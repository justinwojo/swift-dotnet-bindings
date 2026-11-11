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
// Reconciles the embedded stdlib-conformances.json fact table — the ConformanceOracle's
// curated stdlib slice — with ground truth from a `swift-api-digester -dump-sdk -module
// Swift` dump. The table is a curated MATRIX: its type keys are the stdlib types the oracle
// answers for, and its `protocols` list is the protocol axis it answers about. The command
// makes the table exactly (curated types × curated protocols) ∩ (what the live stdlib
// declares), in two directions:
//
//   - PRUNE: a listed `Type : Protocol` pair the live stdlib does not declare is dropped
//     (a hand-curation error, or a conformance removed upstream).
//   - COMPLETE: a curated pair the live stdlib declares but the table omits is added, so a
//     protocol curated for one type is never silently missing for another (the gap that left
//     every non-Int scalar failing a `Codable` constraint).
//
// The curation itself — which types, which protocols — stays a deliberate edit to the JSON
// (add a type key with an empty list, or add a protocol to `protocols`); the regen fills the
// matrix from ground truth. It never adds a protocol that is not on the axis, so the oracle's
// surface grows only by deliberate curation.
//
// Exit codes:
//   0 — table matches ground truth, or write-back succeeded.
//   1 — drift detected in drift-detect mode (a pair to prune or to complete), or a
//       file/parse/write error.
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

        ReconcileResult result;
        try
        {
            result = ReconcileAgainstDump(table, dumpJson);
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
        foreach (var pair in result.AddedPairs)
            logger.LogInformation("ADD     {Pair} — declared by the live Swift stdlib, missing from the table.", pair);

        logger.LogInformation(
            "stdlib-conformances regen: {Types} types × {Protocols} protocols verified, {Dropped} stale " +
            "conformance(s) pruned, {Added} missing conformance(s) completed, {NotFound} type(s) absent from dump.",
            table.Conformances.Count, table.Protocols.Count, result.DroppedPairs.Count, result.AddedPairs.Count,
            result.TypesNotInDump.Count);

        var changed = result.DroppedPairs.Count + result.AddedPairs.Count;
        if (!writeBack)
        {
            if (changed > 0)
            {
                logger.LogError(
                    "stdlib-conformances.json is stale: {Dropped} curated conformance(s) are not declared by the " +
                    "live Swift stdlib and {Added} it declares are missing. Re-run with " +
                    "--stdlib-conformances-write-back to reconcile.",
                    result.DroppedPairs.Count, result.AddedPairs.Count);
                return 1;
            }
            logger.LogInformation("stdlib-conformances.json matches the live Swift stdlib — no drift.");
            return 0;
        }

        if (changed == 0)
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
            logger.LogInformation("Wrote reconciled table to '{Path}'.", tablePath);
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
    /// Makes <paramref name="table"/> exactly its curated matrix — every type key × every
    /// protocol on <see cref="StdlibConformanceTable.Protocols"/> — intersected with what the
    /// live Swift stdlib (per the swift-api-digester dump in <paramref name="dumpJson"/>)
    /// declares. Mutates the table in place. A listed pair the stdlib does not declare, or whose
    /// protocol is off the axis, is pruned, wherever it came from; a declared pair on the protocol axis that the table omits
    /// is added. Surviving entries keep their order and additions follow in axis order, so a
    /// clean run is a byte-stable round-trip. A type key absent from the dump is left untouched
    /// (a wrong dump must not empty the table) and reported. Pure and toolchain-free for unit
    /// testing.
    /// </summary>
    public static ReconcileResult ReconcileAgainstDump(StdlibConformanceTable table, string dumpJson)
    {
        var live = ParseLiveConformances(dumpJson);
        var dropped = new List<string>();
        var added = new List<string>();
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
                if (table.Protocols.Contains(protocol, StringComparer.Ordinal)
                    && liveProtocols.Contains(BareName(protocol)))
                    kept.Add(protocol);
                else
                    dropped.Add($"{typeKey} : {protocol}");
            }
            foreach (var protocol in table.Protocols)
            {
                if (kept.Contains(protocol, StringComparer.Ordinal) || !liveProtocols.Contains(BareName(protocol)))
                    continue;
                kept.Add(protocol);
                added.Add($"{typeKey} : {protocol}");
            }
            table.Conformances[typeKey] = kept;
        }

        return new ReconcileResult(dropped, added, notInDump);
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
        // Two-space indented, trailing newline — matches the committed format so a reconcile is a
        // minimal one-line-per-changed-conformance diff.
        var json = JsonConvert.SerializeObject(table, Formatting.Indented);
        return json + "\n";
    }
}

/// <summary>Outcome of a <see cref="StdlibConformancesRegenCommand.ReconcileAgainstDump"/> pass.</summary>
public sealed class ReconcileResult
{
    public ReconcileResult(
        IReadOnlyList<string> droppedPairs,
        IReadOnlyList<string> addedPairs,
        IReadOnlyList<string> typesNotInDump)
    {
        DroppedPairs = droppedPairs;
        AddedPairs = addedPairs;
        TypesNotInDump = typesNotInDump;
    }

    /// <summary>Listed <c>Type : Protocol</c> pairs the live stdlib does not declare (now pruned).</summary>
    public IReadOnlyList<string> DroppedPairs { get; }

    /// <summary>Curated-matrix pairs the live stdlib declares that the table omitted (now added).</summary>
    public IReadOnlyList<string> AddedPairs { get; }

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

    /// <summary>The curated protocol axis: the protocols the table answers about.</summary>
    [JsonProperty("protocols")]
    public List<string> Protocols { get; set; } = new();

    [JsonProperty("conformances")]
    public Dictionary<string, List<string>> Conformances { get; set; } = new();
}
