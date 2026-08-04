// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Xml;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Serializes a ModuleTypeDatabase to XML for cross-module type resolution.
    /// The emitted XML uses the same schema that TypeDatabase.ReadVersion1_0 parses,
    /// so downstream modules can load it via --module-database to resolve types
    /// from previously generated dependency modules.
    ///
    /// <para><b>Withdrawal parity.</b> The database is a promise to a DOWNSTREAM generator:
    /// "this module declares these types, resolve against them". A type the type-database
    /// registration pass recorded but emission later refused to declare breaks that promise —
    /// the downstream module happily emits code naming a type its dependency's assembly does not
    /// contain, and the break lands in GENERATED code the consumer cannot edit. Registration runs
    /// before emission, so it cannot know about emission-time withdrawals: a malformed-ingestion
    /// closure or the verify-recover loop can withdraw a type long after its record was written.
    /// <paramref name="withdrawnTypeNames"/> closes that gap by filtering those records out at
    /// serialization time.</para>
    ///
    /// <para>The filter is deliberately keyed on <em>withdrawal</em>, not on "was a C# type
    /// declared". Several records are RESOLUTION-ONLY by design and must survive: a type owned by
    /// the Apple supplement package is declared there rather than here, and a SwiftUI View is
    /// projected through the generated bridge instead of a type declaration. Both are still
    /// resolvable identities a downstream module must be able to look up, so a "every record has a
    /// declaration" rule would wrongly delete them. Only a withdrawn type is declared nowhere at
    /// all.</para>
    /// </summary>
    public static class ModuleDatabaseEmitter
    {
        /// <summary>
        /// Emits a module database XML file for the given module.
        /// Returns the output file path, or null if the module has zero type records.
        /// </summary>
        /// <param name="moduleDatabase">The module database to serialize.</param>
        /// <param name="outputDirectory">Directory to write the XML file.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="suppressedProxyClassNames">Proxy class names suppressed by this emission.</param>
        /// <param name="suppressedProxyNamespace">C# namespace the suppressed proxies would have used.</param>
        /// <param name="withdrawnTypeNames">
        /// Module-qualified Swift names of types this emission withdrew at emission time (see the
        /// class remarks). Records matching one of these names are dropped from the serialized
        /// database. Null/empty means "nothing was withdrawn".
        /// </param>
        /// <returns>The path to the emitted XML file, or null if no records to emit.</returns>
        public static string? Emit(
            ModuleTypeDatabase moduleDatabase,
            string outputDirectory,
            ILogger logger,
            IReadOnlyCollection<string>? suppressedProxyClassNames = null,
            string? suppressedProxyNamespace = null,
            IReadOnlyCollection<string>? withdrawnTypeNames = null)
        {
            var withdrawn = withdrawnTypeNames is { Count: > 0 }
                ? new HashSet<string>(withdrawnTypeNames, StringComparer.Ordinal)
                : null;

            var allRecords = moduleDatabase.GetAllTypeRecords()
                .OrderBy(kvp => kvp.Key.ModuleQualifiedName, StringComparer.Ordinal)
                .ToList();

            var records = allRecords;
            if (withdrawn != null)
            {
                records = allRecords
                    .Where(kvp => !withdrawn.Contains(kvp.Key.ModuleQualifiedName))
                    .ToList();
                var dropped = allRecords.Count - records.Count;
                if (dropped > 0)
                {
                    logger.LogInformation(
                        "Module '{Module}': withheld {Count} withdrawn type record(s) from the module database " +
                        "so it cannot advertise a type this binding does not declare.",
                        moduleDatabase.Name, dropped);
                }
            }

            if (records.Count == 0)
            {
                logger.LogInformation("Module '{Module}' has no type records — skipping database emission.", moduleDatabase.Name);
                return null;
            }

            var outputPath = Path.Combine(outputDirectory, $"{moduleDatabase.Name}Database.xml");

            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "    ",
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Replace,
                OmitXmlDeclaration = false,
                Encoding = new System.Text.UTF8Encoding(false)
            };

            using (var writer = XmlWriter.Create(outputPath, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("swifttypedatabase");
                writer.WriteAttributeString("version", "1.0");
                writer.WriteAttributeString("moduleName", moduleDatabase.Name);
                writer.WriteAttributeString("modulePath", moduleDatabase.Path);

                writer.WriteStartElement("entities");

                foreach (var kvp in records)
                {
                    var record = kvp.Value;
                    WriteEntity(writer, record);
                }

                writer.WriteEndElement(); // entities

                // Suppressed proxy class names — names whose EveryProtocol conformance
                // was skipped in this module's emission so the proxy class did not emit.
                // Downstream modules use this to strip method bodies that reference the
                // cross-module qualified form (`{Namespace}.SwiftInterop.{ProxyName}`) which
                // the umbrella-aware existential marshaler can route to suppressed proxies.
                //
                // The `namespace` attribute is the C# namespace into which the proxies
                // would have been emitted (`{generatedNamespace}.SwiftInterop`). Persisting
                // it here is required because `QualifyProxyClassName` uses the protocol's
                // C# namespace (via `record.CSharpTypeName.Namespace`), not the Swift module
                // name — with a non-default `namespacePattern` the two diverge. Older
                // databases that predate this attribute fall back to the Swift module name
                // on read, matching the default-pattern equivalence.
                if (suppressedProxyClassNames is { Count: > 0 })
                {
                    var orderedNames = suppressedProxyClassNames
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList();
                    writer.WriteStartElement("suppressedProxies");
                    if (!string.IsNullOrEmpty(suppressedProxyNamespace))
                        writer.WriteAttributeString("namespace", suppressedProxyNamespace);
                    foreach (var proxyName in orderedNames)
                    {
                        writer.WriteStartElement("proxy");
                        writer.WriteAttributeString("name", proxyName);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement(); // suppressedProxies
                }

                writer.WriteEndElement(); // swifttypedatabase
                writer.WriteEndDocument();
            }

            logger.LogInformation("Emitted module database: {Path} ({Count} type records)", outputPath, records.Count);
            return outputPath;
        }

        private static void WriteEntity(XmlWriter writer, TypeRecord record)
        {
            writer.WriteStartElement("entity");
            writer.WriteAttributeString("managedNameSpace", record.CSharpTypeName.Namespace ?? string.Empty);
            writer.WriteAttributeString("managedTypeName", record.CSharpTypeName.Name);

            writer.WriteStartElement("typedeclaration");
            writer.WriteAttributeString("module", record.SwiftTypeName.Module);
            // Extract the type name without module prefix (e.g., "ImageRequest.UserInfoKey" from "ImagePipeline.ImageRequest.UserInfoKey")
            var nameWithoutModule = record.SwiftTypeName.ModuleQualifiedName[(record.SwiftTypeName.Module.Length + 1)..];
            writer.WriteAttributeString("name", nameWithoutModule);
            writer.WriteAttributeString("mangledName", record.MetadataAccessor ?? string.Empty);
            writer.WriteAttributeString("frozen", (record.Flags & TypeRecordFlags.Frozen) != 0 ? "true" : "false");
            writer.WriteAttributeString("requiresMemoryManagement",
                (record.Flags & TypeRecordFlags.RequiresMemoryManagement) != 0 ? "true" : "false");
            writer.WriteAttributeString("objcBridged",
                (record.Flags & TypeRecordFlags.ObjCBridged) != 0 ? "true" : "false");

            // Kind
            var kindStr = record.Kind switch
            {
                TypeRecordKind.Class => "class",
                TypeRecordKind.Enum => "enum",
                TypeRecordKind.Protocol => "protocol",
                TypeRecordKind.Existential => "existential",
                _ => "struct",
            };
            writer.WriteAttributeString("kind", kindStr);

            // Optional flags
            if ((record.Flags & TypeRecordFlags.HasAssociatedTypes) != 0)
                writer.WriteAttributeString("hasAssociatedTypes", "true");

            if ((record.Flags & TypeRecordFlags.HasSelfRequirement) != 0)
                writer.WriteAttributeString("hasSelfRequirement", "true");

            if ((record.Flags & TypeRecordFlags.SimpleEnum) != 0)
                writer.WriteAttributeString("simpleEnum", "true");

            if ((record.Flags & TypeRecordFlags.OptionSet) != 0)
                writer.WriteAttributeString("optionSet", "true");

            if ((record.Flags & TypeRecordFlags.InheritedRequirementsOnly) != 0)
                writer.WriteAttributeString("inheritedRequirementsOnly", "true");

            if ((record.Flags & TypeRecordFlags.ClassBound) != 0)
                writer.WriteAttributeString("classBound", "true");

            if ((record.Flags & TypeRecordFlags.ObjCRooted) != 0)
                writer.WriteAttributeString("objcRooted", "true");

            if ((record.Flags & TypeRecordFlags.ObjCProtocol) != 0)
                writer.WriteAttributeString("objcProtocol", "true");

            if ((record.Flags & TypeRecordFlags.HasMethodSelfTypeParams) != 0)
                writer.WriteAttributeString("hasMethodSelfTypeParams", "true");

            if ((record.Flags & TypeRecordFlags.NonCopyable) != 0)
                writer.WriteAttributeString("nonCopyable", "true");

            if ((record.Flags & TypeRecordFlags.HasFloatFields) != 0)
                writer.WriteAttributeString("hasFloatFields", "true");

            if ((record.Flags & TypeRecordFlags.HasBoolFields) != 0)
                writer.WriteAttributeString("hasBoolFields", "true");

            if (!string.IsNullOrEmpty(record.RawValueTypeName))
                writer.WriteAttributeString("rawValueType", record.RawValueTypeName);

            // Present only on a mixed-framework ObjC type whose C# name is its Swift-import name
            // rather than its ObjC spelling. A downstream module resolving this type as a superclass
            // has both spellings available (the ABI gives the Swift name, the Clang USR the ObjC one)
            // but no way to know which one this binding actually emitted — this is that answer.
            if (!string.IsNullOrEmpty(record.ObjCRuntimeName))
                writer.WriteAttributeString("objcRuntimeName", record.ObjCRuntimeName);

            // Emitted interface member count (protocols only)
            if (record.Kind == TypeRecordKind.Protocol && record.EmittedMemberCount.HasValue)
                writer.WriteAttributeString("emittedMemberCount", record.EmittedMemberCount.Value.ToString());

            // Total associated-type count (protocols only). Drives constrained-existential
            // projection: a 3-AT protocol used as `any P<X, Y>` (2 args, primary-AT sugar)
            // must NOT project to `IP<X, Y>` since the interface emits all 3 ATs as type
            // parameters. Omit when null so legacy module databases keep loading as null;
            // the consumer treats null as "unverifiable" and skips strongly-typed
            // projection — preserving pre-fix behavior on cross-module references that
            // predate this attribute.
            if (record.Kind == TypeRecordKind.Protocol && record.AssociatedTypeCount.HasValue)
                writer.WriteAttributeString("associatedTypeCount", record.AssociatedTypeCount.Value.ToString());

            // Superclass type name (classes only)
            if (record.SuperclassTypeName != null)
                writer.WriteAttributeString("superclass", record.SuperclassTypeName.ModuleQualifiedName);

            // Whether the class body emitted PInvoke_getMetadata (Class kind only). See
            // ClassHandler.HasMetadataPInvokeInResolvedAncestors — a derived class in a
            // downstream module checks this flag to decide whether the C# `new` modifier on
            // its own PInvoke_getMetadata declaration shadows an inherited member. Omit the
            // attribute when null so legacy module databases keep loading as null.
            if (record.Kind == TypeRecordKind.Class && record.EmittedMetadataPInvoke.HasValue)
                writer.WriteAttributeString(
                    "emittedMetadataPInvoke",
                    record.EmittedMetadataPInvoke.Value ? "true" : "false");

            // Direct protocol conformances (struct/class/enum: declared protocols;
            // protocol: inherited protocols). Stored as a comma-separated list of
            // module-qualified names, mirroring AbiFieldLayout's compact encoding.
            // Used by the bilateral specialization filter to verify
            // `S.Element : SomeProtocol` constraints across modules.
            if (record.ProtocolConformances is { Count: > 0 } conformances)
            {
                writer.WriteAttributeString(
                    "protocolConformances",
                    string.Join(",", conformances.Select(p => p.ModuleQualifiedName)));
            }

            // Inline size for frozen struct Buffer field sizing (e.g., Swift.String = 16 bytes)
            if (record.InlineSize.HasValue)
                writer.WriteAttributeString("inlineSize", record.InlineSize.Value.ToString());

            // ABI field layout for ARM64 thunk register decomposition (e.g., "i,f,i,f")
            if (!string.IsNullOrEmpty(record.AbiFieldLayout))
                writer.WriteAttributeString("abiLayout", record.AbiFieldLayout);

            // Protocol descriptor symbol (protocols only) — used for runtime witness-table
            // lookups when emitting type metadata accessor PInvokes for generics constrained
            // on protocols that can't be projected as static C# interfaces.
            if (!string.IsNullOrEmpty(record.ProtocolDescriptorSymbol))
                writer.WriteAttributeString("protocolDescriptorSymbol", record.ProtocolDescriptorSymbol);

            // Native type name (e.g., Foundation.NSUrl for URL)
            if (record.NativeTypeName != null)
            {
                var nativeType = string.IsNullOrEmpty(record.NativeTypeName.Namespace)
                    ? record.NativeTypeName.Name
                    : $"{record.NativeTypeName.Namespace}.{record.NativeTypeName.Name}";
                writer.WriteAttributeString("nativeType", nativeType);
            }

            // Emitted class instance methods (Class kind only) — used by downstream modules to
            // verify cross-module `override` modifiers. See WrapperEmitter.HasMethodInResolvedAncestors.
            // Always emit the <emittedMethods> element when the list is non-null, even when empty:
            // an empty element means "this class was processed and emitted zero instance methods"
            // (e.g., everything was filtered by validation gates) and the verifier must reject
            // any derived `override` against this parent. Omitting the element would round-trip
            // back to null on read, which the verifier treats as a legacy database and trusts the
            // Swift IsOverride bit — reopening the CS0115 case the populator was added to close.
            if (record.Kind == TypeRecordKind.Class && record.EmittedClassMethods != null)
            {
                writer.WriteStartElement("emittedMethods");
                foreach (var method in record.EmittedClassMethods)
                {
                    writer.WriteStartElement("method");
                    writer.WriteAttributeString("swiftName", method.SwiftName);
                    writer.WriteAttributeString("csharpName", method.CSharpName);
                    // Pipe-separated to avoid colliding with generic type-arg commas. Empty list
                    // (no parameters) is encoded as an empty string, distinct from a single empty
                    // entry by way of WriteAttributeString preserving "".
                    writer.WriteAttributeString(
                        "paramTypes",
                        string.Join("|", method.ParameterSwiftTypes));
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            // Property renames applied by the producing module. Written whenever the collector
            // visited the type, empty element included: an empty <renamedMembers/> means "processed,
            // renamed nothing", which a consumer may rely on, while omitting the element round-trips
            // to null and means only "this database predates the ledger". Same authoritative-empty
            // rule as <emittedMethods> above, and for the same reason.
            if (record.RenamedMembers != null)
            {
                writer.WriteStartElement("renamedMembers");
                foreach (var member in record.RenamedMembers)
                {
                    writer.WriteStartElement("member");
                    writer.WriteAttributeString("kind", member.Kind);
                    writer.WriteAttributeString("swiftName", member.SwiftName);
                    // Staticness disambiguates a Swift static/instance pair sharing one identifier.
                    if (member.IsStatic)
                        writer.WriteAttributeString("static", "true");
                    writer.WriteAttributeString("csharpName", member.CSharpName);
                    writer.WriteAttributeString("scheme", member.Scheme);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            // Per-type availability annotations — persisted across the cross-module XML
            // round-trip so a downstream module can inherit a dependency type's `@available`
            // floor on a generated wrapper (e.g. `Wrapper<OtherModule.AvailableLaterType>`).
            // Only emitted when at least one annotation is present; omission round-trips
            // to null on read, which the consumer treats as "no availability info" (legacy
            // database or always-available type — same fallback as before this field existed).
            if (record.AvailabilityAnnotations is { Count: > 0 } annotations)
            {
                writer.WriteStartElement("availability");
                foreach (var ann in annotations)
                {
                    writer.WriteStartElement("annotation");
                    if (ann.Platform != null)
                        writer.WriteAttributeString("platform", ann.Platform);
                    if (ann.IntroducedVersion != null)
                        writer.WriteAttributeString("introduced", ann.IntroducedVersion);
                    if (ann.DeprecatedVersion != null)
                        writer.WriteAttributeString("deprecated", ann.DeprecatedVersion);
                    if (ann.ObsoletedVersion != null)
                        writer.WriteAttributeString("obsoleted", ann.ObsoletedVersion);
                    if (ann.IsUnconditionallyDeprecated)
                        writer.WriteAttributeString("unconditionallyDeprecated", "true");
                    if (ann.IsUnconditionallyUnavailable)
                        writer.WriteAttributeString("unavailable", "true");
                    if (ann.Message != null)
                        writer.WriteAttributeString("message", ann.Message);
                    if (ann.Renamed != null)
                        writer.WriteAttributeString("renamed", ann.Renamed);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // typedeclaration
            writer.WriteEndElement(); // entity
        }
    }
}
