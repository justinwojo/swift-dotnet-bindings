// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{

    /// <summary>
    /// Manages a mapping database between Swift types and C# types.
    /// </summary>
    public class TypeDatabase : ITypeDatabase
    {
        private readonly ConcurrentDictionary<string, ModuleTypeDatabase> _modules = new();

        // This store is intended for types which are encountered in one module but should belong to another.
        // This is true for closed generics, where a generic definition is in one module and instantiation is in another.
        private readonly ConcurrentDictionary<SwiftTypeName, TypeRecord> _outOfModuleTypes = new();

        // Pending cross-module records discovered when loading a module's XML before the
        // foreign module that owns them has been loaded. Drained when AddModuleDatabase
        // is called for the foreign module. Keyed by the FOREIGN module name (i.e. the
        // record's SwiftTypeName.Module), values are the records to inject into that
        // foreign module's database once it becomes available. See AddModuleDatabase.
        private readonly ConcurrentDictionary<string, List<(SwiftTypeName Name, TypeRecord Record)>> _pendingCrossModuleRecords = new();

        // Parsed ModuleDecls for framework-dependency modules. The TypeRecord projection that
        // AddModuleDatabase ingests discards constructor/method declarations, but consumer-side
        // emitters (e.g. the KeyPath-init factory emitter) need to walk a dependency class's
        // constructor shapes. Retained here so that information survives past name precomputation.
        private readonly List<ModuleDecl> _dependencyModuleDecls = new();

        // Conformance facts for FOREIGN concrete types (no local TypeDecl in any processed
        // module) against synthesized underscore PATs that swift-api-digester stripped from the
        // ABI JSON — e.g. `Swift.Int : AppIntents._IntentValue`. UnderscoreProtocolSynthesizer
        // parses these from the owning module's swiftinterface extension headers and registers
        // them here; BoundGenericsHandler.SatisfiesConstraint consults the table in its
        // `typeArgumentDecl == null` branch so members typed on closed bound generics like
        // `IntentParameter<Int>` are not skipped. Local conformers (incl. frozen value types)
        // do NOT use this table — their conformance is attached to the local decl and persists
        // via TypeRecord.ProtocolConformances. Keyed by concrete-type module-qualified name →
        // set of protocol module-qualified names. Registration only ever comes from the
        // synthesizer's narrow allowlist, so the exact (concrete, protocol) pair is itself the
        // gate against this becoming a general external-conformance oracle.
        private readonly ConcurrentDictionary<string, HashSet<string>> _strippedForeignConformances =
            new(StringComparer.Ordinal);

        // Module aliases for types that appear under different module names in ABI JSON vs their canonical location.
        // For example, CGSize appears as CoreFoundation.CGSize in ABI JSON but is registered under CoreGraphics.
        private static readonly Dictionary<string, string> _moduleAliases = new()
        {
            { "CoreFoundation", "CoreGraphics" },
        };

        // F10 Stage 19: the modules whose types use the C-interop Foo↔FooRef typedef spelling
        // (CoreFoundation / CoreGraphics). The Ref-suffix alias toggle (GetRefAliasVariant) is
        // scoped to these — both the alias keys and values participate, since a CGImageRef is
        // registered under the CoreGraphics value, not the CoreFoundation key. Declared after
        // _moduleAliases so the set is populated when this initializer runs.
        private static readonly HashSet<string> _refAliasModules =
            new(_moduleAliases.Keys.Concat(_moduleAliases.Values), StringComparer.Ordinal);

        // Cross-module type aliases for Swift typealiases that resolve to types in a different module.
        // Key: the alias's module-qualified name (as it appears in ABI JSON).
        // Value: the canonical module-qualified name of the target type, with generic type arguments
        // preserved (e.g., "ManagedSettings.Token<ManagedSettings.Application>") so the generator
        // can distinguish between different generic instantiations of the same base type.
        // TryGetTypeRecord strips generic args before the TypeRecord lookup; TryResolveTypeAlias
        // returns the full canonical name for code generation that needs the concrete specialization.
        private static readonly Dictionary<string, string> _typeAliases = new(StringComparer.Ordinal)
        {
            { "FamilyControls.ApplicationToken", "ManagedSettings.Token<ManagedSettings.Application>" },
            { "FamilyControls.ActivityCategoryToken", "ManagedSettings.Token<ManagedSettings.ActivityCategory>" },
            { "FamilyControls.WebDomainToken", "ManagedSettings.Token<ManagedSettings.WebDomain>" },
        };

        /// <summary>
        /// Gets the library name for async wrapper functions.
        /// This is where the generated Swift async wrappers are compiled to.
        /// If null, falls back to the module's library path.
        /// </summary>
        public string? AsyncLibraryName { get; set; }

        /// <summary>
        /// Finding 47: once frozen (after the main module is finalized into the database, see
        /// Program.cs), every loaded module's registry is immutable to structural writes and the
        /// database-level <see cref="UpdateTypeRecord"/> / <see cref="RegisterCrossModuleType"/>
        /// paths throw. The only sanctioned post-freeze mutation is <see cref="ApplyEmissionResult"/>,
        /// which stamps emission-discovered facts. This turns "the database's answer depends on when
        /// in the pipeline you ask" into a hard, observable boundary.
        /// </summary>
        private bool _frozen;

        public TypeDatabase()
        {
        }

        /// <inheritdoc/>
        public void Freeze()
        {
            _frozen = true;
            foreach (var moduleDb in _modules.Values)
                moduleDb.Freeze();
        }

        /// <inheritdoc/>
        public void AddDependencyModuleDecl(ModuleDecl moduleDecl)
        {
            if (moduleDecl is not null)
                _dependencyModuleDecls.Add(moduleDecl);
        }

        /// <inheritdoc/>
        public IReadOnlyList<ModuleDecl> GetDependencyModuleDecls() => _dependencyModuleDecls;

        /// <summary>
        /// Loads a module database from a specified file.
        /// </summary>
        /// <param name="file">The file path of the module database to load.</param>
        public async Task LoadModuleDatabaseFromFile(string file, ILogger? logger = null)
        {
            var fileContent = await File.ReadAllTextAsync(file);

            XmlDocument xmlDoc = new();
            xmlDoc.LoadXml(fileContent);
            if (!ValidateXmlSchema(xmlDoc))
                throw new Exception($"Invalid XML schema in {file}.");

            var version = xmlDoc.DocumentElement?.Attributes?["version"]?.Value;
            var moduleDatabase = version switch
            {
                "1.0" => ReadVersion1_0(xmlDoc, logger),
                _ => throw new Exception($"Unsupported database version {version} in {file}.")
            };

            AddModuleDatabase(moduleDatabase);
        }


        /// <summary>
        /// Checks whether a module with the given name has been loaded into the type database.
        /// Used to skip dependency databases that duplicate built-in databases (e.g., Foundation).
        /// </summary>
        /// <param name="moduleName">The module name to check.</param>
        /// <returns><c>true</c> if the module has been loaded; otherwise, <c>false</c>.</returns>
        public bool IsModuleLoaded(string moduleName) => _modules.ContainsKey(moduleName);

        /// <summary>
        /// Adds a module database to the type database. Also re-homes any entity whose
        /// <see cref="SwiftTypeName.Module"/> differs from the database's own name into
        /// the foreign module's database — the cross-module nested type mirror serialized
        /// by <see cref="Emitter.ModuleDatabaseEmitter"/>. Without this routing,
        /// <see cref="TryGetTypeRecordInternal"/> (which keys by <see cref="SwiftTypeName.Module"/>)
        /// would never reach the record because it lives in the wrong module's database.
        /// Records whose foreign module is not loaded yet are queued and drained on the next
        /// matching <see cref="AddModuleDatabase"/> call (dependency-load ordering varies).
        /// </summary>
        /// <param name="moduleDatabase">The module database to add.</param>
        /// <exception cref="Exception">Thrown if a module with the same name already exists in the database.</exception>
        /// <inheritdoc/>
        public void RegisterStrippedConformance(SwiftTypeName concreteType, SwiftTypeName protocolName)
        {
            var set = _strippedForeignConformances.GetOrAdd(
                concreteType.ModuleQualifiedName, _ => new HashSet<string>(StringComparer.Ordinal));
            lock (set)
            {
                set.Add(protocolName.ModuleQualifiedName);
            }
        }

        /// <inheritdoc/>
        public bool HasStrippedConformance(SwiftTypeName concreteType, SwiftTypeName protocolName)
        {
            if (!_strippedForeignConformances.TryGetValue(concreteType.ModuleQualifiedName, out var set))
                return false;
            lock (set)
            {
                return set.Contains(protocolName.ModuleQualifiedName);
            }
        }

        public void AddModuleDatabase(ModuleTypeDatabase moduleDatabase)
        {
            if (!_modules.TryAdd(moduleDatabase.Name, moduleDatabase))
            {
                throw new Exception($"Module {moduleDatabase.Name} already exists in the database.");
            }

            // Drain any cross-module records queued for this module from earlier sibling loads.
            // If the foreign DB already has the type, the existing record's identity fields stay
            // authoritative (e.g. stdlib `Swift.UInt8` registered from SwiftDatabase.xml must not
            // be overwritten by a consumer's parser-side product for an `extension UInt8: SomeProtocol`
            // declaration), but additive ProtocolConformances on the incoming record are merged in
            // so cross-module extension conformances remain verifiable by the CSM filter.
            if (_pendingCrossModuleRecords.TryRemove(moduleDatabase.Name, out var pending))
            {
                foreach (var (name, record) in pending)
                {
                    if (!moduleDatabase.IsTypeProcessed(name))
                        moduleDatabase.Register(name, record, ConflictPolicy.KeepExisting);
                    else
                        MergeAdditiveProtocolConformances(moduleDatabase, name, record);
                }
            }

            // Route this module's cross-module mirror records to their owning foreign module.
            // Same authoritative-existing-record discipline as above: only insert when the foreign
            // DB lacks the type. Consumer modules emit parser-side product records for any stdlib
            // type they extend (e.g. `extension UInt8: MyProtocol` produces a Swift.UInt8 entry in
            // the consumer's DB with the consumer's namespace pattern). Without this guard the
            // re-home overwrites the canonical SwiftDatabase.xml entry, causing emission to fall
            // back to raw Swift names (`Swift.UInt8` instead of `byte`). Additive ProtocolConformances
            // on the incoming record are merged into the canonical entry so the CSM
            // associated-type filter can see `UInt8 : Ext.NeedsByte` for `where S.Element : NeedsByte`.
            //
            // If the foreign module isn't loaded yet, queue for later. The record also stays in
            // this module's own DB as a benign duplicate (never reached via standard lookup,
            // which keys by SwiftTypeName.Module).
            foreach (var kvp in moduleDatabase.GetAllTypeRecords())
            {
                var record = kvp.Value;
                var foreignModule = record.SwiftTypeName.Module;
                if (foreignModule == moduleDatabase.Name)
                    continue;

                if (_modules.TryGetValue(foreignModule, out var foreignDb))
                {
                    if (!foreignDb.IsTypeProcessed(record.SwiftTypeName))
                        foreignDb.Register(record.SwiftTypeName, record, ConflictPolicy.KeepExisting);
                    else
                        MergeAdditiveProtocolConformances(foreignDb, record.SwiftTypeName, record);
                }
                else
                {
                    var queue = _pendingCrossModuleRecords.GetOrAdd(
                        foreignModule, _ => new List<(SwiftTypeName, TypeRecord)>());
                    lock (queue)
                    {
                        queue.Add((record.SwiftTypeName, record));
                    }
                }
            }
        }

        /// <summary>
        /// Merges additive <see cref="TypeRecord.ProtocolConformances"/> from <paramref name="incoming"/>
        /// into the existing canonical record for <paramref name="name"/> without overwriting any
        /// other field. Identity fields (C# type, frozen, kind, etc.) on the canonical record stay
        /// authoritative — the parser-side product record produced by a consumer module's
        /// `extension StdlibType: SomeProtocol` declaration carries only the additive conformance
        /// edge, not the canonical layout/marshalling metadata.
        ///
        /// Conformance list semantics: a populated canonical list is treated as authoritative by
        /// <see cref="ConcreteProtocolSpecializationEmitter"/> (the CSM filter walks it transitively
        /// to verify `S.Element : SomeProtocol` bounds). A null canonical list means "unverifiable"
        /// — the filter fails closed for every protocol, so adopting just the incoming additive list
        /// is a strict improvement (the additive edge becomes verifiable; previously-unverifiable
        /// stdlib edges remain unverifiable, same fail-closed posture as before).
        /// </summary>
        internal static void MergeAdditiveProtocolConformances(
            ModuleTypeDatabase foreignDb,
            SwiftTypeName name,
            TypeRecord incoming)
        {
            if (incoming.ProtocolConformances is not { Count: > 0 } incomingList)
                return;
            if (!foreignDb.TryGetTypeRecord(name, out var existing))
                return;

            var existingList = existing.ProtocolConformances;
            if (existingList is null)
            {
                foreignDb.Register(name, existing with { ProtocolConformances = new List<SwiftTypeName>(incomingList) }, ConflictPolicy.Overwrite);
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in existingList)
                seen.Add(n.ModuleQualifiedName);

            List<SwiftTypeName>? merged = null;
            foreach (var n in incomingList)
            {
                if (!seen.Add(n.ModuleQualifiedName))
                    continue;
                merged ??= new List<SwiftTypeName>(existingList);
                merged.Add(n);
            }

            if (merged is not null)
                foreignDb.Register(name, existing with { ProtocolConformances = merged }, ConflictPolicy.Overwrite);
        }

        /// <summary>
        /// Inserts a type record into a foreign module's in-memory database if loaded.
        /// Used for nested types declared inside `extension ForeignModule.ForeignType {...}`
        /// blocks: the SwiftTypeName lives under <c>ForeignModule</c> (so lookups by Swift
        /// name route to that module's database), but the type is physically owned by — and
        /// emitted from — the current module. Without this hop the entry would only exist in
        /// the current module's database and the standard <see cref="TryGetTypeRecordInternal"/>
        /// path (keyed on <c>swiftTypeName.Module</c>) would fail to resolve it. Silently no-ops
        /// when the foreign module isn't loaded — downstream lookups will fall back through the
        /// alias / Apple-supplement resolution paths exactly as they would have without the type.
        /// </summary>
        public void RegisterCrossModuleType(SwiftTypeName swiftTypeName, TypeRecord record)
        {
            if (_modules.TryGetValue(swiftTypeName.Module, out var moduleDatabase))
            {
                if (!moduleDatabase.IsTypeProcessed(swiftTypeName))
                    moduleDatabase.Register(swiftTypeName, record, ConflictPolicy.KeepExisting);
                else
                    MergeAdditiveProtocolConformances(moduleDatabase, swiftTypeName, record);
            }
        }

        /// <summary>
        /// Validates the XML schema of the provided document.
        /// </summary>
        /// <param name="xmlDoc">The XML document to validate.</param>
        /// <returns>True if the XML schema is valid; otherwise, false.</returns>
        private static bool ValidateXmlSchema(XmlDocument xmlDoc)
        {
            if (xmlDoc == null)
                return false;

            if (xmlDoc?.DocumentElement?.Name != "swifttypedatabase")
                return false;

            if (xmlDoc.DocumentElement.Attributes["version"]?.Value != "1.0")
                return false;

            XmlNode? entitiesNode = xmlDoc?.SelectSingleNode("//swifttypedatabase/entities");
            if (entitiesNode == null)
                return false;

            if (entitiesNode.ChildNodes.Count == 0)
                return false;

            foreach (XmlNode entityNode in entitiesNode.ChildNodes)
            {
                // Skip non-element nodes (comments, whitespace, etc.)
                if (entityNode.NodeType != XmlNodeType.Element)
                    continue;

                if (entityNode.Name != "entity")
                    return false;

                XmlNode? typeDeclarationNode = entityNode?.SelectSingleNode("typedeclaration");
                if (typeDeclarationNode == null)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Reads and parses the XML document containing type mappings based on the version 1.0.
        /// </summary>
        /// <param name="xmlDoc">The XML document to read.</param>
        /// <returns>The module database.</returns>
        private static ModuleTypeDatabase ReadVersion1_0(XmlDocument xmlDoc, ILogger? logger = null)
        {
            XmlNode? rootNode = xmlDoc.SelectSingleNode("//swifttypedatabase");
            if (rootNode == null)
                throw new Exception("Invalid XML structure: 'swifttypedatabase' node not found.");

            var databaseModuleName = rootNode.Attributes?["moduleName"]?.Value ?? throw new Exception("Invalid XML structure: Missing 'moduleName' attribute.");
            var databaseModulePath = rootNode.Attributes?["modulePath"]?.Value ?? throw new Exception("Invalid XML structure: Missing 'modulePath' attribute.");

            var moduleDatabase = new ModuleTypeDatabase(databaseModuleName, databaseModulePath, logger);

            XmlNode? entitiesNode = xmlDoc.SelectSingleNode("//swifttypedatabase/entities");

            if (entitiesNode == null)
                throw new Exception("Invalid XML structure: 'entities' node not found.");

            foreach (XmlNode? entityNode in entitiesNode.ChildNodes)
            {
                // Skip non-element nodes (comments, whitespace, etc.)
                if (entityNode?.NodeType != XmlNodeType.Element)
                    continue;

                XmlNode? typeDeclarationNode = entityNode?.SelectSingleNode("typedeclaration");
                if (typeDeclarationNode == null)
                    throw new Exception("Invalid XML structure: 'typedeclaration' node not found.");

                string moduleName = typeDeclarationNode?.Attributes?["module"]?.Value ?? throw new Exception("Invalid XML structure: Missing 'module' attribute.");
                string swiftTypeIdentifier = typeDeclarationNode?.Attributes?["name"]?.Value ?? throw new Exception("Invalid XML structure: Missing 'name' attribute.");
                string swiftMangledName = typeDeclarationNode?.Attributes?["mangledName"]?.Value ?? string.Empty;
                string csharpTypeIdentifier = entityNode?.Attributes?["managedTypeName"]?.Value ?? throw new Exception("Invalid XML structure: Missing 'managedTypeName' attribute.");
                if (entityNode?.Attributes?["managedNameSpace"] is null)
                    throw new Exception("Invalid XML structure: Missing 'managedNameSpace' attribute.");
                string @namespace = entityNode!.Attributes!["managedNameSpace"]!.Value;
                string frozen = typeDeclarationNode?.Attributes?["frozen"]?.Value ?? throw new Exception("Invalid XML structure: Missing 'frozen' attribute.");
                string requiresMemoryManagement = typeDeclarationNode?.Attributes?["requiresMemoryManagement"]?.Value ?? throw new Exception("Invalid XML structure: Missing 'requiresMemoryManagement' attribute.");
                string objcBridged = typeDeclarationNode?.Attributes?["objcBridged"]?.Value ?? "false";
                string kindStr = typeDeclarationNode?.Attributes?["kind"]?.Value ?? "struct";
                string? nativeType = typeDeclarationNode?.Attributes?["nativeType"]?.Value;
                string hasAssociatedTypes = typeDeclarationNode?.Attributes?["hasAssociatedTypes"]?.Value ?? "false";
                string hasSelfRequirement = typeDeclarationNode?.Attributes?["hasSelfRequirement"]?.Value ?? "false";
                string simpleEnum = typeDeclarationNode?.Attributes?["simpleEnum"]?.Value ?? "false";
                string optionSet = typeDeclarationNode?.Attributes?["optionSet"]?.Value ?? "false";
                string inheritedRequirementsOnly = typeDeclarationNode?.Attributes?["inheritedRequirementsOnly"]?.Value ?? "false";
                string classBound = typeDeclarationNode?.Attributes?["classBound"]?.Value ?? "false";
                string objcRooted = typeDeclarationNode?.Attributes?["objcRooted"]?.Value ?? "false";
                string objcProtocol = typeDeclarationNode?.Attributes?["objcProtocol"]?.Value ?? "false";
                string hasMethodSelfTypeParams = typeDeclarationNode?.Attributes?["hasMethodSelfTypeParams"]?.Value ?? "false";
                string nonCopyable = typeDeclarationNode?.Attributes?["nonCopyable"]?.Value ?? "false";
                string hasFloatFields = typeDeclarationNode?.Attributes?["hasFloatFields"]?.Value ?? "false";
                string hasBoolFields = typeDeclarationNode?.Attributes?["hasBoolFields"]?.Value ?? "false";
                string objcBridgeable = typeDeclarationNode?.Attributes?["objcBridgeable"]?.Value ?? "false";
                string? rawValueType = typeDeclarationNode?.Attributes?["rawValueType"]?.Value;
                string? emittedMemberCountStr = typeDeclarationNode?.Attributes?["emittedMemberCount"]?.Value;
                int? emittedMemberCount = emittedMemberCountStr != null ? int.Parse(emittedMemberCountStr) : null;
                // Optional — only present on protocol records produced by 0.10.0+ generators.
                // Null on legacy databases; the constrained-existential consumer falls back
                // to AnyType when it can't verify the protocol's interface arity.
                string? associatedTypeCountStr = typeDeclarationNode?.Attributes?["associatedTypeCount"]?.Value;
                int? associatedTypeCount = associatedTypeCountStr != null ? int.Parse(associatedTypeCountStr) : null;
                string? superclassStr = typeDeclarationNode?.Attributes?["superclass"]?.Value;
                // Whether the class body emitted PInvoke_getMetadata (Class kind only).
                // Absent on legacy databases that predate this field — keep as null so the
                // downstream consumer's HasMetadataPInvokeInResolvedAncestors falls back to
                // pre-fix behavior (assume yes), preserving compile against old NuGets.
                string? emittedMetadataPInvokeStr = typeDeclarationNode?.Attributes?["emittedMetadataPInvoke"]?.Value;
                bool? emittedMetadataPInvoke = emittedMetadataPInvokeStr == null
                    ? (bool?)null
                    : emittedMetadataPInvokeStr.Equals("true", StringComparison.OrdinalIgnoreCase);
                string? inlineSizeStr = typeDeclarationNode?.Attributes?["inlineSize"]?.Value;
                int? inlineSize = inlineSizeStr != null ? int.Parse(inlineSizeStr) : null;
                string? abiFieldLayout = typeDeclarationNode?.Attributes?["abiLayout"]?.Value;
                string? protocolDescriptorSymbol = typeDeclarationNode?.Attributes?["protocolDescriptorSymbol"]?.Value;
                string? protocolConformancesAttr = typeDeclarationNode?.Attributes?["protocolConformances"]?.Value;
                IReadOnlyList<SwiftTypeName>? protocolConformances = null;
                if (!string.IsNullOrEmpty(protocolConformancesAttr))
                {
                    var entries = protocolConformancesAttr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    var list = new List<SwiftTypeName>(entries.Length);
                    foreach (var entry in entries)
                    {
                        if (entry.Contains('<'))
                            continue;
                        try
                        {
                            list.Add(SwiftTypeName.FromModuleQualifiedName(entry));
                        }
                        catch (ArgumentException)
                        {
                            // Skip malformed entries — older databases may carry odd shapes.
                        }
                    }
                    protocolConformances = list;
                }
                if (swiftTypeIdentifier == null || csharpTypeIdentifier == null)
                    throw new Exception("Invalid XML structure: Missing attributes.");

                // Emitted class instance methods (Class kind only). See ModuleDatabaseEmitter
                // and WrapperEmitter.HasMethodInResolvedAncestors for the cross-module override
                // verification this enables. Older databases predate this element — null means
                // "unverifiable", and the cross-module fallback retains its prior trust-the-Swift-bit
                // behavior so legacy XMLs continue to work.
                IReadOnlyList<EmittedClassMethod>? emittedClassMethods = null;
                XmlNode? emittedMethodsNode = typeDeclarationNode?.SelectSingleNode("emittedMethods");
                if (emittedMethodsNode != null)
                {
                    var list = new List<EmittedClassMethod>();
                    foreach (XmlNode? methodNode in emittedMethodsNode.ChildNodes)
                    {
                        if (methodNode?.NodeType != XmlNodeType.Element) continue;
                        if (methodNode.Name != "method") continue;
                        var swiftName = methodNode.Attributes?["swiftName"]?.Value;
                        if (string.IsNullOrEmpty(swiftName)) continue;
                        // csharpName persists the post-NameProvider C# name so the verifier can
                        // compare names without recomputing renames it can't see. Empty (missing
                        // attribute on a legacy database that predates this field) means the
                        // verifier should skip the C# name check — see CrossModuleAncestorHasMethod.
                        var csharpName = methodNode.Attributes?["csharpName"]?.Value ?? string.Empty;
                        var paramTypesAttr = methodNode.Attributes?["paramTypes"]?.Value ?? string.Empty;
                        var paramTypes = paramTypesAttr.Length == 0
                            ? Array.Empty<string>()
                            : paramTypesAttr.Split('|');
                        list.Add(new EmittedClassMethod(swiftName, csharpName, paramTypes));
                    }
                    emittedClassMethods = list;
                }

                // Per-type @available annotations. Optional element — null on legacy databases
                // that predate this field, in which case the cross-module availability merge
                // falls back to parent-only behavior (the dependency type is treated as
                // always-available, preserving prior behavior).
                IReadOnlyList<AvailabilityAnnotation>? availabilityAnnotations = null;
                XmlNode? availabilityNode = typeDeclarationNode?.SelectSingleNode("availability");
                if (availabilityNode != null)
                {
                    var list = new List<AvailabilityAnnotation>();
                    foreach (XmlNode? annNode in availabilityNode.ChildNodes)
                    {
                        if (annNode?.NodeType != XmlNodeType.Element) continue;
                        if (annNode.Name != "annotation") continue;
                        var platform = annNode.Attributes?["platform"]?.Value;
                        var introduced = annNode.Attributes?["introduced"]?.Value;
                        var deprecated = annNode.Attributes?["deprecated"]?.Value;
                        var obsoleted = annNode.Attributes?["obsoleted"]?.Value;
                        var isUnconditionallyDeprecated = string.Equals(
                            annNode.Attributes?["unconditionallyDeprecated"]?.Value, "true",
                            StringComparison.OrdinalIgnoreCase);
                        var isUnconditionallyUnavailable = string.Equals(
                            annNode.Attributes?["unavailable"]?.Value, "true",
                            StringComparison.OrdinalIgnoreCase);
                        var message = annNode.Attributes?["message"]?.Value;
                        var renamed = annNode.Attributes?["renamed"]?.Value;
                        list.Add(new AvailabilityAnnotation(
                            platform, introduced, deprecated, obsoleted,
                            isUnconditionallyDeprecated, isUnconditionallyUnavailable,
                            message, renamed));
                    }
                    if (list.Count > 0)
                        availabilityAnnotations = list;
                }

                var swiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{swiftTypeIdentifier}");
                var csharpTypeName = string.IsNullOrEmpty(@namespace)
                    ? CSharpTypeName.FromKeyword(csharpTypeIdentifier)
                    : CSharpTypeName.FromNamespaceAndName(@namespace, csharpTypeIdentifier);

                // Parse native type name if specified (e.g., "Foundation.NSUrl" for URL)
                CSharpTypeName? nativeTypeName = null;
                if (!string.IsNullOrEmpty(nativeType))
                {
                    var lastDot = nativeType.LastIndexOf('.');
                    if (lastDot > 0)
                    {
                        var nativeNamespace = nativeType.Substring(0, lastDot);
                        var nativeTypePart = nativeType.Substring(lastDot + 1);
                        nativeTypeName = CSharpTypeName.FromNamespaceAndName(nativeNamespace, nativeTypePart);
                    }
                }

                var typeRecord = new TypeRecord()
                {
                    CSharpTypeName = csharpTypeName,
                    SwiftTypeName = swiftTypeName,
                    MetadataAccessor = swiftMangledName,
                    Flags = (frozen.ToLower() == "true" ? TypeRecordFlags.Frozen : TypeRecordFlags.None) |
                            (requiresMemoryManagement.ToLower() == "true" ? TypeRecordFlags.RequiresMemoryManagement : TypeRecordFlags.None) |
                            (objcBridged.ToLower() == "true" ? TypeRecordFlags.ObjCBridged : TypeRecordFlags.None) |
                            (hasAssociatedTypes.ToLower() == "true" ? TypeRecordFlags.HasAssociatedTypes : TypeRecordFlags.None) |
                            (hasSelfRequirement.ToLower() == "true" ? TypeRecordFlags.HasSelfRequirement : TypeRecordFlags.None) |
                            (simpleEnum.ToLower() == "true" ? TypeRecordFlags.SimpleEnum : TypeRecordFlags.None) |
                            (optionSet.ToLower() == "true" ? TypeRecordFlags.OptionSet : TypeRecordFlags.None) |
                            (inheritedRequirementsOnly.ToLower() == "true" ? TypeRecordFlags.InheritedRequirementsOnly : TypeRecordFlags.None) |
                            (classBound.ToLower() == "true" ? TypeRecordFlags.ClassBound : TypeRecordFlags.None) |
                            (objcRooted.ToLower() == "true" ? TypeRecordFlags.ObjCRooted : TypeRecordFlags.None) |
                            (objcProtocol.ToLower() == "true" ? TypeRecordFlags.ObjCProtocol : TypeRecordFlags.None) |
                            (hasMethodSelfTypeParams.ToLower() == "true" ? TypeRecordFlags.HasMethodSelfTypeParams : TypeRecordFlags.None) |
                            (nonCopyable.ToLower() == "true" ? TypeRecordFlags.NonCopyable : TypeRecordFlags.None) |
                            (hasFloatFields.ToLower() == "true" ? TypeRecordFlags.HasFloatFields : TypeRecordFlags.None) |
                            (hasBoolFields.ToLower() == "true" ? TypeRecordFlags.HasBoolFields : TypeRecordFlags.None) |
                            (objcBridgeable.ToLower() == "true" ? TypeRecordFlags.ObjCBridgeable : TypeRecordFlags.None),
                    Kind = kindStr.ToLower() switch
                    {
                        "class" => TypeRecordKind.Class,
                        "enum" => TypeRecordKind.Enum,
                        "protocol" => TypeRecordKind.Protocol,
                        "existential" => TypeRecordKind.Existential,
                        _ => TypeRecordKind.Struct,
                    },
                    NativeTypeName = nativeTypeName,
                    RawValueTypeName = rawValueType,
                    EmittedMemberCount = emittedMemberCount,
                    AssociatedTypeCount = associatedTypeCount,
                    SuperclassTypeName = superclassStr != null && !superclassStr.Contains('<')
                        ? SwiftTypeName.FromModuleQualifiedName(superclassStr)
                        : null,
                    InlineSize = inlineSize,
                    AbiFieldLayout = abiFieldLayout,
                    ProtocolDescriptorSymbol = protocolDescriptorSymbol,
                    ProtocolConformances = protocolConformances,
                    EmittedClassMethods = emittedClassMethods,
                    EmittedMetadataPInvoke = emittedMetadataPInvoke,
                    AvailabilityAnnotations = availabilityAnnotations,
                };

                moduleDatabase.Register(swiftTypeName, typeRecord, ConflictPolicy.Overwrite);
            }

            // Suppressed proxy class names — populated when a previously generated module
            // suppressed a proxy (UnsatisfiedProtocolConstraint, StaticMethodRequirements,
            // HasSelfRequirement, etc.). The downstream module needs this so its emitter
            // can drop method bodies at emission that reference the cross-module qualified
            // proxy form (`{Namespace}.SwiftInterop.{ProxyName}`) emitted by the umbrella-aware
            // existential marshaler. Element is optional — older databases predate it.
            XmlNode? suppressedProxiesNode = xmlDoc.SelectSingleNode("//swifttypedatabase/suppressedProxies");
            if (suppressedProxiesNode != null)
            {
                // Optional namespace attribute — older databases predate it. Default to the
                // Swift module name, which matches the default-pattern equivalence (and is
                // what the previous schema implicitly assumed).
                var nsAttr = suppressedProxiesNode.Attributes?["namespace"]?.Value;
                moduleDatabase.SuppressedProxyNamespace = string.IsNullOrEmpty(nsAttr)
                    ? databaseModuleName
                    : nsAttr;

                foreach (XmlNode? proxyNode in suppressedProxiesNode.ChildNodes)
                {
                    if (proxyNode?.NodeType != XmlNodeType.Element) continue;
                    if (proxyNode.Name != "proxy") continue;
                    var proxyName = proxyNode.Attributes?["name"]?.Value;
                    if (string.IsNullOrEmpty(proxyName)) continue;
                    moduleDatabase.RegisterSuppressedProxyClassName(proxyName);
                }
            }

            return moduleDatabase;
        }

        /// <inheritdoc/>
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            // Arm 1 — SwiftBindings.Apple supplement wins over local module databases for any
            // identity it owns — both cross-module references (Foundation.Locale.Language
            // from Translation) AND same-module references within an Apple framework
            // package (CryptoKit.P256.Signing.ECDSASignature from CryptoKit bindings).
            // Running the resolver FIRST for supplement-owned types keeps framework
            // packages deferring to the supplement's canonical projection instead of
            // re-emitting a parallel local class. The resolver short-circuits to false
            // when the identity is not in the manifest, so non-supplement types fall
            // through to the normal module-database lookup untouched.
            //
            // INVARIANT: currentlyGeneratingModule is always null on this path. The
            // main generator never rebuilds the supplement through TryGetTypeRecord —
            // supplement regeneration uses the dedicated AppleTypesCsEmitter pipeline,
            // which never flows through this helper. The NamedTypeSpec resolver
            // (AppleSupplementStrategy) carries the same null today via
            // ResolutionContext.CurrentlyGeneratingModule, so behavior matches; if a
            // future caller starts threading a real module name in, both surfaces need
            // to honor it for the TypeOwnerRegistry Level-5 (Local) fall-through to
            // stay consistent.
            //
            // Finding 10: the supplement arm is split out so the resolver leg
            // (DatabaseLookupStrategy) can run Arms 2–6 alone via
            // TryGetTypeRecordWithoutSupplement — its AppleSupplementStrategy already
            // consulted the supplement at a higher precedence, so re-consulting it here
            // was a redundant double-consult. Raw SwiftTypeName callers still get the
            // supplement-first ordering by calling this method.
            if (TryResolveAppleSupplementArm(swiftTypeName, out record))
                return true;

            return TryGetTypeRecordWithoutSupplement(swiftTypeName, out record);
        }

        /// <summary>
        /// Finding 10: Arm 1 of the cascade in isolation — the Apple-supplement consult (with its
        /// reference recording). Factored out so <see cref="TryGetTypeRecord"/> and the
        /// resolver-facing <see cref="TryGetTypeRecordWithoutSupplement"/> share one definition of
        /// "is this a supplement-owned identity" instead of duplicating it.
        /// </summary>
        private static bool TryResolveAppleSupplementArm(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            if (AppleSupplementResolver.TryResolve(swiftTypeName, currentlyGeneratingModule: null, out var supplementRecord))
            {
                AppleSupplementReferences.Record(swiftTypeName.ModuleQualifiedName, "TypeDatabase.TryGetTypeRecord:AppleSupplementResolver");
                record = supplementRecord;
                return true;
            }

            record = null;
            return false;
        }

        /// <inheritdoc/>
        public bool TryGetTypeRecordWithoutSupplement(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            // F10 Stage 18: arms 2–6 (direct/alias/umbrella lookup, Ref-suffix variant,
            // out-of-module cache, cross-module typealias, Swift.Error) now live in
            // TypeResolver.DatabaseCascade — the single source of truth this raw-name path
            // shares with the NamedTypeSpec resolver chain. Run that cascade, and ONLY that
            // cascade, so raw-SwiftTypeName callers get exactly arms 2–6 and never strategies
            // 1–11. The cascade strategies call this database's arm primitives directly
            // (TryGetTypeRecordInternal / GetRefAliasVariant / TryGetOutOfModuleType /
            // TryResolveTypeAlias), never back into this adapter, so there is no recursion.
            //
            // The ModuleQualifiedName round-trips losslessly through NamedTypeSpec →
            // SwiftTypeName.FromTypeSpec for every reachable (≥2-segment) name, reproducing a
            // record-equal SwiftTypeName, so each arm keys on the same module/name/dict key as
            // the retired inline cascade.
            var spec = new NamedTypeSpec(swiftTypeName.ModuleQualifiedName);
            var context = new ResolutionContext(this);
            foreach (var strategy in TypeResolver.DatabaseCascade)
            {
                if (strategy.TryResolve(spec, context, out var result) && result.Record is not null)
                {
                    record = result.Record;
                    return true;
                }
            }

            record = null;
            return false;
        }

        /// <summary>
        /// Resolves a cross-module type alias to its full canonical name, preserving generic
        /// type arguments. Returns null if the type is not an alias.
        /// E.g., "FamilyControls.ApplicationToken" → "ManagedSettings.Token&lt;ManagedSettings.Application&gt;"
        /// </summary>
        public string? TryResolveTypeAlias(SwiftTypeName swiftTypeName)
        {
            return _typeAliases.TryGetValue(swiftTypeName.ModuleQualifiedName, out var canonicalName)
                ? canonicalName
                : null;
        }

        /// <inheritdoc/>
        public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record)
        {
            // Finding 47: a full-record overwrite is a structural write — forbidden once the
            // registry is frozen. The module-DB path below would already throw via
            // ModuleTypeDatabase.Register's own guard, but the out-of-module fallback has no
            // module to guard it, so gate both on the database-level freeze here.
            if (_frozen)
            {
                throw new InvalidOperationException(
                    $"SWIFTBIND045: type registry is frozen; cannot UpdateTypeRecord "
                    + $"'{name.ModuleQualifiedName}' after the freeze point. Post-freeze, only "
                    + "ApplyEmissionResult may mutate records (emission-discovered facts only).");
            }

            var moduleName = name.Module;
            if (_modules.TryGetValue(moduleName, out var moduleDb))
            {
                moduleDb.Register(name, record, ConflictPolicy.Overwrite);
                return;
            }
            // Fall back to out-of-module types
            _outOfModuleTypes.AddOrUpdate(name, record, (_, _) => record);
        }

        /// <inheritdoc/>
        public void ApplyEmissionResult(SwiftTypeName name, TypeEmissionResult result)
        {
            // Finding 47: the sole sanctioned post-freeze mutation. Stamps the emission-discovered
            // facts onto the already-registered record (in its owning module DB, or the
            // out-of-module store) without touching any structural field, and bypasses the freeze
            // guard by routing through ModuleTypeDatabase.ApplyEmissionUpdate. Only refines an
            // existing record — emission never introduces a new type identity — so when no base
            // record is found there is nothing to stamp and the call is a no-op.
            if (_modules.TryGetValue(name.Module, out var moduleDb)
                && moduleDb.TryGetTypeRecord(name, out var existing))
            {
                EmissionAttempt.Current?.Journal.Capture(name, existing);
                moduleDb.ApplyEmissionUpdate(name, result.ApplyTo(existing));
                return;
            }

            if (_outOfModuleTypes.TryGetValue(name, out var existingOutOfModule))
            {
                EmissionAttempt.Current?.Journal.Capture(name, existingOutOfModule);
                _outOfModuleTypes[name] = result.ApplyTo(existingOutOfModule);
            }
        }

        /// <inheritdoc/>
        public void RestoreEmissionRecord(SwiftTypeName name, TypeRecord record)
        {
            // Mirrors ApplyEmissionResult's two stores exactly, so a restore can never land somewhere
            // the forward stamp did not. Writes the record whole rather than merging: the point is to
            // reinstate the pre-attempt state, including fields the discarded attempt overwrote.
            if (_modules.TryGetValue(name.Module, out var moduleDb)
                && moduleDb.TryGetTypeRecord(name, out _))
            {
                moduleDb.ApplyEmissionUpdate(name, record);
                return;
            }

            if (_outOfModuleTypes.ContainsKey(name))
            {
                _outOfModuleTypes[name] = record;
            }
        }

        /// <summary>
        /// Determines whether the specified module has been processed.
        /// </summary>
        /// <param name="moduleName">The Swift module name.</param>
        /// <returns><c>true</c> if the module has been processed; otherwise, <c>false</c>.</returns>
        public bool IsModuleProcessed(string moduleName)
        {
            return _modules.ContainsKey(moduleName);
        }

        /// <inheritdoc/>
        public bool IsTypeProcessed(SwiftTypeName swiftTypeName)
        {
            // Finding 10: defined as "TryGetTypeRecord succeeds". The historical body was a 3-arm
            // subset (module DB + Ref-variant + type-alias) that silently disagreed with
            // TryGetTypeRecord on supplement-owned, out-of-module, and Swift.Error identities —
            // the divergence the finding exists to retire. Callers that need the narrower
            // "registered in a loaded database" question (the parser's duplicate gate and
            // metadata-accessor choice) use IsTypeRegistered, which keeps the old narrow body.
            return TryGetTypeRecord(swiftTypeName, out _);
        }

        /// <inheritdoc/>
        public bool IsTypeRegistered(SwiftTypeName swiftTypeName)
        {
            // The narrow, side-effect-free registration predicate (former IsTypeProcessed body):
            // module DB / module-alias / umbrella (+ Ref-variant + type-alias) only — no supplement
            // arm, no out-of-module, no Swift.Error, and no AppleSupplementReferences.Record. The
            // parser asks this question to decide whether a type was ALREADY registered by a loaded
            // dependency; a supplement-owned same-module type must answer "no" here so the parser
            // emits it rather than throwing a spurious "already processed" duplicate.
            if (IsTypeProcessedInternal(swiftTypeName))
                return true;

            var refVariant = GetRefAliasVariant(swiftTypeName);
            if (refVariant != null && IsTypeProcessedInternal(refVariant))
                return true;

            // Cross-module type aliases — strip generic args for base type lookup
            if (_typeAliases.TryGetValue(swiftTypeName.ModuleQualifiedName, out var canonicalName))
            {
                var baseName = canonicalName.IndexOf('<') is var idx and >= 0
                    ? canonicalName[..idx]
                    : canonicalName;
                var canonicalTypeName = SwiftTypeName.FromModuleQualifiedName(baseName);
                if (IsTypeProcessedInternal(canonicalTypeName))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Retrieves the library path for the specified module.
        /// </summary>
        /// <param name="moduleName">The name of the module.</param>
        /// <returns>The file path of the library associated with the module.</returns>
        /// <exception cref="Exception">Thrown if the library path does not exist for the specified module.</exception>
        public string GetLibraryPath(string moduleName)
        {
            if (!_modules.TryGetValue(moduleName, out var moduleDatabase))
            {
                throw new Exception($"Module {moduleName} does not exist in the database.");
            }

            return moduleDatabase.Path;
        }

        /// <summary>
        /// Populates the out-of-module types store with the specified types.
        /// </summary>
        /// <param name="types">The types to add.</param>
        public void AddOutOfModuleTypes(IEnumerable<(SwiftTypeName identifier, TypeRecord record)> types)
        {
            foreach (var (identifier, record) in types)
            {
                _outOfModuleTypes.TryAdd(identifier, record);
            }
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<(string Namespace, string ProxyName)> GetCrossModuleSuppressedProxyClassNames()
        {
            List<(string, string)>? pairs = null;
            foreach (var moduleDb in _modules.Values)
            {
                if (moduleDb.SuppressedProxyClassNames.Count == 0)
                    continue;
                // Namespace falls back to the Swift module name when the database predates
                // the namespace attribute — matches the historical default-pattern shape.
                var ns = string.IsNullOrEmpty(moduleDb.SuppressedProxyNamespace)
                    ? moduleDb.Name
                    : moduleDb.SuppressedProxyNamespace!;
                pairs ??= new List<(string, string)>();
                foreach (var name in moduleDb.SuppressedProxyClassNames)
                    pairs.Add((ns, name));
            }
            return (IReadOnlyCollection<(string, string)>?)pairs ?? Array.Empty<(string, string)>();
        }

        // F10 Stage 17: arm-2 primitive (direct module DB + CoreFoundation→CoreGraphics module
        // alias + compileImportModule umbrella). Promoted from private to internal so the
        // resolver-cascade strategies (DatabaseLookupStrategy arm 2, CrossModuleAliasStrategy
        // arm 5's canonical-base lookup) can reuse this single source of truth without
        // re-entering TryGetTypeRecordWithoutSupplement. It is non-recursive — it never calls
        // back into the cascade — which is what keeps the Stage 18 collapse recursion-free.
        internal bool TryGetTypeRecordInternal(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            if (_modules.TryGetValue(swiftTypeName.Module, out var moduleDatabase))
            {
                if (moduleDatabase.TryGetTypeRecord(swiftTypeName, out record))
                    return true;
            }

            // Try module alias (e.g., CoreFoundation -> CoreGraphics)
            if (_moduleAliases.TryGetValue(swiftTypeName.Module, out var aliasedModule))
            {
                if (_modules.TryGetValue(aliasedModule, out moduleDatabase))
                {
                    // Preserve full nested name while remapping only the root module segment.
                    var aliasedQualifiedName = $"{aliasedModule}.{swiftTypeName.ModuleQualifiedName[(swiftTypeName.Module.Length + 1)..]}";
                    var aliasedTypeName = SwiftTypeName.FromModuleQualifiedName(aliasedQualifiedName);
                    if (moduleDatabase.TryGetTypeRecord(aliasedTypeName, out record))
                        return true;
                }
            }

            // Apple `@_implementationOnly` umbrella fallback: a type qualified with the
            // umbrella module name (e.g., RealityKit.Entity) may actually be declared in
            // a source module that re-exports through it (RealityFoundation). The
            // `compileImportModule` declaration in apple-frameworks.json registers each
            // source→umbrella relationship; consult the reverse map and probe each source
            // module's database for the type. Without this, the cross-module recursion in
            // TypeProjectionFactory drops Optional<RealityKit.Entity> to null and emission
            // falls back to the raw bound-generic shape (Swift.SwiftOptional<IntPtr>).
            var sourceModules = AppleFrameworkRegistry.GetCompileImportSourceModules(swiftTypeName.Module);
            if (sourceModules.Count > 0)
            {
                foreach (var sourceModule in sourceModules)
                {
                    if (!_modules.TryGetValue(sourceModule, out moduleDatabase))
                        continue;
                    var rewrittenQualifiedName = $"{sourceModule}.{swiftTypeName.ModuleQualifiedName[(swiftTypeName.Module.Length + 1)..]}";
                    var rewrittenTypeName = SwiftTypeName.FromModuleQualifiedName(rewrittenQualifiedName);
                    if (moduleDatabase.TryGetTypeRecord(rewrittenTypeName, out record))
                        return true;
                }
            }

            record = null;
            return false;
        }

        // F10 Stage 17: arm-4 primitive (out-of-module type cache) exposed to
        // OutOfModuleLookupStrategy so the cascade arm has a single source of truth shared
        // with the inline TryGetTypeRecordWithoutSupplement path. Pure dictionary probe — no
        // recursion, no fall-through.
        internal bool TryGetOutOfModuleType(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
            => _outOfModuleTypes.TryGetValue(swiftTypeName, out record);

        private bool IsTypeProcessedInternal(SwiftTypeName swiftTypeName)
        {
            // Mirror TryGetTypeRecordInternal: each lookup path (direct module / module alias /
            // umbrella source-module) returns ONLY on a positive hit, then falls through. A direct
            // module-name match without the type record (e.g., RealityKit is loaded but Entity is
            // declared in RealityFoundation under @_implementationOnly) must still consult the
            // compileImportModule reverse map; otherwise IsTypeProcessed disagrees with
            // TryGetTypeRecord and downstream emitters get inconsistent answers.
            if (_modules.TryGetValue(swiftTypeName.Module, out var moduleDatabase))
            {
                if (moduleDatabase.IsTypeProcessed(swiftTypeName))
                    return true;
            }

            // Try module alias (e.g., CoreFoundation -> CoreGraphics)
            if (_moduleAliases.TryGetValue(swiftTypeName.Module, out var aliasedModule))
            {
                if (_modules.TryGetValue(aliasedModule, out moduleDatabase))
                {
                    var aliasedQualifiedName = $"{aliasedModule}.{swiftTypeName.ModuleQualifiedName[(swiftTypeName.Module.Length + 1)..]}";
                    var aliasedTypeName = SwiftTypeName.FromModuleQualifiedName(aliasedQualifiedName);
                    if (moduleDatabase.IsTypeProcessed(aliasedTypeName))
                        return true;
                }
            }

            // Apple `@_implementationOnly` umbrella fallback — see TryGetTypeRecordInternal
            // for the rationale. Mirrors the same source-module probe so processed-state
            // queries keyed on the umbrella name (e.g., RealityKit.Entity) resolve to the
            // source module's record (RealityFoundation.Entity).
            var sourceModules = AppleFrameworkRegistry.GetCompileImportSourceModules(swiftTypeName.Module);
            if (sourceModules.Count > 0)
            {
                foreach (var sourceModule in sourceModules)
                {
                    if (!_modules.TryGetValue(sourceModule, out moduleDatabase))
                        continue;
                    var rewrittenQualifiedName = $"{sourceModule}.{swiftTypeName.ModuleQualifiedName[(swiftTypeName.Module.Length + 1)..]}";
                    var rewrittenTypeName = SwiftTypeName.FromModuleQualifiedName(rewrittenQualifiedName);
                    if (moduleDatabase.IsTypeProcessed(rewrittenTypeName))
                        return true;
                }
            }

            return false;
        }

        // F10 Stage 18: arm-3 primitive (C-interop Foo↔FooRef suffix toggle). Promoted to
        // internal so DatabaseLookupStrategy can run arm 3 against the same definition the
        // raw-name cascade and IsTypeRegistered use. Still a pure name transform — no lookup,
        // no recursion.
        //
        // F10 Stage 19: scoped to the CoreFoundation/CoreGraphics family. The Foo/FooRef
        // spelling toggle is a C-interop typedef convention that only those modules use; an
        // arbitrary module's "…Ref"-suffixed type is a distinct real identity, not an alias of
        // a sibling. Returning null for non-family modules stops both arm 3 and IsTypeRegistered
        // from synthesizing a bogus sibling lookup — both call sites already guard the null.
        internal static SwiftTypeName? GetRefAliasVariant(SwiftTypeName swiftTypeName)
        {
            if (!_refAliasModules.Contains(swiftTypeName.Module))
                return null;

            var fullName = swiftTypeName.ModuleQualifiedName;
            if (fullName.EndsWith("Ref", StringComparison.Ordinal))
            {
                return SwiftTypeName.FromModuleQualifiedName(fullName[..^3]);
            }

            return SwiftTypeName.FromModuleQualifiedName($"{fullName}Ref");
        }
    }
}
