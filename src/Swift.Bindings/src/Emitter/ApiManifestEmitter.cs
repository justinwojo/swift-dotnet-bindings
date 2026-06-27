// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Emits <c>{Module}.api-manifest.json</c> — the consumer-visible binding contract,
    /// mapping each emitted public member's post-collision C# signature to the native entry
    /// symbol its P/Invoke binds. The ratchet gate (<c>build/Build.ApiManifestGate.cs</c>) diffs
    /// this against a committed baseline and fails when an UNCHANGED C# signature retargets to a
    /// DIFFERENT symbol — the overload-disambiguation hazard. Collision suffixes are assigned in
    /// declaration order, so the bare-name owner (and thus the symbol a stable C# signature binds)
    /// can shift if upstream reorders its overloads; the manifest is the durable detector that
    /// surfaces exactly that — any retarget of a stable signature, whatever its cause, is caught
    /// before it ships. Added/removed members are reported by the gate but are not failures
    /// (only a retarget on a stable signature breaks the ABI contract).
    /// </summary>
    public static class ApiManifestEmitter
    {
        /// <summary>Bumped only when the on-disk manifest shape changes; the gate asserts a match.</summary>
        public const int SchemaVersion = 1;

        /// <summary>
        /// Writes <c>{namespace}.api-manifest.json</c> next to the generated <c>.cs</c>. No-ops
        /// (returns <c>null</c>) when there is no emission context or no recorded members.
        /// </summary>
        public static string? Emit(string moduleName, string @namespace, ModuleEmissionContext? emissionCtx,
            string outputDirectory, ILogger logger)
        {
            if (emissionCtx is null) return null;
            var entries = emissionCtx.ApiManifestEntries;
            if (entries.Count == 0) return null;

            var document = new ApiManifestDocument
            {
                SchemaVersion = SchemaVersion,
                Module = moduleName,
                // ApiManifestEntries is already Ordinal-sorted; the explicit OrderBy keeps the
                // serialized order independent of the backing collection's iteration contract.
                Members = entries
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => new ApiManifestMember { Signature = kv.Key, Symbol = kv.Value })
                    .ToList(),
            };

            var path = Path.Combine(outputDirectory, $"{@namespace}.api-manifest.json");
            File.WriteAllText(path, JsonSerializer.Serialize(document, ApiManifestJsonContext.Default.ApiManifestDocument));
            logger.LogInformation($"Wrote API manifest ({document.Members.Count} members) to {path}");
            return path;
        }
    }

    /// <summary>On-disk shape of <c>{Module}.api-manifest.json</c>.</summary>
    public sealed class ApiManifestDocument
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; set; }
        [JsonPropertyName("module")] public string Module { get; set; } = "";
        [JsonPropertyName("members")] public List<ApiManifestMember> Members { get; set; } = new();
    }

    /// <summary>One emitted public member: its post-collision C# signature → native entry symbol.</summary>
    public sealed class ApiManifestMember
    {
        [JsonPropertyName("signature")] public string Signature { get; set; } = "";
        [JsonPropertyName("symbol")] public string Symbol { get; set; } = "";
    }

    /// <summary>
    /// Source-generated JSON serializer context for AOT/trim-safe serialization of the API manifest.
    /// </summary>
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(ApiManifestDocument))]
    internal partial class ApiManifestJsonContext : JsonSerializerContext
    {
    }
}
