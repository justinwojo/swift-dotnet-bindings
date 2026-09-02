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
    public void Build_RecoveredRows_CountedAsRecovered_AndExcludedFromPublicSurfaceLost()
    {
        // A CSM-recovered AnyType skip (RecoveredBy populated) must roll up under the Recovered
        // disposition and be subtracted from PublicSurfaceLost alongside ExpectedNonPublic — its typed
        // surface IS callable, so it isn't lost consumer surface.
        var recovered = Item(SkipReason.AnyTypeFallback, "items", kind: BindingItemKind.Property,
            containingType: "Demo.MusicItemCollection");
        recovered.RecoveredBy = new List<string> { "MusicItemCollection<Song>.Items" };

        var list = new List<SkippedItem>
        {
            Item(SkipReason.ModuleInternal, "a"),            // expected-nonpublic
            Item(SkipReason.UnsupportedExistential, "b"),    // known-limitation (public surface lost)
            recovered,                                       // recovered (NOT lost)
        };

        var summary = SkipTriageBuilder.Build(list);

        Assert.Equal(1, summary.ByDisposition["Recovered"]);
        // 3 total - 1 expected-nonpublic - 1 recovered = 1 genuinely-lost surface.
        Assert.Equal(1, summary.PublicSurfaceLost);
        // Recovered rows are never in the review set.
        Assert.DoesNotContain(summary.ReviewItems, i => i.Name == "items");
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
        Assert.Equal(0, summary.DegradedConsumeCount);
        Assert.Empty(summary.DegradedConsumeItems);
    }

    [Fact]
    public void Build_ConsumeDegraded_SurfacedAdditively_WithoutFlippingReviewOrDisposition()
    {
        // A consume-degraded row is a KnownLimitation (attributed, not a bug) — it must NOT inflate
        // ReviewCount. But it also must not be invisible on a ReviewCount==0 report: the additive
        // DegradedConsume callout names it. Details carry the greppable "consume-degraded" site token.
        var consume = Item(SkipReason.SuppressedProxyMemberDegraded, "setThing",
            details: SuppressedProxyReporting.Details(SuppressedProxyReporting.Site.ConsumeDegraded, "Demo.WidgetProxy"),
            containingType: "Demo.Host");
        var list = new List<SkippedItem>
        {
            Item(SkipReason.ModuleInternal, "a"),  // expected-nonpublic
            consume,                               // known-limitation + consume-degraded callout
        };

        var summary = SkipTriageBuilder.Build(list);

        // Disposition unchanged: still KnownLimitation, ReviewCount stays 0.
        Assert.Equal(1, summary.ByDisposition["KnownLimitation"]);
        Assert.Equal(0, summary.ReviewCount);
        Assert.Empty(summary.ReviewItems);
        // Surfaced additively.
        Assert.Equal(1, summary.DegradedConsumeCount);
        Assert.Contains(summary.DegradedConsumeItems, i => i.Name == "setThing" && i.ContainingType == "Demo.Host");
    }

    [Fact]
    public void Build_ProduceThrowAndReceiverFailfast_NotCountedAsConsumeDegraded()
    {
        // The produce-throw arm carries its own SB0006 compile error and the receiver-failfast arm its
        // own fail-fast body — neither is a silent consume position, so the consume callout must exclude
        // them (it keys on the consume-degraded site token, not the shared reason).
        var list = new List<SkippedItem>
        {
            Item(SkipReason.SuppressedProxyMemberDegraded, "getThing",
                details: SuppressedProxyReporting.Details(SuppressedProxyReporting.Site.ProduceThrow, "Demo.WidgetProxy")),
            Item(SkipReason.SuppressedProxyMemberDegraded, "receiveThing",
                details: SuppressedProxyReporting.Details(SuppressedProxyReporting.Site.ReceiverFailFast, "Demo.WidgetProxy")),
        };

        var summary = SkipTriageBuilder.Build(list);

        Assert.Equal(0, summary.DegradedConsumeCount);
        Assert.Empty(summary.DegradedConsumeItems);
    }

    [Fact]
    public void Build_PublishesTheWholeDeclaredButDegradedPredicate_NotJustTheReasonsPresent()
    {
        // The set is a contract for out-of-process consumers that measure lost surface (the coverage
        // ratchet), so it must describe the predicate rather than this input: a corpus that trips none
        // of the reasons still has to learn which reasons to exclude. Asserted against the predicate
        // itself so a newly-classified reason cannot reach the emitted set without reaching this list.
        var expected = Enum.GetValues<SkipReason>()
            .Where(SkipDispositionClassifier.IsDeclaredButDegraded)
            .Select(reason => reason.ToString())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // MixedList() deliberately contains no declared-but-degraded row.
        var summary = SkipTriageBuilder.Build(MixedList());

        Assert.Equal(0, summary.DeclaredButDegradedCount);
        Assert.Equal(expected, summary.DeclaredButDegradedReasons);
    }

    [Fact]
    public void DeclaredButDegraded_MembershipIsFrozen_WideningIsADeliberateAct()
    {
        // Deliberately duplicates the predicate as a literal, which the sibling test above
        // deliberately does not. The BindingTests coverage ratchet excludes these reasons from the
        // surface-loss count it enforces, so widening the predicate narrows a release gate — and an
        // increase-only ratchet cannot notice, because a count that wrongly DROPS stays green.
        // Whoever adds a reason has to change this list in the same commit and argue that the
        // surface it names really does still ship.
        string[] frozen =
        [
            nameof(SkipReason.PropertyWrapperDeclinedDirectPInvoke),
            nameof(SkipReason.ProtocolProxyVtableEmpty),
            nameof(SkipReason.ProtocolWitnessNotDispatchable),
        ];

        var summary = SkipTriageBuilder.Build(MixedList());

        Assert.Equal(frozen.OrderBy(n => n, StringComparer.Ordinal).ToList(),
            summary.DeclaredButDegradedReasons);
    }
}
