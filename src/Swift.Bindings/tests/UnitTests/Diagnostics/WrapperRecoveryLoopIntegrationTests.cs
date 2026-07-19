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
}
