// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BindingsGeneration
{
    /// <summary>
    /// The structured type-ownership manifest the Swift pipeline writes alongside its generated
    /// C# (<c>swift-types.json</c>). It replaces the old "regex-scrape the emitted <c>*.cs</c> for
    /// <c>public class|struct|enum|interface NAME</c>" heuristic (Finding 23). Each entry records
    /// a Swift type's <see cref="SwiftName"/>, the <see cref="ObjCRuntimeName"/> it registers
    /// under, the <see cref="ProjectedCSharpName"/> it emits as, and its <see cref="Kind"/>.
    /// <para/>
    /// The mixed-framework ObjC dedup matches ObjC declarations against
    /// <see cref="ObjCRuntimeName"/> — the only naming universe both pipelines share. This fixes
    /// the two structural defects of the scrape: (1) the Swift pipeline emits a protocol as
    /// <c>IFoo</c> while the ObjC side names it <c>Foo</c>, so the scrape's <c>IFoo</c> never
    /// matched the ObjC <c>Foo</c> (the protocol leg could not fire); and (2) an
    /// <c>@objc(CustomName)</c> rename evaded the scrape entirely. It also deletes the stale-file
    /// hazard (the scrape read every <c>*.cs</c> in the output dir, including leftovers from prior
    /// runs).
    /// </summary>
    public sealed class SwiftTypeOwnershipManifest
    {
        /// <summary>The on-disk file name, written into and read from the generator output dir.</summary>
        public const string FileName = "swift-types.json";

        /// <summary>The schema version this generator WRITES. Bump in lockstep with
        /// <see cref="ExpectedSchemaVersion"/> whenever the on-disk shape changes.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>The schema version a consumer REQUIRES. The reader hard-throws
        /// (SWIFTBIND105) when an on-disk manifest declares a different version, mirroring the
        /// project's <c>kSchemaVersion</c>/<c>ExpectedSchemaVersion</c> handshake discipline so a
        /// shape drift fails loudly instead of silently mis-mapping ownership.</summary>
        public const int ExpectedSchemaVersion = 1;

        [JsonProperty("schemaVersion", Order = 0)]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonProperty("module", Order = 1)]
        public string Module { get; set; } = string.Empty;

        [JsonProperty("types", Order = 2)]
        public List<SwiftTypeOwnershipEntry> Types { get; set; } = new();
    }

    /// <summary>One Swift type's cross-pipeline ownership record. See
    /// <see cref="SwiftTypeOwnershipManifest"/>.</summary>
    public sealed class SwiftTypeOwnershipEntry
    {
        /// <summary>The Swift declaration's source name (e.g. <c>Widget</c>).</summary>
        [JsonProperty("swiftName", Order = 0)]
        public string SwiftName { get; set; } = string.Empty;

        /// <summary>The Objective-C runtime name this type registers under — equal to
        /// <see cref="SwiftName"/> unless an <c>@objc(CustomName)</c> rename applies. This is the
        /// match key the mixed-framework dedup uses against ObjC declaration names.</summary>
        [JsonProperty("objcRuntimeName", Order = 1)]
        public string ObjCRuntimeName { get; set; } = string.Empty;

        /// <summary>The C# identifier the Swift pipeline emits this type as: <c>I{Name}</c> for a
        /// protocol, the bare name otherwise. Informational/best-effort — present so ownership is
        /// unit-testable; the dedup itself keys on <see cref="ObjCRuntimeName"/>.</summary>
        [JsonProperty("projectedCSharpName", Order = 2)]
        public string ProjectedCSharpName { get; set; } = string.Empty;

        /// <summary>The declaration kind: <c>class</c>, <c>protocol</c>, <c>struct</c>, or
        /// <c>enum</c>.</summary>
        [JsonProperty("kind", Order = 3)]
        public string Kind { get; set; } = string.Empty;
    }

    /// <summary>
    /// Builds, writes, and reads the <see cref="SwiftTypeOwnershipManifest"/>.
    /// </summary>
    public static class SwiftTypeOwnershipManifestEmitter
    {
        /// <summary>
        /// Builds the manifest from the parsed module model and writes <c>swift-types.json</c> into
        /// <paramref name="outputDirectory"/>. Covers public, non-SPI top-level and nested
        /// classes/structs/enums (<c>module.Types</c>) and protocols (<c>module.Protocols</c>) —
        /// the same public-surface gate the emitter uses for top-level type emission.
        /// </summary>
        public static void Emit(ModuleDecl module, string outputDirectory, ILogger logger)
        {
            var manifest = Build(module);
            var path = Path.Combine(outputDirectory, SwiftTypeOwnershipManifest.FileName);
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Include,
            };
            Directory.CreateDirectory(outputDirectory);
            File.WriteAllText(path, JsonConvert.SerializeObject(manifest, settings) + Environment.NewLine);
            logger.LogDebug(
                "Wrote Swift type-ownership manifest ({Count} type(s)) to {Path}.",
                manifest.Types.Count, path);
        }

        /// <summary>
        /// Builds the in-memory manifest from the module model. Exposed for unit testing.
        /// </summary>
        public static SwiftTypeOwnershipManifest Build(ModuleDecl module)
        {
            var manifest = new SwiftTypeOwnershipManifest
            {
                SchemaVersion = SwiftTypeOwnershipManifest.CurrentSchemaVersion,
                Module = module.Name,
            };

            // ModuleDecl.Types already includes protocols: the parser fills it with
            // decls.OfType<TypeDecl>() and ProtocolDecl : TypeDecl, so Types is the superset and
            // Protocols is just its protocol subset. Walking Types alone covers every kind without
            // double-counting protocols (which also appear in ModuleDecl.Protocols).
            foreach (var type in module.Types)
                CollectType(type, manifest.Types);

            return manifest;
        }

        private static void CollectType(TypeDecl type, List<SwiftTypeOwnershipEntry> sink)
        {
            // Public-surface gate: mirror the top-level type emission filter. @usableFromInline
            // and @_spi types are not part of the consumer-visible ObjC surface, so they never
            // collide with an ObjC declaration and must not drive a dedup drop.
            if (!type.IsModuleInternal && !type.IsSpiProtected)
            {
                var kind = KindOf(type);
                if (kind != null)
                {
                    var swiftName = type.GetSwiftName();
                    sink.Add(new SwiftTypeOwnershipEntry
                    {
                        SwiftName = swiftName,
                        // Null ObjCRuntimeName means "no @objc rename" → the ObjC runtime name
                        // equals the Swift source name (the ObjC @interface/@protocol emitted in
                        // the Swift-generated header).
                        ObjCRuntimeName = type.ObjCRuntimeName ?? swiftName,
                        ProjectedCSharpName = kind == "protocol" ? "I" + type.Name : type.Name,
                        Kind = kind,
                    });
                }
            }

            // Nested types (e.g. a nested @objc class requires an explicit @objc(Name) the facts
            // captured, which we want in the manifest too).
            foreach (var nested in type.Types)
                CollectType(nested, sink);
        }

        private static string? KindOf(TypeDecl type) => type switch
        {
            ClassDecl => "class",
            ProtocolDecl => "protocol",
            StructDecl => "struct",
            EnumDecl => "enum",
            _ => null,
        };

        /// <summary>
        /// Reads <c>swift-types.json</c> from <paramref name="outputDirectory"/> and returns the
        /// set of Objective-C runtime names the Swift pipeline owns — the dedup key set the
        /// mixed-framework ObjC filter matches its declarations against. Returns an empty set when
        /// no manifest is present (a non-mixed or legacy output dir).
        /// </summary>
        /// <exception cref="InvalidOperationException">The manifest declares a schema version
        /// other than <see cref="SwiftTypeOwnershipManifest.ExpectedSchemaVersion"/>
        /// (SWIFTBIND105), or its contents are not parseable JSON (SWIFTBIND106) — either is a
        /// generator/consumer drift or on-disk corruption that must fail loudly rather than
        /// silently mis-map ownership.</exception>
        public static HashSet<string> ReadOwnedObjCRuntimeNames(string outputDirectory)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var path = Path.Combine(outputDirectory, SwiftTypeOwnershipManifest.FileName);
            if (!File.Exists(path))
                return names;

            JObject json;
            try
            {
                json = JObject.Parse(File.ReadAllText(path));
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"SWIFTBIND106: '{SwiftTypeOwnershipManifest.FileName}' at '{path}' is not " +
                    "parseable JSON. The Swift type-ownership manifest is corrupt or truncated; " +
                    "regenerate the Swift bindings with the current generator.", ex);
            }
            var schemaVersion = json.Value<int?>("schemaVersion");
            if (schemaVersion != SwiftTypeOwnershipManifest.ExpectedSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"SWIFTBIND105: '{SwiftTypeOwnershipManifest.FileName}' declares schema version " +
                    $"{(schemaVersion.HasValue ? schemaVersion.Value.ToString() : "<missing>")}, but this " +
                    $"generator expects {SwiftTypeOwnershipManifest.ExpectedSchemaVersion}. The Swift " +
                    "type-ownership manifest shape drifted between the writer and reader. Regenerate the " +
                    "Swift bindings with the current generator (bump CurrentSchemaVersion and " +
                    "ExpectedSchemaVersion in lockstep when changing the manifest shape).");
            }

            if (json["types"] is JArray types)
            {
                foreach (var entry in types)
                {
                    var objcName = entry.Value<string>("objcRuntimeName");
                    if (!string.IsNullOrEmpty(objcName))
                        names.Add(objcName);
                }
            }

            return names;
        }
    }
}
