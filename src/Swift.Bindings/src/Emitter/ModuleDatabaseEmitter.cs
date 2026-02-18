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
            ILogger logger)
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

            if ((record.Flags & TypeRecordFlags.SimpleEnum) != 0)
                writer.WriteAttributeString("simpleEnum", "true");

            if (!string.IsNullOrEmpty(record.RawValueTypeName))
                writer.WriteAttributeString("rawValueType", record.RawValueTypeName);

            // Native type name (e.g., Foundation.NSUrl for URL)
            if (record.NativeTypeName != null)
            {
                var nativeType = string.IsNullOrEmpty(record.NativeTypeName.Namespace)
                    ? record.NativeTypeName.Name
                    : $"{record.NativeTypeName.Namespace}.{record.NativeTypeName.Name}";
                writer.WriteAttributeString("nativeType", nativeType);
            }

            writer.WriteEndElement(); // typedeclaration
            writer.WriteEndElement(); // entity
        }
    }
}
