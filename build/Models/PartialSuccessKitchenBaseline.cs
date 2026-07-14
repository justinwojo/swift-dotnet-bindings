// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Typed model for <c>build/baselines/partial-success-kitchen-baseline.json</c> — the frozen
/// skip budget of the PartialSuccessKitchen fixture. The <c>--partial-success-kitchen</c> gate
/// generates a binding from a tiny module that deliberately mixes unsupported shapes with two
/// must-emit types, then proves the product promise: the generator exits 0, the emitted C#
/// compiles, and the skip report honestly accounts for every dropped shape.
///
/// <para>Two independent checks run against the freshly-generated <c>binding-report.json</c>:
/// <list type="bullet">
///   <item><description><b>Design floors</b> (<see cref="CheckFloors"/>) — the shape-independent
///     invariants the design fixes up front: no Review-tier skips, no dangling wrapper symbols,
///     and enough of each expected disposition that "partial success" is genuinely exercised.
///     These never move.</description></item>
///   <item><description><b>Exact drift</b> (<see cref="Compare"/>) — the per-reason and
///     per-disposition multiset is frozen after the first green run and compared exactly, so any
///     change in how the generator classifies these shapes (a new skip, a reason relabel, a shape
///     that starts emitting) surfaces as a gate failure until the baseline is reseeded in the same
///     commit. This is the "report is accurate" half of the gate.</description></item>
/// </list></para>
///
/// <para><b>Self-contained by design.</b> BCL-only (no Nuke, source-generated JSON) so the
/// unit-test project can link-compile and exercise the pure parsing / floor / diff logic directly —
/// the same pattern as <c>ApiManifestBaseline</c> / <c>RuntimeIdentityBaseline</c>.</para>
/// </summary>
public record PartialSuccessKitchenBaseline
{
    [JsonPropertyName("git_sha")] public string GitSha { get; init; } = "";

    /// <summary>The <c>SkipTriage.ReviewCount</c> the fixture is frozen at. Design floor is 0.</summary>
    [JsonPropertyName("review_count")] public int ReviewCount { get; init; }

    /// <summary>Frozen per-<c>SkipReason</c> multiset, one flat row per reason for clean review diffs.</summary>
    [JsonPropertyName("by_reason")]
    public IReadOnlyList<CountEntry> ByReason { get; init; } = Array.Empty<CountEntry>();

    /// <summary>Frozen per-<c>SkipDisposition</c> multiset.</summary>
    [JsonPropertyName("by_disposition")]
    public IReadOnlyList<CountEntry> ByDisposition { get; init; } = Array.Empty<CountEntry>();

    public record CountEntry
    {
        [JsonPropertyName("key")] public string Key { get; init; } = "";
        [JsonPropertyName("count")] public int Count { get; init; }
    }

    // ── Design floors (never move; enforced on every run including the seed run) ───────────
    public const int MinExpectedStructural = 2;
    public const int MinExpectedNonPublic = 1;
    public const int MinKnownLimitation = 3;

    public static PartialSuccessKitchenBaseline Load(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path)) : new();

    public static PartialSuccessKitchenBaseline Parse(string json)
        => string.IsNullOrWhiteSpace(json)
            ? new()
            : JsonSerializer.Deserialize(json, PartialSuccessKitchenBaselineJsonContext.Default.PartialSuccessKitchenBaseline)
              ?? new();

    public void Save(string path) => File.WriteAllText(path, ToJson());

    public string ToJson()
        => JsonSerializer.Serialize(this, PartialSuccessKitchenBaselineJsonContext.Default.PartialSuccessKitchenBaseline);

    /// <summary>Freezes a baseline from an observed report projection.</summary>
    public static PartialSuccessKitchenBaseline FromReport(KitchenReportProjection report, string gitSha = "")
    {
        ArgumentNullException.ThrowIfNull(report);
        return new PartialSuccessKitchenBaseline
        {
            GitSha = gitSha,
            ReviewCount = report.ReviewCount,
            ByReason = report.ByReason
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new CountEntry { Key = kv.Key, Count = kv.Value })
                .ToList(),
            ByDisposition = report.ByDisposition
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new CountEntry { Key = kv.Key, Count = kv.Value })
                .ToList(),
        };
    }

    /// <summary>
    /// The shape-independent design invariants (§8 of the fixture design). Returns one string per
    /// violated floor; an empty list means the report clears every floor. Enforced on every run —
    /// including the auto-seed run — so a degenerate report can never be frozen as the baseline.
    /// </summary>
    public static IReadOnlyList<string> CheckFloors(KitchenReportProjection report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var failures = new List<string>();

        if (report.ReviewCount != 0)
            failures.Add($"ReviewCount must be 0 (every skip must have a defensible disposition) — got {report.ReviewCount}: "
                + string.Join(", ", report.ReviewItemSummaries));

        var missingWrapper = report.ByReason.GetValueOrDefault("MissingWrapperSymbol");
        if (missingWrapper != 0)
            failures.Add($"MissingWrapperSymbol rows must be 0 (no dangling wrapper symbol) — got {missingWrapper}");

        var structural = report.ByDisposition.GetValueOrDefault("ExpectedStructural");
        if (structural < MinExpectedStructural)
            failures.Add($"ExpectedStructural rows must be ≥ {MinExpectedStructural} — got {structural}");

        var nonPublic = report.ByDisposition.GetValueOrDefault("ExpectedNonPublic");
        if (nonPublic < MinExpectedNonPublic)
            failures.Add($"ExpectedNonPublic rows must be ≥ {MinExpectedNonPublic} — got {nonPublic}");

        var known = report.ByDisposition.GetValueOrDefault("KnownLimitation");
        if (known < MinKnownLimitation)
            failures.Add($"KnownLimitation rows must be ≥ {MinKnownLimitation} — got {known}");

        return failures;
    }

    /// <summary>
    /// Exact-match diff of an observed report against the frozen baseline: the ReviewCount and the
    /// full per-reason and per-disposition multisets must match. Returns one string per drift; an
    /// empty list means the report matches the frozen budget. Any drift fails the gate until the
    /// baseline is reseeded in the same commit.
    /// </summary>
    public IReadOnlyList<string> Compare(KitchenReportProjection report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var drift = new List<string>();

        if (report.ReviewCount != ReviewCount)
            drift.Add($"ReviewCount drift: baseline {ReviewCount} → observed {report.ReviewCount}");

        DiffMultiset("SkipReason", ByReason, report.ByReason, drift);
        DiffMultiset("SkipDisposition", ByDisposition, report.ByDisposition, drift);

        return drift;
    }

    private static void DiffMultiset(
        string label, IReadOnlyList<CountEntry> baseline, IReadOnlyDictionary<string, int> observed, List<string> drift)
    {
        var baselineByKey = baseline.ToDictionary(e => e.Key, e => e.Count, StringComparer.Ordinal);

        foreach (var key in baselineByKey.Keys.Union(observed.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            var prev = baselineByKey.GetValueOrDefault(key);
            var curr = observed.GetValueOrDefault(key);
            if (prev == curr) continue;
            if (prev == 0)
                drift.Add($"NEW {label} '{key}' (observed {curr}) — reseed baseline if intentional");
            else if (curr == 0)
                drift.Add($"GONE {label} '{key}' (baseline {prev}) — reseed baseline if intentional");
            else
                drift.Add($"{label} '{key}' count drift: baseline {prev} → observed {curr}");
        }
    }
}

/// <summary>
/// The parsed, gate-relevant projection of a generator <c>binding-report.json</c>: the
/// <c>SkipTriage</c> roll-up (ReviewCount, per-reason and per-disposition counts) plus the short
/// review-item list for diagnostics. Parsing lives here (a pure <see cref="JsonDocument"/> DOM read,
/// no reflection) so it is unit-testable without running the generator.
/// </summary>
public sealed record KitchenReportProjection
{
    public int ReviewCount { get; init; }
    public IReadOnlyDictionary<string, int> ByReason { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> ByDisposition { get; init; } = new Dictionary<string, int>();

    /// <summary>Short "kind Name: reason" strings for each Review-tier item, for a legible failure log.</summary>
    public IReadOnlyList<string> ReviewItemSummaries { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Parses the gate-relevant slice of a generator <c>binding-report.json</c>. A report with no
    /// <c>SkipTriage</c> block (a module that skipped nothing) projects to an all-empty result, which
    /// the floor check then fails for want of the expected skip shapes — never a silent pass.
    /// </summary>
    public static KitchenReportProjection ParseReport(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new KitchenReportProjection();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("SkipTriage", out var triage)
            || triage.ValueKind != JsonValueKind.Object)
            return new KitchenReportProjection();

        var reviewCount = triage.TryGetProperty("ReviewCount", out var rc) && rc.ValueKind == JsonValueKind.Number
            ? rc.GetInt32()
            : 0;

        var reviewItems = new List<string>();
        if (triage.TryGetProperty("ReviewItems", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                var kind = item.TryGetProperty("Kind", out var k) ? k.GetString() : null;
                var name = item.TryGetProperty("Name", out var n) ? n.GetString() : null;
                var reason = item.TryGetProperty("Reason", out var rn) ? rn.GetString() : null;
                reviewItems.Add($"{kind} {name}: {reason}");
            }
        }

        return new KitchenReportProjection
        {
            ReviewCount = reviewCount,
            ByReason = ReadCountMap(triage, "ByReason"),
            ByDisposition = ReadCountMap(triage, "ByDisposition"),
            ReviewItemSummaries = reviewItems,
        };
    }

    private static IReadOnlyDictionary<string, int> ReadCountMap(JsonElement parent, string property)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (parent.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var member in obj.EnumerateObject())
            {
                if (member.Value.ValueKind == JsonValueKind.Number)
                    map[member.Name] = member.Value.GetInt32();
            }
        }
        return map;
    }
}

/// <summary>Source-generation context for <see cref="PartialSuccessKitchenBaseline"/> — keeps
/// (de)serialization AOT-safe (no reflection) so the model link-compiles cleanly into the
/// IsAotCompatible unit-test project, mirroring <c>ApiManifestBaselineJsonContext</c>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PartialSuccessKitchenBaseline))]
internal partial class PartialSuccessKitchenBaselineJsonContext : JsonSerializerContext
{
}
