// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BindingsGeneration.StdlibConformances;

// Entry point for the `--regen-stdlib-conformances` CLI mode (wrapped by the
// `nuke regen-stdlib-conformances` target, which runs swift-api-digester to produce the
// --stdlib-dump first).
//
// Verifies the embedded stdlib-conformances.json fact table — the ConformanceOracle's
// curated stdlib slice — against ground truth from a `swift-api-digester -dump-sdk -module
// Swift` dump. The table is a deliberately MINIMAL "ONE input" (it omits true-but-irrelevant
// conformances like Sendable/Copyable/Decodable), so this command does NOT widen it to the
// full live conformance graph. It only PRUNES: any curated `Type : Protocol` the live stdlib
// no longer declares is dropped. That keeps the file a faithful, reproducible projection of
// the toolchain's stdlib without silently expanding the oracle's emission surface, and it
// catches hand-curation errors (a listed conformance the stdlib never actually declared) and
// cross-Xcode drift (a conformance removed upstream).
//
// Extending the curated set (a new stdlib type, or a new protocol for an existing type) stays
// a deliberate edit to the JSON followed by a regen-to-verify run; the regen confirms the
// addition against ground truth or prunes it back out if it isn't real.
//
// Exit codes:
//   0 — table matches ground truth (nothing to prune), or write-back succeeded.
//   1 — drift detected in drift-detect mode (a curated entry the stdlib does not declare),
//       or a file/parse/write error.
public static class StdlibConformancesRegenCommand
{
    public static int Run(
        string dumpPath,
        string tablePath,
        bool writeBack,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(dumpPath))
        {
            logger.LogError("--regen-stdlib-conformances requires --stdlib-dump <path>.");
            return 1;
        }
        if (string.IsNullOrWhiteSpace(tablePath))
        {
            logger.LogError("--regen-stdlib-conformances requires --stdlib-conformances <path>.");
            return 1;
        }
        if (!File.Exists(dumpPath))
        {
            logger.LogError("swift-api-digester dump not found: '{Path}'.", dumpPath);
            return 1;
        }
        if (!File.Exists(tablePath))
        {
            logger.LogError("stdlib-conformances.json not found: '{Path}'.", tablePath);
            return 1;
        }

        StdlibConformanceTable table;
        try
        {
            table = JsonConvert.DeserializeObject<StdlibConformanceTable>(File.ReadAllText(tablePath))
                ?? throw new InvalidOperationException("Deserialization returned null.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load stdlib-conformances.json '{Path}': {Message}", tablePath, ex.Message);
            return 1;
        }

        string dumpJson;
        try
        {
            dumpJson = File.ReadAllText(dumpPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read swift-api-digester dump '{Path}': {Message}", dumpPath, ex.Message);
            return 1;
        }

        PruneResult result;
        try
        {
            result = PruneAgainstDump(table, dumpJson, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to parse swift-api-digester dump '{Path}': {Message}", dumpPath, ex.Message);
            return 1;
        }

        foreach (var typeKey in result.TypesNotInDump)
            logger.LogWarning(
                "Type '{Type}' is in the fact table but not in the Swift module dump — keeping its entries " +
                "unchanged (a wrong/empty dump must not nuke the table). Verify --stdlib-dump targets the " +
                "Swift module.", typeKey);

        foreach (var pair in result.DroppedPairs)
            logger.LogInformation("PRUNE   {Pair} — not declared by the live Swift stdlib.", pair);

        logger.LogInformation(
            "stdlib-conformances regen: {Types} types verified, {Dropped} stale conformance(s) pruned, " +
            "{NotFound} type(s) absent from dump.",
            table.Conformances.Count, result.DroppedPairs.Count, result.TypesNotInDump.Count);

        if (!writeBack)
        {
            if (result.DroppedPairs.Count > 0)
            {
                logger.LogError(
                    "stdlib-conformances.json is stale: {Count} curated conformance(s) are not declared by " +
                    "the live Swift stdlib. Re-run with --stdlib-conformances-write-back to prune them.",
                    result.DroppedPairs.Count);
                return 1;
            }
            logger.LogInformation("stdlib-conformances.json matches the live Swift stdlib — no drift.");
            return 0;
        }

        if (result.DroppedPairs.Count == 0)
        {
            logger.LogInformation("stdlib-conformances.json already matches the live Swift stdlib — nothing to write.");
            return 0;
        }

        // Temp-file + atomic rename: a crash mid-serialization must not leave the repo's
        // committed table truncated. Mirrors AppleTypesManifestValidateCommand's write path.
        var tempPath = tablePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tempPath, Serialize(table));
            File.Move(tempPath, tablePath, overwrite: true);
            logger.LogInformation("Wrote pruned table to '{Path}'.", tablePath);
        }
        catch (Exception ex)
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
            logger.LogError(ex, "Failed to write back '{Path}': {Message}", tablePath, ex.Message);
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Prunes from <paramref name="table"/> every curated <c>Type : Protocol</c> pair that the
    /// live Swift stdlib (per the swift-api-digester dump in <paramref name="dumpJson"/>) does
    /// not declare. Mutates <paramref name="table"/> in place; never adds a conformance, never
    /// reorders surviving ones — so a clean run is a byte-stable round-trip. A type key absent
    /// from the dump is left untouched (a wrong dump must not empty the table) and reported via
    /// <see cref="PruneResult.TypesNotInDump"/>. Pure and toolchain-free for unit testing.
    /// </summary>
    public static PruneResult PruneAgainstDump(StdlibConformanceTable table, string dumpJson, ILogger? logger = null)
    {
        var live = ParseLiveConformances(dumpJson);
        var dropped = new List<string>();
        var notInDump = new List<string>();

        // Snapshot the keys so reassigning values doesn't risk enumerator invalidation.
        foreach (var typeKey in table.Conformances.Keys.ToList())
        {
            if (!live.TryGetValue(BareName(typeKey), out var liveProtocols))
            {
                notInDump.Add(typeKey);
                continue;
            }

            var kept = new List<string>();
            foreach (var protocol in table.Conformances[typeKey])
            {
                if (liveProtocols.Contains(BareName(protocol)))
                    kept.Add(protocol);
                else
                    dropped.Add($"{typeKey} : {protocol}");
            }
            table.Conformances[typeKey] = kept;
        }

        return new PruneResult(dropped, notInDump);
    }

    /// <summary>
    /// Builds a map from each Swift-module type's bare name to the set of bare protocol names it
    /// declares conformance to, unioned across every top-level <c>TypeDecl</c> node carrying that
    /// name (so conformances split across extensions all count).
    /// </summary>
    private static Dictionary<string, HashSet<string>> ParseLiveConformances(string dumpJson)
    {
        var root = JObject.Parse(dumpJson);
        var children = root["ABIRoot"]?["children"] as JArray
            ?? throw new InvalidOperationException("dump has no ABIRoot.children array.");

        var live = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var node in children)
        {
            if ((string?)node["kind"] != "TypeDecl") continue;
            if ((string?)node["moduleName"] != "Swift") continue;
            var name = (string?)node["name"];
            if (string.IsNullOrEmpty(name)) continue;

            if (!live.TryGetValue(name, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                live[name] = set;
            }

            if (node["conformances"] is JArray conformances)
            {
                foreach (var conf in conformances)
                {
                    if ((string?)conf["kind"] != "Conformance") continue;
                    var protocolName = (string?)conf["name"];
                    if (!string.IsNullOrEmpty(protocolName))
                        set.Add(protocolName!);
                }
            }
        }
        return live;
    }

    private static string BareName(string qualified)
    {
        var dot = qualified.IndexOf('.');
        return dot < 0 ? qualified : qualified[(dot + 1)..];
    }

    private static string Serialize(StdlibConformanceTable table)
    {
        // Two-space indented, trailing newline — matches the committed format so a prune is a
        // minimal one-line-per-dropped-conformance diff.
        var json = JsonConvert.SerializeObject(table, Formatting.Indented);
        return json + "\n";
    }
}

/// <summary>Outcome of a <see cref="StdlibConformancesRegenCommand.PruneAgainstDump"/> pass.</summary>
public sealed class PruneResult
{
    public PruneResult(IReadOnlyList<string> droppedPairs, IReadOnlyList<string> typesNotInDump)
    {
        DroppedPairs = droppedPairs;
        TypesNotInDump = typesNotInDump;
    }

    /// <summary>Curated <c>Type : Protocol</c> pairs the live stdlib does not declare (now pruned).</summary>
    public IReadOnlyList<string> DroppedPairs { get; }

    /// <summary>Table type keys absent from the dump — left unchanged, reported for inspection.</summary>
    public IReadOnlyList<string> TypesNotInDump { get; }
}

/// <summary>
/// The shape of stdlib-conformances.json. Member order matches the file so a write-back round-trip
/// preserves field order; <see cref="ConformanceOracle"/> reads the same shape via its own loader.
/// </summary>
public sealed class StdlibConformanceTable
{
    [JsonProperty("$schema")]
    public string? Schema { get; set; }

    [JsonProperty("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonProperty("comment")]
    public string? Comment { get; set; }

    [JsonProperty("conformances")]
    public Dictionary<string, List<string>> Conformances { get; set; } = new();
}
