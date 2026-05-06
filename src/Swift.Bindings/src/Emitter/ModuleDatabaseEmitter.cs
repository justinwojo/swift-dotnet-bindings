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
        /// <returns>The path to the emitted XML file, or null if no records to emit.</returns>
        public static string? Emit(
            ModuleTypeDatabase moduleDatabase,
            string outputDirectory,
            ILogger logger,
            IReadOnlyCollection<string>? suppressedProxyClassNames = null,
            string? suppressedProxyNamespace = null)
        {
            var records = moduleDatabase.GetAllTypeRecords()
                .OrderBy(kvp => kvp.Key.ModuleQualifiedName, StringComparer.Ordinal)
                .ToList();
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
            // Extract the type name without module prefix (e.g., "ImageRequest.UserInfoKey" from "Nuke.ImageRequest.UserInfoKey")
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

            if ((record.Flags & TypeRecordFlags.InheritedRequirementsOnly) != 0)
                writer.WriteAttributeString("inheritedRequirementsOnly", "true");

            if ((record.Flags & TypeRecordFlags.ClassBound) != 0)
                writer.WriteAttributeString("classBound", "true");

            if ((record.Flags & TypeRecordFlags.ObjCRooted) != 0)
                writer.WriteAttributeString("objcRooted", "true");

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

            writer.WriteEndElement(); // typedeclaration
            writer.WriteEndElement(); // entity
        }
    }
}
