// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Xml;

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

        // Module aliases for types that appear under different module names in ABI JSON vs their canonical location.
        // For example, CGSize appears as CoreFoundation.CGSize in ABI JSON but is registered under CoreGraphics.
        private static readonly Dictionary<string, string> _moduleAliases = new()
        {
            { "CoreFoundation", "CoreGraphics" },
        };

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

        public TypeDatabase()
        {
        }

        /// <summary>
        /// Loads a module database from a specified file.
        /// </summary>
        /// <param name="file">The file path of the module database to load.</param>
        public async Task LoadModuleDatabaseFromFile(string file)
        {
            var fileContent = await File.ReadAllTextAsync(file);

            XmlDocument xmlDoc = new();
            xmlDoc.LoadXml(fileContent);
            if (!ValidateXmlSchema(xmlDoc))
                throw new Exception($"Invalid XML schema in {file}.");

            var version = xmlDoc.DocumentElement?.Attributes?["version"]?.Value;
            var moduleDatabase = version switch
            {
                "1.0" => ReadVersion1_0(xmlDoc),
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
        /// Adds a module database to the type database.
        /// </summary>
        /// <param name="moduleDatabase">The module database to add.</param>
        /// <exception cref="Exception">Thrown if a module with the same name already exists in the database.</exception>
        public void AddModuleDatabase(ModuleTypeDatabase moduleDatabase)
        {
            if (!_modules.TryAdd(moduleDatabase.Name, moduleDatabase))
            {
                throw new Exception($"Module {moduleDatabase.Name} already exists in the database.");
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
        private static ModuleTypeDatabase ReadVersion1_0(XmlDocument xmlDoc)
        {
            XmlNode? rootNode = xmlDoc.SelectSingleNode("//swifttypedatabase");
            if (rootNode == null)
                throw new Exception("Invalid XML structure: 'swifttypedatabase' node not found.");

            var databaseModuleName = rootNode.Attributes?["moduleName"]?.Value ?? throw new Exception("Invalid XML structure: Missing 'moduleName' attribute.");
            var databaseModulePath = rootNode.Attributes?["modulePath"]?.Value ?? throw new Exception("Invalid XML structure: Missing 'modulePath' attribute.");

            var moduleDatabase = new ModuleTypeDatabase(databaseModuleName, databaseModulePath);

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
                string inheritedRequirementsOnly = typeDeclarationNode?.Attributes?["inheritedRequirementsOnly"]?.Value ?? "false";
                string classBound = typeDeclarationNode?.Attributes?["classBound"]?.Value ?? "false";
                string objcRooted = typeDeclarationNode?.Attributes?["objcRooted"]?.Value ?? "false";
                string hasMethodSelfTypeParams = typeDeclarationNode?.Attributes?["hasMethodSelfTypeParams"]?.Value ?? "false";
                string nonCopyable = typeDeclarationNode?.Attributes?["nonCopyable"]?.Value ?? "false";
                string hasFloatFields = typeDeclarationNode?.Attributes?["hasFloatFields"]?.Value ?? "false";
                string hasBoolFields = typeDeclarationNode?.Attributes?["hasBoolFields"]?.Value ?? "false";
                string objcBridgeable = typeDeclarationNode?.Attributes?["objcBridgeable"]?.Value ?? "false";
                string? rawValueType = typeDeclarationNode?.Attributes?["rawValueType"]?.Value;
                string? emittedMemberCountStr = typeDeclarationNode?.Attributes?["emittedMemberCount"]?.Value;
                int? emittedMemberCount = emittedMemberCountStr != null ? int.Parse(emittedMemberCountStr) : null;
                string? superclassStr = typeDeclarationNode?.Attributes?["superclass"]?.Value;
                string? inlineSizeStr = typeDeclarationNode?.Attributes?["inlineSize"]?.Value;
                int? inlineSize = inlineSizeStr != null ? int.Parse(inlineSizeStr) : null;
                string? abiFieldLayout = typeDeclarationNode?.Attributes?["abiLayout"]?.Value;
                string? protocolDescriptorSymbol = typeDeclarationNode?.Attributes?["protocolDescriptorSymbol"]?.Value;
                if (swiftTypeIdentifier == null || csharpTypeIdentifier == null)
                    throw new Exception("Invalid XML structure: Missing attributes.");


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
                            (inheritedRequirementsOnly.ToLower() == "true" ? TypeRecordFlags.InheritedRequirementsOnly : TypeRecordFlags.None) |
                            (classBound.ToLower() == "true" ? TypeRecordFlags.ClassBound : TypeRecordFlags.None) |
                            (objcRooted.ToLower() == "true" ? TypeRecordFlags.ObjCRooted : TypeRecordFlags.None) |
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
                    SuperclassTypeName = superclassStr != null && !superclassStr.Contains('<')
                        ? SwiftTypeName.FromModuleQualifiedName(superclassStr)
                        : null,
                    InlineSize = inlineSize,
                    AbiFieldLayout = abiFieldLayout,
                    ProtocolDescriptorSymbol = protocolDescriptorSymbol,
                };

                moduleDatabase.RegisterType(swiftTypeName, typeRecord);
            }

            return moduleDatabase;
        }

        /// <inheritdoc/>
        public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
        {
            // SwiftBindings.Apple supplement wins over local module databases for any
            // identity it owns — both cross-module references (Foundation.Locale.Language
            // from Translation) AND same-module references within an Apple framework
            // package (CryptoKit.P256.Signing.ECDSASignature from CryptoKit bindings).
            // Running the resolver FIRST for supplement-owned types keeps framework
            // packages deferring to the supplement's canonical projection instead of
            // re-emitting a parallel local class. The resolver short-circuits to false
            // when the identity is not in the manifest, so non-supplement types fall
            // through to the normal module-database lookup untouched.
            if (AppleSupplementResolver.TryResolve(swiftTypeName, currentlyGeneratingModule: null, out var supplementRecord))
            {
                AppleSupplementReferences.Record(swiftTypeName.ModuleQualifiedName);
                record = supplementRecord;
                return true;
            }

            if (TryGetTypeRecordInternal(swiftTypeName, out record))
                return true;

            // C-interop aliases often use either Foo or FooRef across sources.
            // Try a suffix variant to avoid missing CoreGraphics/CoreFoundation typedef-backed types.
            var refVariant = GetRefAliasVariant(swiftTypeName);
            if (refVariant != null && TryGetTypeRecordInternal(refVariant, out record))
                return true;

            // Try looking in the out-of-module types
            if (_outOfModuleTypes.TryGetValue(swiftTypeName, out record))
                return true;

            // Cross-module type aliases: resolve the alias to its canonical type and retry.
            // E.g., FamilyControls.ApplicationToken → ManagedSettings.Token<ManagedSettings.Application>
            // Strip generic args for the TypeRecord lookup (the TypeRecord is for the base type).
            if (_typeAliases.TryGetValue(swiftTypeName.ModuleQualifiedName, out var canonicalName))
            {
                var baseName = canonicalName.IndexOf('<') is var idx and >= 0
                    ? canonicalName[..idx]
                    : canonicalName;
                var canonicalTypeName = SwiftTypeName.FromModuleQualifiedName(baseName);
                if (TryGetTypeRecordInternal(canonicalTypeName, out record))
                    return true;
            }

            // Well-known stdlib protocols (Swift.Error → AnyError)
            if (swiftTypeName.ModuleQualifiedName == "Swift.Error")
            {
                record = TypeDatabaseExtensions.SwiftErrorType;
                return true;
            }

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
            var moduleName = name.Module;
            if (_modules.TryGetValue(moduleName, out var moduleDb))
            {
                moduleDb.RegisterType(name, record);
                return;
            }
            // Fall back to out-of-module types
            _outOfModuleTypes.AddOrUpdate(name, record, (_, _) => record);
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

        private bool TryGetTypeRecordInternal(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
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

            record = null;
            return false;
        }

        private bool IsTypeProcessedInternal(SwiftTypeName swiftTypeName)
        {
            if (_modules.TryGetValue(swiftTypeName.Module, out var moduleDatabase))
                return moduleDatabase.IsTypeProcessed(swiftTypeName);

            // Try module alias (e.g., CoreFoundation -> CoreGraphics)
            if (_moduleAliases.TryGetValue(swiftTypeName.Module, out var aliasedModule))
            {
                if (_modules.TryGetValue(aliasedModule, out moduleDatabase))
                {
                    var aliasedQualifiedName = $"{aliasedModule}.{swiftTypeName.ModuleQualifiedName[(swiftTypeName.Module.Length + 1)..]}";
                    var aliasedTypeName = SwiftTypeName.FromModuleQualifiedName(aliasedQualifiedName);
                    return moduleDatabase.IsTypeProcessed(aliasedTypeName);
                }
            }

            return false;
        }

        private static SwiftTypeName? GetRefAliasVariant(SwiftTypeName swiftTypeName)
        {
            var fullName = swiftTypeName.ModuleQualifiedName;
            if (fullName.EndsWith("Ref", StringComparison.Ordinal))
            {
                return SwiftTypeName.FromModuleQualifiedName(fullName[..^3]);
            }

            return SwiftTypeName.FromModuleQualifiedName($"{fullName}Ref");
        }
    }
}
