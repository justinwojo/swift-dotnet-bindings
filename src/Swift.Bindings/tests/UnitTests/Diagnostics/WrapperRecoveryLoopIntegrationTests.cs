// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

using BindingsGeneration.Diagnostics;

namespace BindingsGeneration.Tests;

/// <summary>
/// End-to-end coverage of the verify-recover loop over <em>real</em> swiftc output: the recorded
/// wrapper-compile captures flow through the real <see cref="SwiftDiagnosticParser"/> and
/// <see cref="DiagnosticAttributor"/> into the real <see cref="WrapperRecoveryController"/>, which
/// drives the loop to its outcome. The controller's own property tests
/// (<see cref="WrapperRecoveryControllerTests"/>) script synthetic <see cref="AttributionResult"/>s to
/// isolate the termination logic; these tests instead let genuine compiler diagnostics — the exact
/// gutter lines, restated carets, and cascade shapes swiftc emits — decide what the loop withdraws and
/// whether it converges. That closes the seam between "the parser/attributor read real output right"
/// and "the controller does the right thing with a real attribution."
/// </summary>
/// <remarks>
/// The driver replays a recorded capture as the loop's first render, then models the re-render that
/// follows a withdrawal: every culprit the real attributor named for a capture is a broken member, so
/// once all of them are on the denylist the re-render drops their <c>@_cdecl</c> blocks and the
/// remaining (healthy) members compile clean — the driver returns <c>null</c> (converged). A capture
/// with no culprit (a global input failure) never clears by withdrawing a leaf, so it is replayed every
/// round and the controller's fail-closed path decides the outcome. This keeps the render/compile step
/// hermetic — no Swift toolchain in the unit suite — while exercising the real diagnostic chain the
/// production <see cref="InEmissionDriver"/> feeds the same controller.
/// </remarks>
public class WrapperRecoveryLoopIntegrationTests
{
    /// <summary>
    /// Drives the controller from a recorded capture: round 1 replays the real parsed+attributed
    /// failure; a later round returns clean once every real culprit has been withdrawn.
    /// </summary>
    private sealed class CaptureReplayDriver : IWrapperRecoveryDriver
    {
        private readonly AttributionResult _capture;
        private readonly ImmutableHashSet<RecoveryUnitId> _culprits;

        public int Rounds { get; private set; }
        public List<ImmutableArray<RecoveryUnitId>> SeenDenylists { get; } = new();

        public CaptureReplayDriver(string fixture)
        {
            var groups = SwiftDiagnosticParser.Parse(AttributionFixtures.Stderr(fixture));
            var attributor = new DiagnosticAttributor(
                new[] { AttributionFixtures.SymbolStep(AttributionFixtures.Source(fixture)) });
            _capture = attributor.Attribute(groups);
            _culprits = _capture.Culprits.ToImmutableHashSet();
        }

        public AttributionResult? RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist)
        {
            ArgumentNullException.ThrowIfNull(denylist);
            Rounds++;
            SeenDenylists.Add(denylist.OrderBy(u => u.Canonical, StringComparer.Ordinal).ToImmutableArray());

            // A capture with no attributed culprit (a global input classification, an unplaceable error)
            // cannot be cleared by withdrawing a leaf, so replay it every round and let the controller's
            // fail-closed path decide. Otherwise, once every real culprit is denied the re-render drops
            // those blocks and the healthy remainder compiles clean.
            if (_culprits.Count == 0)
                return _capture;
            return _culprits.IsSubsetOf(denylist) ? null : _capture;
        }
    }

    /// <summary>
    /// Extends <see cref="CaptureReplayDriver"/> with a real bounded-bisection fallback for a capture the
    /// attributor could not place. Round 1 replays the genuine unattributable failure (proving the loop's
    /// red fail-closed state through the real parser and attributor); when the controller consults the
    /// bisection seam, this driver runs the <em>real</em> <see cref="BoundedBisectionSearch"/> over the
    /// fixture's withdrawable member leaves, using a probe that models the production re-render.
    /// </summary>
    /// <remarks>
    /// The stderr is a genuine file-scope failure — an error swiftc reports outside every <c>@_cdecl</c>
    /// block, which the symbol-anchor step cannot charge to any member, so the real attribution carries
    /// <see cref="AttributionResult.HasUnattributedError"/>. The probe models the same re-render
    /// <see cref="CaptureReplayDriver"/> models for an attributed culprit, extended to shared scaffolding:
    /// one member's emission is what dragged the unattributable construct into the wrapper (the planted
    /// culprit), so a render whose denylist withdraws that member drops the scaffolding and the remainder
    /// compiles clean. Withdrawing any other member does not. The RED baseline runs the same fixture
    /// through the default-seam <see cref="CaptureReplayDriver"/>, which fails closed; the delta is
    /// exactly the bounded search.
    /// </remarks>
    private sealed class BisectionReplayDriver : IWrapperRecoveryDriver
    {
        private readonly AttributionResult _capture;
        private readonly RecoveryUnitId _planted;
        private readonly IReadOnlyList<ImmutableArray<RecoveryUnitId>> _candidateGroups;

        public int Rounds { get; private set; }
        public int BisectCalls { get; private set; }
        public bool CaptureIsUnattributable => _capture.HasUnattributedError;

        public BisectionReplayDriver(string fixture, RecoveryUnitId planted, params RecoveryUnitId[] candidates)
        {
            var groups = SwiftDiagnosticParser.Parse(AttributionFixtures.Stderr(fixture));
            var attributor = new DiagnosticAttributor(
                new[] { AttributionFixtures.SymbolStep(AttributionFixtures.Source(fixture)) });
            _capture = attributor.Attribute(groups);
            _planted = planted;
            _candidateGroups = candidates.Select(ImmutableArray.Create).ToList();
        }

        public AttributionResult? RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist)
        {
            ArgumentNullException.ThrowIfNull(denylist);
            Rounds++;
            // Withdrawing the member whose emission owns the unattributable scaffolding clears the compile;
            // any other denylist replays the same genuine unattributable failure.
            return denylist.Contains(_planted) ? null : _capture;
        }

        public BisectionOutcome AttemptBisection(IReadOnlySet<RecoveryUnitId> denylist)
        {
            BisectCalls++;
            var groups = _candidateGroups
                .Where(g => !g.Any(denylist.Contains))
                .ToList();
            return BoundedBisectionSearch.Run(
                groups,
                subset => RenderCompileAttribute(Union(denylist, subset)) is null);
        }

        private static IReadOnlySet<RecoveryUnitId> Union(
            IReadOnlySet<RecoveryUnitId> denylist, IReadOnlyCollection<RecoveryUnitId> subset)
        {
            var set = new HashSet<RecoveryUnitId>(denylist);
            set.UnionWith(subset);
            return set;
        }
    }

    // ── converging over real captures ───────────────────────────────────────────────────────

    /// <summary>
    /// One genuinely-broken member (two swiftc errors on its line) surfaced through the real chain: the
    /// loop withdraws exactly that member's leaf and the next render compiles clean. The healthy
    /// siblings in the same capture are never touched.
    /// </summary>
    [Fact]
    public void RealCapture_SingleBrokenMember_WithdrawsItAndConverges()
    {
        var driver = new CaptureReplayDriver("SingleBrokenMember");

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.None, result.Cause);
        var withdrawn = Assert.Single(result.Denylist);
        Assert.Equal(AttributionFixtures.UnitForSymbol("SBW_Gadget_rotate"), withdrawn);
        Assert.Equal(2, result.Rounds);
        // The first render saw an empty denylist; the second saw exactly the one broken leaf.
        Assert.Empty(driver.SeenDenylists[0]);
        Assert.Equal(new[] { AttributionFixtures.UnitForSymbol("SBW_Gadget_rotate") }, driver.SeenDenylists[1]);
    }

    /// <summary>
    /// Two independent broken members and one healthy sibling: the real attributor names both culprits
    /// in one round, the loop withdraws both together, and the clean member is never withdrawn.
    /// </summary>
    [Fact]
    public void RealCapture_TwoBrokenMembers_WithdrawsBothKeepsTheHealthyOne()
    {
        var driver = new CaptureReplayDriver("TwoBrokenMembers");

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        Assert.Equal(2, result.Denylist.Length);
        Assert.Contains(AttributionFixtures.UnitForSymbol("SBW_Ledger_credit"), result.Denylist);
        Assert.Contains(AttributionFixtures.UnitForSymbol("SBW_Ledger_debit"), result.Denylist);
        // The healthy sibling compiled clean in the capture, so it is never a culprit and never withdrawn.
        Assert.DoesNotContain(AttributionFixtures.UnitForSymbol("SBW_Ledger_balance"), result.Denylist);
        // Both culprits came out of the first failing round: converged on the second render.
        Assert.Equal(2, result.Rounds);
    }

    /// <summary>
    /// A four-error cascade inside a single member collapses to one culprit through the real chain, so
    /// the loop makes exactly one withdrawal (not four) and converges — cascade hygiene end to end.
    /// </summary>
    [Fact]
    public void RealCapture_CascadeInOneMember_CollapsesToOneWithdrawalAndConverges()
    {
        var driver = new CaptureReplayDriver("CascadeInOneMember");

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        var withdrawn = Assert.Single(result.Denylist);
        Assert.Equal(AttributionFixtures.UnitForSymbol("SBW_Timer_fire"), withdrawn);
        Assert.Equal(2, result.Rounds);
    }

    // ── failing closed over real captures ───────────────────────────────────────────────────

    /// <summary>
    /// A missing input module is not a declaration's fault: the real classifier tags it
    /// InputConfiguration, and the controller fails the module closed on the first round rather than
    /// withdrawing any leaf — no denylist churn, no false "recovery".
    /// </summary>
    [Fact]
    public void RealCapture_MissingModule_FailsClosedAsInputConfiguration()
    {
        var driver = new CaptureReplayDriver("MissingModule");

        var result = WrapperRecoveryController.Run(driver);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.InputConfiguration, result.Cause);
        Assert.Empty(result.Denylist);
        Assert.Empty(result.Blocking);
        // Fails closed on the first render — never spins hoping a leaf withdrawal will clear a bad input.
        Assert.Equal(1, result.Rounds);
    }

    // ── bounded-bisection fallback over a real unattributable capture ─────────────────────────

    /// <summary>
    /// The RED baseline for the bounded-bisection fallback, proven through the real chain: a genuine
    /// file-scope swiftc error the attributor cannot charge to any member surfaces as an unattributable
    /// failure, and a driver with no bisection seam (the default) fails the module closed. This is the
    /// exact state the fallback exists to improve on — captured here so the GREEN test's delta is
    /// unambiguously the search, not a change in how the failure is parsed or attributed.
    /// </summary>
    [Fact]
    public void RealUnattributableCapture_WithoutBisection_FailsClosed()
    {
        var driver = new CaptureReplayDriver("UnattributableFileScope");

        var result = WrapperRecoveryController.Run(driver);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.Unattributable, result.Cause);
        Assert.Empty(result.Denylist);
        Assert.Empty(result.SearchIsolated);
    }

    /// <summary>
    /// The GREEN outcome: the same genuine unattributable capture, but the driver's bounded-bisection
    /// fallback runs the real <see cref="BoundedBisectionSearch"/> over the fixture's three member leaves.
    /// The search isolates exactly the planted culprit within a single-digit probe budget, the controller
    /// withdraws it and records it as search-isolated, and the next render compiles clean — a failure that
    /// fails closed today converges, without any change to the parser or attributor.
    /// </summary>
    [Fact]
    public void RealUnattributableCapture_WithBisection_IsolatesPlantedCulpritAndConverges()
    {
        var alpha = AttributionFixtures.UnitForSymbol("SBW_Probe_alpha");
        var bravo = AttributionFixtures.UnitForSymbol("SBW_Probe_bravo");
        var charlie = AttributionFixtures.UnitForSymbol("SBW_Probe_charlie");
        // 'bravo' is the member whose emission owns the unattributable scaffolding in this model.
        var driver = new BisectionReplayDriver("UnattributableFileScope", planted: bravo, alpha, bravo, charlie);

        // The red state is genuinely unattributable through the real parser+attributor — not a synthetic
        // stand-in — so the search is the only thing that turns fail-closed into convergence.
        Assert.True(driver.CaptureIsUnattributable, "the fixture must reproduce a real unattributable failure");

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.None, result.Cause);
        Assert.True(driver.BisectCalls >= 1, "the unattributable failure must consult the bisection seam");
        // Exactly the planted culprit is isolated — the healthy siblings are never withdrawn.
        var isolated = Assert.Single(result.SearchIsolated);
        Assert.Equal(bravo, isolated);
        Assert.Equal(new[] { bravo }, result.Denylist);
        Assert.DoesNotContain(alpha, result.Denylist);
        Assert.DoesNotContain(charlie, result.Denylist);
    }
}
