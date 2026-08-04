// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Typed model for <c>build/baselines/api-manifest-baseline.json</c> — the ABI-contract
/// ratchet. Each entry pins one emitted public member's <c>(module, C# signature)</c> to the
/// native entry symbol its P/Invoke binds, as recorded by the generator's
/// <c>BindingsGeneration.ApiManifestEmitter</c> into <c>{Module}.api-manifest.json</c>.
///
/// <para><b>Self-contained by design.</b> BCL-only (no Nuke, source-generated JSON) so the
/// unit-test project can link-compile and test the pure <see cref="Compare"/> logic directly —
/// the same pattern as <c>RuntimeIdentityBaseline</c> / <c>ArtifactParityGate</c>.</para>
///
/// <para><b>Ratchet semantics</b> — unlike the count-based trend gates, this one is an exact
/// contract check on a stable key, and it ratchets in BOTH directions:
/// <list type="bullet">
///   <item><description><b>RETARGET</b> (same <c>(module, signature)</c> now binds a DIFFERENT
///     symbol) is a <b>failure</b>: the consumer-visible C# API silently rebinds to other native
///     code — the overload-disambiguation hazard the gate catches. Either it's a real regression, or an
///     intentional, reviewed ABI change that must reseed the baseline in the same commit.</description></item>
///   <item><description><b>REMOVED</b> (a baseline signature no longer emitted) is a
///     <b>failure</b> too: a member that used to be part of the binding's public surface silently
///     vanished. A generator change that withdraws a whole type takes every one of that type's
///     members with it, and to a consumer already calling them that is indistinguishable from the
///     library deleting its API — except nothing else notices, because a compile gate is perfectly
///     happy with a binding that emits LESS. Surface shrink must be an explicit, reviewed decision
///     that reseeds this baseline, not a side effect nobody sees.</description></item>
///   <item><description><b>ADDED</b> (a signature absent from the baseline) is reported but is
///     <b>not</b> a failure — growing the bound surface is the direction of travel, and requiring
///     a reseed for every newly bound member would make the baseline pure friction.</description></item>
/// </list></para>
///
/// <para><b>Honest scope.</b> The manifest covers the members that carry a native entry symbol:
/// methods, constructors and free functions (written at the generator's overload-disambiguation
/// chokepoints, which give them their post-collision C# signature) plus properties and subscripts
/// (written at their own emission points and keyed by accessor). Type declarations are NOT
/// recorded, so a type whose declaration vanishes while it had no symbol-bearing member at all
/// passes this gate silently. Read a green result as "no symbol-bearing member disappeared", never
/// as "the public surface is unchanged".</para>
/// </summary>
public record ApiManifestBaseline
{
    /// <summary>Bumped in lockstep with <c>BindingsGeneration.ApiManifestEmitter.SchemaVersion</c>.</summary>
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("git_sha")] public string GitSha { get; init; } = "";

    /// <summary>
    /// Flat list of <c>(module, signature, symbol)</c> rows. Stored as a list of records (not a
    /// nested dictionary) so the on-disk JSON diffs cleanly in review — a retarget shows as a
    /// one-line symbol change on a stable signature.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<ApiManifestBaselineEntry> Entries { get; init; } = Array.Empty<ApiManifestBaselineEntry>();

    public record ApiManifestBaselineEntry
    {
        [JsonPropertyName("module")] public string Module { get; init; } = "";
        [JsonPropertyName("signature")] public string Signature { get; init; } = "";
        [JsonPropertyName("symbol")] public string Symbol { get; init; } = "";
    }

    public static ApiManifestBaseline Load(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path)) : new();

    /// <summary>Parses baseline JSON (empty/whitespace ⇒ a fresh empty baseline).</summary>
    public static ApiManifestBaseline Parse(string json)
        => string.IsNullOrWhiteSpace(json)
            ? new()
            : JsonSerializer.Deserialize(json, ApiManifestBaselineJsonContext.Default.ApiManifestBaseline)
              ?? new();

    public void Save(string path) => File.WriteAllText(path, ToJson());

    public string ToJson()
        => JsonSerializer.Serialize(this, ApiManifestBaselineJsonContext.Default.ApiManifestBaseline);

    /// <summary>
    /// Diffs current manifest entries against the baseline.
    /// </summary>
    /// <returns>
    /// <c>Retargets</c>: stable <c>(module, signature)</c> keys whose symbol changed.
    /// <c>Removed</c>: baseline signatures no longer emitted. Both FAIL the gate.
    /// <c>Added</c>: newly emitted signatures — informational, never a failure.
    /// </returns>
    public ApiManifestComparison Compare(
        IReadOnlyList<ApiManifestBaselineEntry> currentEntries)
    {
        var retargets = new List<string>();
        var added = new List<string>();
        var removed = new List<string>();

        // Last-write-wins on a duplicate key mirrors the generator's SortedDictionary, so the
        // gate's view of "the symbol for this signature" matches what was serialized.
        var baselineByKey = new Dictionary<(string Module, string Signature), string>();
        foreach (var e in Entries) baselineByKey[(e.Module, e.Signature)] = e.Symbol;

        var currentByKey = new Dictionary<(string Module, string Signature), string>();
        foreach (var e in currentEntries) currentByKey[(e.Module, e.Signature)] = e.Symbol;

        foreach (var (key, currSymbol) in currentByKey.OrderBy(kv => kv.Key.Module, StringComparer.Ordinal)
                     .ThenBy(kv => kv.Key.Signature, StringComparer.Ordinal))
        {
            if (!baselineByKey.TryGetValue(key, out var prevSymbol))
                added.Add($"ADDED {key.Module}: {key.Signature} → {currSymbol}");
            else if (prevSymbol != currSymbol)
                retargets.Add($"RETARGET {key.Module}: {key.Signature} — {prevSymbol} → {currSymbol}");
        }

        foreach (var (key, prevSymbol) in baselineByKey.OrderBy(kv => kv.Key.Module, StringComparer.Ordinal)
                     .ThenBy(kv => kv.Key.Signature, StringComparer.Ordinal))
        {
            if (!currentByKey.ContainsKey(key))
                removed.Add($"REMOVED {key.Module}: {key.Signature} (was {prevSymbol})");
        }

        return new ApiManifestComparison(retargets, added, removed);
    }
}

/// <summary>
/// Outcome of one baseline-vs-current manifest diff, plus the gate's pass/fail policy. Keeping the
/// policy on the model (rather than only in the build target) makes the two-sided ratchet directly
/// testable: the build target's job reduces to reporting and throwing.
/// </summary>
public readonly record struct ApiManifestComparison(
    IReadOnlyList<string> Retargets,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed)
{
    /// <summary>
    /// True when the diff must fail the gate. Both directions of the ratchet block: a retarget
    /// silently rebinds an existing member to different native code, and a removal silently deletes
    /// a member a consumer may already call. An addition never blocks.
    /// </summary>
    public bool HasBlockingFindings => Retargets.Count > 0 || Removed.Count > 0;
}

/// <summary>Source-generation context for <see cref="ApiManifestBaseline"/> — keeps
/// (de)serialization AOT-safe (no reflection) so the model link-compiles cleanly into the
/// IsAotCompatible unit-test project, mirroring <c>RuntimeIdentityBaselineJsonContext</c>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ApiManifestBaseline))]
internal partial class ApiManifestBaselineJsonContext : JsonSerializerContext
{
}
