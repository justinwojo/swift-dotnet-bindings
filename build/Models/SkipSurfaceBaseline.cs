// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nuke.Common.IO;

/// <summary>
/// Typed model for <c>build/baselines/skip-surface-baseline.json</c> — Layer B trend gate over
/// mechanically-parseable skip markers in generator output (see
/// <c>src/docs/0.10.0-fix-plan.md</c> §"Layer B"). Mirrors the
/// <see cref="ValidationBaseline"/> ratchet pattern.
///
/// <para><b>Scope</b>: skip-class regressions only. Shape-class projection bugs
/// (wrong type lowering, missing interface adoption, lost defaults) live in
/// Layer A, not here.</para>
///
/// <para><b>Keying</b>: each entry is keyed on <c>(Source, Marker, Reason)</c>.
/// <c>Source</c> is the generator-output file path relative to the repository
/// root (e.g. <c>BindingTests/output/SwiftBindingsTestLib.cs</c>) or a
/// SurfaceArea snippet identifier once that corpus is populated. <c>Marker</c>
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
///   <item><description>A baseline key absent from current results is silently retained
///     in the diff report as an improvement, not an error — that's the path that lets
///     fix bundles drop their entries by editing the baseline downward.</description></item>
/// </list></para>
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
        /// File path of the generator output (relative to repo root) OR the
        /// SurfaceArea snippet identifier (once the corpus is populated).
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

    public static SkipSurfaceBaseline Load(AbsolutePath path)
        => File.Exists(path)
            ? JsonSerializer.Deserialize<SkipSurfaceBaseline>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            : new();

    public void Save(AbsolutePath path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>
    /// Diffs current scan results against the baseline.
    /// </summary>
    /// <returns>
    /// <c>Regressions</c>: entries that fail the ratchet (upward count or new key).
    /// <c>Improvements</c>: keys whose count went down or disappeared.
    /// </returns>
    public (IReadOnlyList<string> Regressions, IReadOnlyList<string> Improvements) Compare(
        IReadOnlyList<SkipSurfaceEntry> currentEntries)
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

        // Existing keys: upward delta is a regression, downward/flat is fine.
        foreach (var (key, prevCount) in baselineByKey)
        {
            if (!currentByKey.TryGetValue(key, out var currCount))
            {
                // Disappeared entirely — improvement. (Baseline still carries the row;
                // committers prune in the bundle commit that delivers the fix.)
                improvements.Add($"GONE {key.Marker} '{key.Reason}' in {key.Source} (was {prevCount})");
                continue;
            }
            if (currCount > prevCount)
                regressions.Add($"UP {key.Marker} '{key.Reason}' in {key.Source} ({prevCount} → {currCount})");
            else if (currCount < prevCount)
                improvements.Add($"DOWN {key.Marker} '{key.Reason}' in {key.Source} ({prevCount} → {currCount})");
        }

        return (regressions, improvements);
    }
}
