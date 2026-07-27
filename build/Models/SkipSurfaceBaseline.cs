// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// This file is link-compiled into projects that do not enable nullable reference types, so the
// annotations below are opted into locally rather than inherited from a csproj.
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Typed model for <c>build/baselines/skip-surface-baseline.json</c> — Layer B trend gate over
/// mechanically-parseable skip markers in generator output. Mirrors the
/// <c>ValidationBaseline</c> ratchet pattern.
///
/// <para><b>Self-contained by design.</b> BCL-only (no build-framework path types,
/// source-generated JSON) so the unit-test project can link-compile and test the pure
/// <see cref="Compare"/> logic directly — the same pattern as <c>ApiManifestBaseline</c>.</para>
///
/// <para><b>Scope</b>: skip-class regressions only. Shape-class projection bugs
/// (wrong type lowering, missing interface adoption, lost defaults) live in
/// Layer A, not here.</para>
///
/// <para><b>Keying</b>: each entry is keyed on <c>(Source, Marker, Reason)</c>.
/// <c>Source</c> is the generator-output file path relative to the repository
/// root (e.g. <c>BindingTests/output/SwiftBindingsTestLib.cs</c>). <c>Marker</c>
/// is the emission shape (<c>Unsupported</c>, <c>Skipped</c>,
/// <c>UnsupportedSwiftType</c>, <c>ObsoleteSB0001</c>, or the reserved
/// <c>Tombstone</c> slot). <c>Reason</c> is the marker payload, normalized
/// (collapsed whitespace, trailing punctuation stripped).</para>
///
/// <para><b>Ratchet semantics</b>:
/// <list type="bullet">
///   <item><description>For every baseline key, current count must be ≤ baseline count
///     (downward or flat passes; upward fails).</description></item>
///   <item><description>A current key not present in the baseline fails the gate. New
///     keys are introduced only by committing a baseline update in the same change.</description></item>
///   <item><description>A baseline key whose count fell or vanished is an improvement —
///     that's the path that lets fix bundles drop their entries by editing the baseline
///     downward — UNLESS the cross-reference below says the marker disappeared because
///     the surface did.</description></item>
/// </list></para>
///
/// <para><b>Why a falling count is not self-evidently good.</b> A count-based ratchet cannot
/// distinguish "this member is bound now" from "this member no longer exists". A withdrawn type
/// takes its API and its skip markers with it, so an amputation reads as a clean improvement —
/// the one failure mode the gate would otherwise celebrate. <see cref="Compare"/> therefore
/// accepts the set of types that lost every symbol-bearing member since the API-manifest baseline
/// and reclassifies any falling row attributable to such a type as a regression.</para>
///
/// <para><b>Honest scope of that cross-reference.</b> It can only speak for markers whose reason
/// names a declaring type, and only for types the API manifest records at all — the manifest
/// covers symbol-bearing methods and constructors, not properties, subscripts, or bare type
/// declarations. A type that only ever had properties is invisible to it, and a marker whose
/// reason is generic prose (most attribute-shaped markers) carries no type to attribute. Those
/// rows stay unverified improvements; the cross-reference narrows the blind spot, it does not
/// close it.</para>
/// </summary>
public record SkipSurfaceBaseline
{
    [JsonPropertyName("git_sha")] public string GitSha { get; init; } = "";

    /// <summary>
    /// Per-key skip-marker counts. Stored as a list of records (rather than a
    /// dictionary) so the on-disk JSON is stable and reviewable — composite
    /// keys don't round-trip cleanly through JSON object keys, and flat-arrays-of-records
    /// diff well in code review.
    /// </summary>
    [JsonPropertyName("entries")]
    public IReadOnlyList<SkipSurfaceEntry> Entries { get; init; } = Array.Empty<SkipSurfaceEntry>();

    public record SkipSurfaceEntry
    {
        /// <summary>
        /// File path of the generator output, relative to the repository root.
        /// </summary>
        [JsonPropertyName("source")] public string Source { get; init; } = "";

        /// <summary>
        /// Marker kind — one of <c>Unsupported</c>, <c>UnsupportedSwiftType</c>,
        /// <c>ObsoleteSB0001</c>, <c>Skipped</c>, <c>Tombstone</c>. Stored
        /// alongside the reason because the same prose can land under different
        /// marker shapes and the ratchet has to distinguish them.
        /// </summary>
        [JsonPropertyName("marker")] public string Marker { get; init; } = "";

        /// <summary>
        /// Normalized skip reason — the prose payload of the marker, trimmed
        /// and collapsed.
        /// </summary>
        [JsonPropertyName("reason")] public string Reason { get; init; } = "";

        [JsonPropertyName("count")] public int Count { get; init; }
    }

    public static SkipSurfaceBaseline Load(string path)
        => File.Exists(path) ? Parse(File.ReadAllText(path)) : new();

    /// <summary>Parses baseline JSON (empty/whitespace ⇒ a fresh empty baseline).</summary>
    public static SkipSurfaceBaseline Parse(string json)
        => string.IsNullOrWhiteSpace(json)
            ? new()
            : JsonSerializer.Deserialize(json, SkipSurfaceBaselineJsonContext.Default.SkipSurfaceBaseline)
              ?? new();

    public void Save(string path) => File.WriteAllText(path, ToJson());

    public string ToJson()
        => JsonSerializer.Serialize(this, SkipSurfaceBaselineJsonContext.Default.SkipSurfaceBaseline);

    /// <summary>
    /// Diffs current scan results against the baseline.
    /// </summary>
    /// <param name="currentEntries">Skip markers parsed from the current generator output.</param>
    /// <param name="vanishedManifestTypes">
    /// <c>{module}|{TypeName}</c> keys for types that had at least one symbol-bearing member in the
    /// API-manifest baseline and have none now. A falling row attributable to one of these types is
    /// reported as a regression rather than an improvement: its marker went away because the type's
    /// bindable surface did. Pass an empty set to run the count ratchet alone.
    /// </param>
    /// <returns>
    /// <c>Regressions</c>: entries that fail the ratchet (upward count, new key, or a fall that the
    /// cross-reference attributes to vanished surface).
    /// <c>Improvements</c>: keys whose count genuinely went down or disappeared.
    /// </returns>
    public (IReadOnlyList<string> Regressions, IReadOnlyList<string> Improvements) Compare(
        IReadOnlyList<SkipSurfaceEntry> currentEntries,
        IReadOnlySet<string>? vanishedManifestTypes = null)
    {
        var regressions = new List<string>();
        var improvements = new List<string>();

        var baselineByKey = Entries.ToDictionary(e => (e.Source, e.Marker, e.Reason), e => e.Count);
        var currentByKey = currentEntries.ToDictionary(e => (e.Source, e.Marker, e.Reason), e => e.Count);

        // New keys (not in baseline) → regression: must be accompanied by a baseline update.
        foreach (var (key, count) in currentByKey)
        {
            if (!baselineByKey.ContainsKey(key))
                regressions.Add($"NEW {key.Marker} '{key.Reason}' in {key.Source} (count={count}) — add to baseline if intentional");
        }

        // Existing keys: upward delta is a regression, downward/flat is fine — subject to the
        // cross-reference below, which is what separates "fixed" from "amputated".
        foreach (var (key, prevCount) in baselineByKey)
        {
            currentByKey.TryGetValue(key, out var currCount);
            if (currCount > prevCount)
            {
                regressions.Add($"UP {key.Marker} '{key.Reason}' in {key.Source} ({prevCount} → {currCount})");
                continue;
            }
            if (currCount == prevCount) continue;

            var fell = currCount == 0
                ? $"GONE {key.Marker} '{key.Reason}' in {key.Source} (was {prevCount})"
                : $"DOWN {key.Marker} '{key.Reason}' in {key.Source} ({prevCount} → {currCount})";

            var vanishedType = FindVanishedType(key.Source, key.Reason, vanishedManifestTypes);
            if (vanishedType != null)
            {
                regressions.Add(
                    $"{fell} — but '{vanishedType}' no longer contributes any member to the API manifest, " +
                    "so this marker went away because the surface did, not because the skip was fixed");
                continue;
            }

            // Genuine improvement. (Baseline still carries the row; committers prune it in the
            // commit that delivers the fix.)
            improvements.Add(fell);
        }

        return (regressions, improvements);
    }

    /// <summary>
    /// Returns the module-qualified type a falling row should be blamed on, or null when the row
    /// cannot be attributed (no type in the reason, or the type still has manifest entries).
    /// </summary>
    private static string? FindVanishedType(string source, string reason, IReadOnlySet<string>? vanished)
    {
        if (vanished == null || vanished.Count == 0) return null;
        var declaringType = TryExtractDeclaringTypeFromReason(reason);
        if (declaringType == null) return null;
        var key = $"{ModuleFromSource(source)}|{declaringType}";
        return vanished.Contains(key) ? declaringType : null;
    }

    /// <summary>
    /// Derives the module name from a generator-output source path
    /// (<c>BindingTests/output/SwiftBindingsTestLib.cs</c> ⇒ <c>SwiftBindingsTestLib</c>), matching
    /// the module key the API manifest records.
    /// </summary>
    public static string ModuleFromSource(string source)
    {
        var file = source;
        var slash = file.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) file = file.Substring(slash + 1);
        var dot = file.IndexOf('.');
        return dot > 0 ? file.Substring(0, dot) : file;
    }

    /// <summary>
    /// Extracts the declaring type from a C# member signature as recorded in the API manifest
    /// (<c>Outer.Inner.Method(int)</c> ⇒ <c>Outer.Inner</c>). Returns null for a free function,
    /// which has no declaring type to attribute a skip to.
    /// </summary>
    public static string? TryExtractDeclaringType(string signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        var paren = signature.IndexOf('(');
        var head = paren >= 0 ? signature.Substring(0, paren) : signature;
        var lastDot = head.LastIndexOf('.');
        return lastDot > 0 ? head.Substring(0, lastDot) : null;
    }

    /// <summary>
    /// Extracts the declaring type a skip marker's reason names. Reasons begin with the skipped
    /// member's kind and its quoted Swift-qualified name (e.g.
    /// <c>method 'Outer.Inner.doThing' — …</c>); the type is that name minus its final segment.
    /// Type names survive projection unchanged, so the result is directly comparable with the
    /// manifest-derived form. Returns null when the reason names no member (generic attribute
    /// prose) or names a free function.
    /// </summary>
    public static string? TryExtractDeclaringTypeFromReason(string reason)
    {
        if (string.IsNullOrEmpty(reason)) return null;
        var open = reason.IndexOf('\'');
        if (open < 0) return null;
        var close = reason.IndexOf('\'', open + 1);
        if (close <= open + 1) return null;

        var qualified = reason.Substring(open + 1, close - open - 1);
        var lastDot = qualified.LastIndexOf('.');
        return lastDot > 0 ? qualified.Substring(0, lastDot) : null;
    }

    /// <summary>
    /// Types that had at least one symbol-bearing member in the API-manifest baseline and have
    /// NONE in the current manifests — the types whose skip markers can no longer count as
    /// improvements, because the marker went away with the surface rather than with the skip.
    /// Keyed <c>{module}|{TypeName}</c>; free functions (no declaring type) contribute nothing.
    /// </summary>
    /// <remarks>
    /// An EMPTY baseline yields nothing: with no reference point there is no loss to detect. An
    /// empty CURRENT set is the opposite case and deliberately yields EVERY baseline type — total
    /// surface collapse is the maximal loss this cross-reference exists to catch, so it must not
    /// short-circuit to "nothing vanished" and let the resulting flood of GONE skip rows bank as
    /// improvements.
    /// </remarks>
    public static IReadOnlySet<string> ComputeVanishedTypes(
        IReadOnlyList<ApiManifestBaseline.ApiManifestBaselineEntry> baselineEntries,
        IReadOnlyList<ApiManifestBaseline.ApiManifestBaselineEntry> currentEntries)
    {
        var vanished = new HashSet<string>(StringComparer.Ordinal);
        if (baselineEntries == null || baselineEntries.Count == 0) return vanished;

        var currentTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in currentEntries ?? Array.Empty<ApiManifestBaseline.ApiManifestBaselineEntry>())
        {
            var type = TryExtractDeclaringType(e.Signature);
            if (type != null) currentTypes.Add($"{e.Module}|{type}");
        }

        foreach (var e in baselineEntries)
        {
            var type = TryExtractDeclaringType(e.Signature);
            if (type == null) continue;
            var key = $"{e.Module}|{type}";
            if (!currentTypes.Contains(key)) vanished.Add(key);
        }

        return vanished;
    }
}

/// <summary>Source-generation context for <see cref="SkipSurfaceBaseline"/> — keeps
/// (de)serialization AOT-safe (no reflection) so the model link-compiles cleanly into the
/// IsAotCompatible unit-test project, mirroring <c>ApiManifestBaselineJsonContext</c>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SkipSurfaceBaseline))]
internal partial class SkipSurfaceBaselineJsonContext : JsonSerializerContext
{
}
