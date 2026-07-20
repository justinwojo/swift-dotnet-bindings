// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;

using BindingsGeneration;

namespace BindingsGeneration.Tests;

/// <summary>
/// Pins the pure converged↔recompile parity comparator: identical artifacts agree on every compared
/// axis; a dropped slice, an architecture multiset difference, or a defined-symbol difference each
/// surfaces as a named divergence. Order-insensitivity on architectures and slice matching by id are
/// asserted so the observation cannot false-positive on a benign reordering.
/// </summary>
public class WrapperArtifactParityTests
{
    private static WrapperSliceArtifact Slice(
        string id, IEnumerable<string> archs, IEnumerable<string> symbols) => new()
    {
        SliceId = id,
        Architectures = archs.ToList(),
        DefinedSymbols = new HashSet<string>(symbols),
    };

    private static WrapperSliceArtifact SimSlice(params string[] symbols) =>
        Slice("ios-arm64_x86_64-simulator", new[] { "arm64", "x86_64" }, symbols);

    [Fact]
    public void Compare_IdenticalArtifacts_IsIdentical()
    {
        var a = new[] { SimSlice("$sFoo", "$sBar") };
        var b = new[] { SimSlice("$sFoo", "$sBar") };
        var report = WrapperArtifactParity.Compare(a, b);
        Assert.True(report.IsIdentical);
        Assert.Empty(report.Divergences);
    }

    [Fact]
    public void Compare_ArchitectureOrderDiffersOnly_IsIdentical()
    {
        // lipo -archs order is not meaningful; a reversed arch list must not read as a divergence.
        var a = new[] { Slice("s", new[] { "arm64", "x86_64" }, new[] { "$sFoo" }) };
        var b = new[] { Slice("s", new[] { "x86_64", "arm64" }, new[] { "$sFoo" }) };
        var report = WrapperArtifactParity.Compare(a, b);
        Assert.True(report.IsIdentical);
    }

    [Fact]
    public void Compare_SymbolOrderDiffersOnly_IsIdentical()
    {
        var a = new[] { SimSlice("$sFoo", "$sBar", "$sBaz") };
        var b = new[] { SimSlice("$sBaz", "$sFoo", "$sBar") };
        Assert.True(WrapperArtifactParity.Compare(a, b).IsIdentical);
    }

    [Fact]
    public void Compare_SliceOnlyInConverged_Diverges()
    {
        var a = new[] { SimSlice("$sFoo"), Slice("ios-arm64", new[] { "arm64" }, new[] { "$sFoo" }) };
        var b = new[] { SimSlice("$sFoo") };
        var report = WrapperArtifactParity.Compare(a, b);
        Assert.False(report.IsIdentical);
        Assert.Contains(report.Divergences, d => d.Contains("ios-arm64") && d.Contains("absent from the recompile"));
    }

    [Fact]
    public void Compare_SliceOnlyInRecompile_Diverges()
    {
        var a = new[] { SimSlice("$sFoo") };
        var b = new[] { SimSlice("$sFoo"), Slice("ios-arm64", new[] { "arm64" }, new[] { "$sFoo" }) };
        var report = WrapperArtifactParity.Compare(a, b);
        Assert.False(report.IsIdentical);
        Assert.Contains(report.Divergences, d => d.Contains("absent from the converged artifact"));
    }

    [Fact]
    public void Compare_ArchitectureDropped_Diverges()
    {
        // The exact arch-fold-degrade trap the shared wrapper path guards against: a converged fat
        // slice vs an x86_64-only recompile must be caught.
        var a = new[] { Slice("s", new[] { "arm64", "x86_64" }, new[] { "$sFoo" }) };
        var b = new[] { Slice("s", new[] { "x86_64" }, new[] { "$sFoo" }) };
        var report = WrapperArtifactParity.Compare(a, b);
        Assert.False(report.IsIdentical);
        Assert.Contains(report.Divergences, d => d.Contains("architecture mismatch"));
    }

    [Fact]
    public void Compare_SymbolOnlyInConverged_Diverges()
    {
        var a = new[] { SimSlice("$sFoo", "$sExtra") };
        var b = new[] { SimSlice("$sFoo") };
        var report = WrapperArtifactParity.Compare(a, b);
        Assert.False(report.IsIdentical);
        Assert.Contains(report.Divergences, d => d.Contains("only in the converged artifact") && d.Contains("$sExtra"));
    }

    [Fact]
    public void Compare_SymbolOnlyInRecompile_Diverges()
    {
        var a = new[] { SimSlice("$sFoo") };
        var b = new[] { SimSlice("$sFoo", "$sExtra") };
        var report = WrapperArtifactParity.Compare(a, b);
        Assert.False(report.IsIdentical);
        Assert.Contains(report.Divergences, d => d.Contains("only in the recompile"));
    }

    [Fact]
    public void Compare_DuplicateSliceId_Diverges()
    {
        var a = new[] { SimSlice("$sFoo"), SimSlice("$sFoo") };
        var b = new[] { SimSlice("$sFoo") };
        var report = WrapperArtifactParity.Compare(a, b);
        Assert.False(report.IsIdentical);
        Assert.Contains(report.Divergences, d => d.Contains("more than once"));
    }

    [Fact]
    public void Compare_ManySymbolDifferences_SamplesAndSummarizesTheRest()
    {
        var extra = Enumerable.Range(0, 20).Select(i => $"$sExtra{i:D2}").ToArray();
        var a = new[] { SimSlice(new[] { "$sFoo" }.Concat(extra).ToArray()) };
        var b = new[] { SimSlice("$sFoo") };
        var report = WrapperArtifactParity.Compare(a, b);
        Assert.False(report.IsIdentical);
        var line = Assert.Single(report.Divergences, d => d.Contains("only in the converged artifact"));
        Assert.Contains("20 symbol(s)", line);
        Assert.Contains("more)", line); // sample cap summarized the tail
    }

    [Fact]
    public void Compare_MultipleMatchingSlices_IsIdentical()
    {
        var a = new[] { SimSlice("$sFoo"), Slice("ios-arm64", new[] { "arm64" }, new[] { "$sFoo" }) };
        var b = new[] { Slice("ios-arm64", new[] { "arm64" }, new[] { "$sFoo" }), SimSlice("$sFoo") };
        Assert.True(WrapperArtifactParity.Compare(a, b).IsIdentical);
    }
}
