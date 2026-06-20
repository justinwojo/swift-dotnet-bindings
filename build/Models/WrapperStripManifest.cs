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
/// Per-run record of the harness Swift-wrapper post-processing leg. Session 7b retired the
/// bespoke harness stripper (<c>SwiftSourceStripper</c>) and routes the harness wrapper compile
/// through the generator's OWN <c>SwiftWrapperPostProcessor.Process</c> — the same scrub oracle
/// the generator-own wrapper compile uses — so the harness wrapper is identical to the
/// generator-own wrapper by construction (no over-strip, no PreservedProtocols allowlist).
///
/// <para>The manifest records exactly what <c>Process</c> removed: the total block count, the
/// per-sub-cause breakdown (internal-type body reference / Swift-unavailable type / broken
/// safety-net shape), and the stripped <c>@_cdecl</c>/<c>@_silgen_name</c> symbols. The committed
/// baseline (<c>BindingTests/baselines.json</c> <c>wrapper_stripped_count</c>) records the allowed
/// count so the gate fails on any INCREASE — a NEW uncompilable emission the generator should
/// never have produced. The count cannot reach 0 while the generator still emits-then-scrubs the
/// documented internal-receiver case (<c>InternalHolder.describe</c>); closing that at emission is
/// the rejected receiver-aware gate / Step 8, not a harness strip.</para>
/// </summary>
public record WrapperStripManifest
{
    /// <summary>Which wrapper-build leg produced this manifest: a platform name (<c>ios</c>/<c>macos</c>/…), <c>device-main</c>, or <c>device-dep</c>.</summary>
    [JsonPropertyName("site")] public string Site { get; init; } = "";

    /// <summary>Total blocks <c>Process</c> stripped across all wrapper files. Gated against the committed baseline (fail-on-increase).</summary>
    [JsonPropertyName("stripped_block_total")] public int StrippedBlockTotal { get; init; }

    /// <summary>Per-sub-cause counts (InternalType / NSInvocation / Other), sorted for stable diffs. Sums to <see cref="StrippedBlockTotal"/>.</summary>
    [JsonPropertyName("by_sub_cause")] public IReadOnlyList<SubCauseCount> BySubCause { get; init; } = new List<SubCauseCount>();

    /// <summary>The <c>@_cdecl</c>/<c>@_silgen_name</c> symbols <c>Process</c> removed, sorted.</summary>
    [JsonPropertyName("stripped_symbols")] public IReadOnlyList<string> StrippedSymbols { get; init; } = new List<string>();

    public record SubCauseCount
    {
        [JsonPropertyName("sub_cause")] public string SubCause { get; init; } = "";
        [JsonPropertyName("count")] public int Count { get; init; }
    }

    /// <summary>
    /// Builds the manifest from aggregated <c>Process</c> output. The caller converts the
    /// generator's <c>StripSubCause</c> enum to strings so this Models type stays
    /// generator-assembly-agnostic.
    /// </summary>
    public static WrapperStripManifest Build(
        string site,
        int strippedBlockTotal,
        IReadOnlyDictionary<string, int> bySubCause,
        IEnumerable<string> strippedSymbols)
        => new()
        {
            Site = site,
            StrippedBlockTotal = strippedBlockTotal,
            BySubCause = bySubCause
                .Where(kv => kv.Value > 0)
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new SubCauseCount { SubCause = kv.Key, Count = kv.Value })
                .ToList(),
            StrippedSymbols = strippedSymbols.OrderBy(s => s, StringComparer.Ordinal).ToList(),
        };

    public void Save(AbsolutePath path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }) + "\n");
}
