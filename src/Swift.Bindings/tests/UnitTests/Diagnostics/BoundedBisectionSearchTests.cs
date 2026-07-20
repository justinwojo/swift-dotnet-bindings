// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

using BindingsGeneration;
using BindingsGeneration.Diagnostics;

namespace BindingsGeneration.Tests;

/// <summary>
/// Property tests for the pure <see cref="BoundedBisectionSearch"/> — the verify-recover loop's
/// fallback when attribution cannot name a culprit. The search never renders anything itself: it is
/// handed a candidate pool and a <c>probeClean</c> delegate that models a render→compile under the base
/// denylist plus a subset. These tests drive it through a scripted probe so its three gates
/// (containment, narrowing, confirmation) and its single-digit probe budget are pinned without a Swift
/// toolchain, exactly as <see cref="WrapperRecoveryControllerTests"/> pins the controller.
/// </summary>
/// <remarks>
/// The probe is deliberately the whole model of "what a re-render would find." A single-culprit probe
/// (<see cref="Probe.NeedingAll"/> with one planted unit) models the common case: exactly one member
/// dragged in the unattributable failure, so the compile clears iff that member is withdrawn. A
/// two-culprit probe models the case the search must decline: no single leaf withdrawal clears the
/// compile, so no isolation can be confirmed.
/// </remarks>
public class BoundedBisectionSearchTests
{
    // ---- builders ----------------------------------------------------------------------------

    private static RecoveryUnitId Leaf(string symbol) =>
        RecoveryUnitId.Create(
            DeclId.Create("Fixture", declPath: null, BindingItemKind.Method, symbol), RecoveryScope.LeafApi);

    private static RecoveryUnitId Accessor(string symbol) =>
        RecoveryUnitId.ForAccessorGroup(
            DeclId.Create("Fixture", declPath: null, BindingItemKind.Property, symbol));

    private static IReadOnlyList<ImmutableArray<RecoveryUnitId>> Singletons(params RecoveryUnitId[] units) =>
        units.Select(ImmutableArray.Create).ToList();

    /// <summary>
    /// A scripted probe: it reports a subset "clean" iff every planted culprit is withdrawn in it, and
    /// records every subset it was asked about so a test can assert what the search actually probed.
    /// </summary>
    private sealed class Probe
    {
        private readonly ImmutableHashSet<RecoveryUnitId> _needed;
        public List<ImmutableHashSet<RecoveryUnitId>> Calls { get; } = new();

        private Probe(IEnumerable<RecoveryUnitId> needed) => _needed = needed.ToImmutableHashSet();

        /// <summary>A probe that is clean iff <paramref name="culprits"/> are all withdrawn.</summary>
        public static Probe NeedingAll(params RecoveryUnitId[] culprits) => new(culprits);

        public bool Clean(IReadOnlyCollection<RecoveryUnitId> subset)
        {
            var set = subset.ToImmutableHashSet();
            Calls.Add(set);
            return _needed.IsSubsetOf(set);
        }
    }

    // ---- single-culprit isolation ------------------------------------------------------------

    [Fact]
    public void SingleCulprit_IsIsolatedAndConfirmedWithinBudget()
    {
        var a = Leaf("alpha");
        var b = Leaf("bravo");
        var c = Leaf("charlie");
        var probe = Probe.NeedingAll(b); // only 'bravo' dragged in the failure

        var outcome = BoundedBisectionSearch.Run(Singletons(a, b, c), probe.Clean);

        Assert.True(outcome.DidIsolate);
        Assert.Equal(new[] { b }, outcome.Isolated);
        // Single-digit budget: containment + a couple narrowing steps + two confirmation probes.
        Assert.True(outcome.ProbesUsed <= BoundedBisectionSearch.DefaultProbeBudget);
        Assert.True(outcome.ProbesUsed < 10, "the search must stay within a single-digit probe budget");
    }

    [Fact]
    public void SingleCulprit_ConfirmationProbesSufficiencyAndNecessity()
    {
        var a = Leaf("alpha");
        var b = Leaf("bravo");
        var probe = Probe.NeedingAll(b);

        var outcome = BoundedBisectionSearch.Run(Singletons(a, b), probe.Clean);

        Assert.Equal(new[] { b }, outcome.Isolated);
        // Sufficiency: the culprit alone was probed and cleared the compile.
        Assert.Contains(probe.Calls, s => s.Count == 1 && s.Contains(b));
        // Necessity: withdrawing every OTHER candidate while retaining the culprit was probed and still failed.
        Assert.Contains(probe.Calls, s => s.Contains(a) && !s.Contains(b));
    }

    [Fact]
    public void AccessorGroupCulprit_IsIsolatableToo()
    {
        var leaf = Leaf("plainMethod");
        var prop = Accessor("brokenProperty");
        var probe = Probe.NeedingAll(prop);

        var outcome = BoundedBisectionSearch.Run(Singletons(leaf, prop), probe.Clean);

        Assert.Equal(new[] { prop }, outcome.Isolated);
    }

    [Fact]
    public void SingleCulprit_InALargerPool_StillIsolatesWithinBudget()
    {
        // A pool of a dozen leaves with one culprit: binary narrowing reaches it in ⌈log2 12⌉ = 4 probes
        // plus containment and the two confirmations — six total, comfortably single-digit.
        var units = Enumerable.Range(0, 12).Select(i => Leaf($"m{i}")).ToArray();
        var probe = Probe.NeedingAll(units[7]);

        var outcome = BoundedBisectionSearch.Run(Singletons(units), probe.Clean);

        Assert.Equal(new[] { units[7] }, outcome.Isolated);
        Assert.True(outcome.ProbesUsed <= BoundedBisectionSearch.DefaultProbeBudget);
    }

    // ---- containment gate --------------------------------------------------------------------

    [Fact]
    public void WithdrawAllStillFails_DeclinesAfterOneProbe()
    {
        // The culprit is not a withdrawable leaf at all — no subset of the candidates clears the compile.
        // The containment probe (withdraw everything) fails, so the search declines before narrowing.
        var a = Leaf("alpha");
        var b = Leaf("bravo");
        var probe = Probe.NeedingAll(Leaf("somethingNotInThePool"));

        var outcome = BoundedBisectionSearch.Run(Singletons(a, b), probe.Clean);

        Assert.False(outcome.DidIsolate);
        Assert.True(outcome.Isolated.IsEmpty);
        Assert.Equal(1, outcome.ProbesUsed); // exactly the one containment probe
    }

    // ---- confirmation gate: multiple culprits ------------------------------------------------

    [Fact]
    public void TwoIndependentCulprits_NoSingleGroupSuffices_Declines()
    {
        // Both 'alpha' and 'charlie' must be withdrawn together to clear the compile. Withdraw-all clears
        // (containment passes), but narrowing lands on a single group whose sole withdrawal never clears
        // — the sufficiency probe fails — so the search declines rather than withdraw an innocent member.
        var a = Leaf("alpha");
        var b = Leaf("bravo");
        var c = Leaf("charlie");
        var probe = Probe.NeedingAll(a, c);

        var outcome = BoundedBisectionSearch.Run(Singletons(a, b, c), probe.Clean);

        Assert.False(outcome.DidIsolate);
        Assert.True(outcome.Isolated.IsEmpty);
    }

    // ---- budget exhaustion -------------------------------------------------------------------

    [Fact]
    public void PoolTooLargeToNarrowWithinBudget_Declines()
    {
        // 300 groups need ⌈log2 300⌉ = 9 narrowing probes on top of containment before a culprit is even
        // named — past the single-digit budget. The search declines rather than overrun it.
        var units = Enumerable.Range(0, 300).Select(i => Leaf($"m{i}")).ToArray();
        var probe = Probe.NeedingAll(units[250]);

        var outcome = BoundedBisectionSearch.Run(Singletons(units), probe.Clean);

        Assert.False(outcome.DidIsolate);
        Assert.True(outcome.ProbesUsed <= BoundedBisectionSearch.DefaultProbeBudget,
            "a declined search must still honour the probe budget");
    }

    [Fact]
    public void CustomBudget_IsHonoured()
    {
        // A budget of 1 can only afford the containment probe; even a single-culprit pool of two cannot be
        // narrowed and confirmed within it, so the search declines.
        var a = Leaf("alpha");
        var b = Leaf("bravo");
        var probe = Probe.NeedingAll(b);

        var outcome = BoundedBisectionSearch.Run(Singletons(a, b), probe.Clean, probeBudget: 1);

        Assert.False(outcome.DidIsolate);
        Assert.True(outcome.ProbesUsed <= 1);
    }

    // ---- group atomicity (dependency-closure respect) ----------------------------------------

    [Fact]
    public void GroupsAreNeverSplit_AProbeWithdrawsAWholeGroupOrNoneOfIt()
    {
        // A needs-closed group {a1,a2} is one candidate; the search must never probe a subset that
        // contains one of its units without the other, or it would split a unit from its dependency
        // closure — the exact hazard the grouping exists to prevent.
        var a1 = Leaf("closureHead");
        var a2 = Leaf("closureDependent");
        var b = Leaf("independentLeaf");
        var group = ImmutableArray.Create(a1, a2);
        var groups = new List<ImmutableArray<RecoveryUnitId>> { group, ImmutableArray.Create(b) };
        var probe = Probe.NeedingAll(a1); // withdrawing the group (which carries a1) clears it

        var outcome = BoundedBisectionSearch.Run(groups, probe.Clean);

        // Every probed subset contains both group members or neither — never exactly one.
        Assert.All(probe.Calls, s => Assert.False(s.Contains(a1) ^ s.Contains(a2),
            "a probe split a needs-closed group"));
        // And the whole group is isolated together, not just the failing member.
        Assert.True(outcome.DidIsolate);
        Assert.Contains(a1, outcome.Isolated);
        Assert.Contains(a2, outcome.Isolated);
    }

    // ---- degenerate inputs -------------------------------------------------------------------

    [Fact]
    public void EmptyCandidatePool_DeclinesWithoutProbing()
    {
        var probe = Probe.NeedingAll(Leaf("anything"));

        var outcome = BoundedBisectionSearch.Run(
            Array.Empty<ImmutableArray<RecoveryUnitId>>(), probe.Clean);

        Assert.False(outcome.DidIsolate);
        Assert.Equal(0, outcome.ProbesUsed);
        Assert.Empty(probe.Calls);
    }

    [Fact]
    public void OnlyEmptyGroups_DeclinesWithoutProbing()
    {
        // A group carrying no unit cannot be withdrawn and would make the containment/narrowing probes
        // ambiguous, so a pool of only-empty groups declines exactly like an empty pool.
        var groups = new List<ImmutableArray<RecoveryUnitId>>
        {
            ImmutableArray<RecoveryUnitId>.Empty,
            default,
        };
        var probe = Probe.NeedingAll(Leaf("anything"));

        var outcome = BoundedBisectionSearch.Run(groups, probe.Clean);

        Assert.False(outcome.DidIsolate);
        Assert.Equal(0, outcome.ProbesUsed);
        Assert.Empty(probe.Calls);
    }

    [Fact]
    public void SingleGroupSolePool_ConfirmsWithoutNecessityProbe()
    {
        // With one candidate group there are no "other" candidates, so the necessity probe is vacuous and
        // skipped: containment proves the culprit is inside the set and sufficiency proves it alone clears
        // the compile. The lone group is isolated.
        var only = Leaf("lonelyCulprit");
        var probe = Probe.NeedingAll(only);

        var outcome = BoundedBisectionSearch.Run(Singletons(only), probe.Clean);

        Assert.Equal(new[] { only }, outcome.Isolated);
        // No subset that excludes the culprit was ever probed (there is nothing else to withhold).
        Assert.DoesNotContain(probe.Calls, s => !s.Contains(only));
    }

    // ---- guards ------------------------------------------------------------------------------

    [Fact]
    public void NullCandidateGroups_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            BoundedBisectionSearch.Run(null!, _ => true));

    [Fact]
    public void NullProbe_Throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            BoundedBisectionSearch.Run(Singletons(Leaf("a")), null!));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveBudget_Throws(int budget) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BoundedBisectionSearch.Run(Singletons(Leaf("a")), _ => true, budget));

    // ---- outcome helpers ---------------------------------------------------------------------

    [Fact]
    public void DeclinedOutcome_IsNotAnIsolation()
    {
        var declined = BisectionOutcome.Declined(3);
        Assert.False(declined.DidIsolate);
        Assert.True(declined.Isolated.IsEmpty);
        Assert.Equal(3, declined.ProbesUsed);
    }
}
