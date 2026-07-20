// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;

namespace BindingsGeneration;

/// <summary>
/// One promised slice of a compiled wrapper xcframework, reduced to the identity axes a converged
/// loop artifact and a post-loop recompile must agree on: the slice's architectures and its defined
/// (exported) symbol set. A descriptor is a pure value — the live extraction (<c>lipo -archs</c>,
/// <c>NativeSymbolProbe.ReadDefinedSymbols</c>) happens at the call site and hands the results here,
/// so the comparison itself is toolchain-free and unit-testable.
/// </summary>
public sealed record WrapperSliceArtifact
{
    /// <summary>The slice identity, e.g. <c>ios-arm64-simulator</c>. Slices are matched across the two
    /// artifacts by this id, so it must be the same normalized token on both sides.</summary>
    public required string SliceId { get; init; }

    /// <summary>The slice's architectures (<c>lipo -archs</c>). Compared as a multiset — order does not
    /// matter, but a dropped or extra arch does.</summary>
    public required IReadOnlyList<string> Architectures { get; init; }

    /// <summary>The slice's defined (exported) symbols. Compared as a set.</summary>
    public required IReadOnlySet<string> DefinedSymbols { get; init; }
}

/// <summary>The outcome of comparing a converged wrapper artifact with its post-loop recompile.</summary>
public sealed record WrapperParityReport
{
    /// <summary>True when the two artifacts are identical on every compared axis (slice set,
    /// per-slice architectures, per-slice defined symbols).</summary>
    public required bool IsIdentical { get; init; }

    /// <summary>Human-readable divergence lines, empty when identical. Each names one concrete
    /// disagreement (a slice present on one side only, an architecture multiset mismatch, or a
    /// symbol-set difference with a bounded sample).</summary>
    public required IReadOnlyList<string> Divergences { get; init; }

    /// <summary>A one-line summary suitable for a warning log.</summary>
    public string Summary => IsIdentical
        ? "converged wrapper artifact matches the post-loop recompile on all compared axes"
        : $"converged wrapper artifact diverges from the post-loop recompile in {Divergences.Count} way(s)";
}

/// <summary>
/// Compares a converged (verify-recover loop) wrapper artifact against the post-loop recompile that is
/// still, this wave, the shipped artifact. This is a PURE OBSERVATION verifier: it computes whether the
/// loop's final compile would have produced the same bytes-of-interest as the recompile, and reports
/// disagreement — it does NOT choose which artifact ships. Selection stays with the recompile until a
/// soak shows zero disagreement, at which point the loop's converged artifact becomes obligation 12's
/// evidence directly and the recompile (and this cross-check) retire. Wiring the live extraction and the
/// soak counter is the session-08 removal-trigger's job; this session ships the comparator and its
/// fixtures so the parity question has one honest answer when that wiring lands.
/// </summary>
/// <remarks>
/// Load-command identity — the third axis the design names alongside the symbol table and
/// architectures — is deliberately NOT compared here: the generator has no load-command extractor today,
/// and asserting an identity that was never read would be exactly the "the compiler accepted it"
/// substitution the publication model forbids. The comparator states what it checked (slices, archs,
/// defined symbols) and nothing it did not.
/// </remarks>
public static class WrapperArtifactParity
{
    /// <summary>The most symbol names to name in a single divergence line before summarizing the rest.</summary>
    private const int SymbolSampleCap = 8;

    /// <summary>
    /// Compares the two artifacts slice-for-slice. Returns an identical report only when the slice sets
    /// match and every matched slice agrees on architectures and defined symbols.
    /// </summary>
    public static WrapperParityReport Compare(
        IReadOnlyList<WrapperSliceArtifact> converged,
        IReadOnlyList<WrapperSliceArtifact> recompiled)
    {
        ArgumentNullException.ThrowIfNull(converged);
        ArgumentNullException.ThrowIfNull(recompiled);

        var divergences = new List<string>();

        var convergedBySlice = Index(converged, "converged", divergences);
        var recompiledBySlice = Index(recompiled, "recompiled", divergences);

        var allSlices = convergedBySlice.Keys
            .Union(recompiledBySlice.Keys, StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal);

        foreach (var slice in allSlices)
        {
            var inConverged = convergedBySlice.TryGetValue(slice, out var c);
            var inRecompiled = recompiledBySlice.TryGetValue(slice, out var r);

            if (!inConverged)
            {
                divergences.Add($"slice '{slice}' is present in the recompile but absent from the converged artifact");
                continue;
            }
            if (!inRecompiled)
            {
                divergences.Add($"slice '{slice}' is present in the converged artifact but absent from the recompile");
                continue;
            }

            CompareArchitectures(slice, c!, r!, divergences);
            CompareSymbols(slice, c!, r!, divergences);
        }

        return new WrapperParityReport
        {
            IsIdentical = divergences.Count == 0,
            Divergences = divergences,
        };
    }

    private static Dictionary<string, WrapperSliceArtifact> Index(
        IReadOnlyList<WrapperSliceArtifact> slices, string side, List<string> divergences)
    {
        var map = new Dictionary<string, WrapperSliceArtifact>(StringComparer.Ordinal);
        foreach (var slice in slices)
        {
            if (!map.TryAdd(slice.SliceId, slice))
                divergences.Add($"the {side} artifact lists slice '{slice.SliceId}' more than once");
        }
        return map;
    }

    private static void CompareArchitectures(
        string slice, WrapperSliceArtifact c, WrapperSliceArtifact r, List<string> divergences)
    {
        var cArch = c.Architectures.OrderBy(a => a, StringComparer.Ordinal).ToList();
        var rArch = r.Architectures.OrderBy(a => a, StringComparer.Ordinal).ToList();
        if (!cArch.SequenceEqual(rArch, StringComparer.Ordinal))
        {
            divergences.Add(
                $"slice '{slice}' architecture mismatch: converged [{string.Join(", ", cArch)}] " +
                $"vs recompile [{string.Join(", ", rArch)}]");
        }
    }

    private static void CompareSymbols(
        string slice, WrapperSliceArtifact c, WrapperSliceArtifact r, List<string> divergences)
    {
        var onlyConverged = c.DefinedSymbols.Except(r.DefinedSymbols, StringComparer.Ordinal).ToList();
        var onlyRecompiled = r.DefinedSymbols.Except(c.DefinedSymbols, StringComparer.Ordinal).ToList();

        if (onlyConverged.Count != 0)
            divergences.Add($"slice '{slice}' has {onlyConverged.Count} symbol(s) only in the converged artifact: {Sample(onlyConverged)}");
        if (onlyRecompiled.Count != 0)
            divergences.Add($"slice '{slice}' has {onlyRecompiled.Count} symbol(s) only in the recompile: {Sample(onlyRecompiled)}");
    }

    private static string Sample(IReadOnlyList<string> symbols)
    {
        var ordered = symbols.OrderBy(s => s, StringComparer.Ordinal).ToList();
        if (ordered.Count <= SymbolSampleCap)
            return string.Join(", ", ordered);
        return string.Join(", ", ordered.Take(SymbolSampleCap)) + $", … (+{ordered.Count - SymbolSampleCap} more)";
    }
}
