// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

public class SkipTriageBuilderTests
{
    private static SkippedItem Item(SkipReason reason, string name, string? details = null,
        BindingItemKind kind = BindingItemKind.Method, string? containingType = null) => new()
    {
        Kind = kind,
        Name = name,
        ContainingType = containingType,
        Reason = reason,
        Details = details,
    };

    private static List<SkippedItem> MixedList() => new()
    {
        // 3 expected-nonpublic
        Item(SkipReason.ModuleInternal, "a"),
        Item(SkipReason.ModuleInternal, "b"),
        Item(SkipReason.UnderscorePrefixInternal, "c"),
        // 2 expected-structural
        Item(SkipReason.SynthesizedCodable, "d"),
        Item(SkipReason.StaticProtocolMember, "e"),
        // 2 known-limitation
        Item(SkipReason.UnsupportedExistential, "f"),
        Item(SkipReason.AnyTypeFallback, "g"),
        // 2 review (one plain, one EveryProtocol refined to review via details)
        Item(SkipReason.Unknown, "h"),
        Item(SkipReason.EveryProtocolConformanceSkipped, "iProxy",
            details: "Protocol proxy skipped: EveryProtocol conformance was not emitted (no decision recorded).",
            kind: BindingItemKind.Type, containingType: "Demo.I"),
        // 1 EveryProtocol refined AWAY from review (module-internal) → expected-nonpublic
        Item(SkipReason.EveryProtocolConformanceSkipped, "jProxy",
            details: "Protocol proxy skipped: EveryProtocol conformance was not emitted (module-internal protocol).",
            kind: BindingItemKind.Type),
    };

    [Fact]
    public void Build_CountsByDisposition()
    {
        var summary = SkipTriageBuilder.Build(MixedList());

        Assert.Equal(10, summary.Total);
        // 3 ModuleInternal/Underscore + 1 EveryProtocol(module-internal) = 4 expected-nonpublic
        Assert.Equal(4, summary.ByDisposition["ExpectedNonPublic"]);
        Assert.Equal(2, summary.ByDisposition["ExpectedStructural"]);
        Assert.Equal(2, summary.ByDisposition["KnownLimitation"]);
        Assert.Equal(2, summary.ByDisposition["Review"]);
    }

    [Fact]
    public void Build_ReviewCountAndItems_AreTheLookAtThisSet()
    {
        var summary = SkipTriageBuilder.Build(MixedList());

        Assert.Equal(2, summary.ReviewCount);
        Assert.Equal(2, summary.ReviewItems.Count);
        // The EveryProtocol item refined to review carries its context through.
        Assert.Contains(summary.ReviewItems, i => i.Name == "h" && i.Reason == SkipReason.Unknown);
        Assert.Contains(summary.ReviewItems, i =>
            i.Name == "iProxy" && i.Reason == SkipReason.EveryProtocolConformanceSkipped && i.ContainingType == "Demo.I");
        // The module-internal EveryProtocol proxy must NOT be in review.
        Assert.DoesNotContain(summary.ReviewItems, i => i.Name == "jProxy");
    }

    [Fact]
    public void Build_ReviewItems_PreserveSourceOrder()
    {
        var summary = SkipTriageBuilder.Build(MixedList());

        Assert.Equal(new[] { "h", "iProxy" }, summary.ReviewItems.Select(i => i.Name).ToArray());
    }

    [Fact]
    public void Build_PublicSurfaceLost_ExcludesOnlyExpectedNonPublic()
    {
        var summary = SkipTriageBuilder.Build(MixedList());

        // 10 total - 4 expected-nonpublic = 6 consumer-visible.
        Assert.Equal(6, summary.PublicSurfaceLost);
    }

    [Fact]
    public void Build_ByReason_CountsRawReasons()
    {
        var summary = SkipTriageBuilder.Build(MixedList());

        Assert.Equal(2, summary.ByReason["ModuleInternal"]);
        Assert.Equal(2, summary.ByReason["EveryProtocolConformanceSkipped"]);
        Assert.Equal(1, summary.ByReason["Unknown"]);
    }

    [Fact]
    public void Build_ByDisposition_InsertedInTierOrder()
    {
        var summary = SkipTriageBuilder.Build(MixedList());

        Assert.Equal(
            new[] { "ExpectedNonPublic", "ExpectedStructural", "KnownLimitation", "Review" },
            summary.ByDisposition.Keys.ToArray());
    }

    [Fact]
    public void Build_EmptyList_IsAllZero()
    {
        var summary = SkipTriageBuilder.Build(new List<SkippedItem>());

        Assert.Equal(0, summary.Total);
        Assert.Empty(summary.ByDisposition);
        Assert.Empty(summary.ByReason);
        Assert.Equal(0, summary.ReviewCount);
        Assert.Equal(0, summary.PublicSurfaceLost);
        Assert.Empty(summary.ReviewItems);
    }
}
