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
/// Property tests for the wave-1 verify-recover controller. Each of the loop's non-negotiable
/// properties — monotonic progress, one removal channel, target-slice consistency, fail-closed
/// escalation for coarse scopes and input failures — is pinned by a test that drives the pure
/// controller through a scripted <see cref="IWrapperRecoveryDriver"/> and asserts the outcome.
/// </summary>
/// <remarks>
/// The driver is a fake: it never runs swiftc. It returns a scripted sequence of
/// <see cref="AttributionResult"/>s (or reacts to the denylist it is handed) so the controller's
/// termination and withdrawal logic is exercised without a Swift toolchain — the whole reason the
/// controller and the render/compile seam are split.
/// </remarks>
public class WrapperRecoveryControllerTests
{
    // ---- unit builders -----------------------------------------------------------------------

    private static RecoveryUnitId Leaf(string symbol) =>
        RecoveryUnitId.Create(AttributionFixtures.DeclForSymbol(symbol), RecoveryScope.LeafApi);

    private static RecoveryUnitId Accessor(string symbol) =>
        RecoveryUnitId.Create(AttributionFixtures.DeclForSymbol(symbol), RecoveryScope.AccessorGroup);

    private static RecoveryUnitId Coarse(string symbol, RecoveryScope scope) =>
        RecoveryUnitId.Create(AttributionFixtures.DeclForSymbol(symbol), scope);

    // ---- attribution-result builders ---------------------------------------------------------

    private static DiagnosticGroup ErrorGroup(string message) =>
        new()
        {
            Primary = new CompilerDiagnostic
            {
                File = "Wrapper.swift",
                Line = 1,
                Column = 1,
                Severity = DiagnosticSeverity.Error,
                Message = message,
            },
        };

    /// <summary>
    /// A failure that attributes each of <paramref name="culprits"/> to a distinct unit. One error
    /// group per culprit (message keyed on the unit's canonical form) so the fingerprint is an honest
    /// digest of exactly this culprit set — the same builder the real attributor's fingerprint sees.
    /// </summary>
    private static AttributionResult AttributedFailure(params RecoveryUnitId[] culprits)
    {
        var groups = culprits.Select(u => ErrorGroup($"cannot bind {u.Canonical}")).ToList();
        var diagnostics = culprits
            .Select((u, i) => new AttributedDiagnostic
            {
                Diagnostic = groups[i],
                Kind = AttributionKind.Unit,
                Artifact = ArtifactId.Create(u.Decl, ArtifactRole.SwiftWrapper),
                Unit = u,
                Source = ProvenanceSource.SymbolAnchor,
            })
            .ToImmutableArray();

        return new AttributionResult
        {
            Diagnostics = diagnostics,
            Culprits = culprits.Distinct().ToImmutableArray(),
            Fingerprint = DiagnosticFingerprint.Compute(groups),
        };
    }

    /// <summary>A failure whose one error resolved to no unit and no classification.</summary>
    private static AttributionResult UnattributableFailure(string message = "internal compiler error")
    {
        var group = ErrorGroup(message);
        return new AttributionResult
        {
            Diagnostics = ImmutableArray.Create(new AttributedDiagnostic
            {
                Diagnostic = group,
                Kind = AttributionKind.Unattributed,
                Source = ProvenanceSource.None,
            }),
            Culprits = ImmutableArray<RecoveryUnitId>.Empty,
            Fingerprint = DiagnosticFingerprint.Compute(new[] { group }),
        };
    }

    /// <summary>A global input failure: an error classified to a cause outside any declaration.</summary>
    private static AttributionResult InputConfigurationFailure(string missingModule = "Foo")
    {
        var group = ErrorGroup($"no such module '{missingModule}'");
        return new AttributionResult
        {
            Diagnostics = ImmutableArray.Create(new AttributedDiagnostic
            {
                Diagnostic = group,
                Kind = AttributionKind.Classification,
                Owner = CauseOwner.InputConfiguration,
                ClassificationDetail = missingModule,
                Source = ProvenanceSource.None,
            }),
            Culprits = ImmutableArray<RecoveryUnitId>.Empty,
            Fingerprint = DiagnosticFingerprint.Compute(new[] { group }),
        };
    }

    /// <summary>
    /// A failure attributing every culprit under one <em>shared</em> message, so the fingerprint is
    /// identical no matter which culprit surfaced. Models a real swiftc cascade whose template text
    /// ("cannot find type 'X' in scope") repeats verbatim across members — the case where progress
    /// must be read from the fresh culprit, not the message digest.
    /// </summary>
    private static AttributionResult AttributedFailureWithMessage(string message, params RecoveryUnitId[] culprits)
    {
        var groups = culprits.Select(_ => ErrorGroup(message)).ToList();
        var diagnostics = culprits
            .Select((u, i) => new AttributedDiagnostic
            {
                Diagnostic = groups[i],
                Kind = AttributionKind.Unit,
                Artifact = ArtifactId.Create(u.Decl, ArtifactRole.SwiftWrapper),
                Unit = u,
                Source = ProvenanceSource.SymbolAnchor,
            })
            .ToImmutableArray();

        return new AttributionResult
        {
            Diagnostics = diagnostics,
            Culprits = culprits.Distinct().ToImmutableArray(),
            Fingerprint = DiagnosticFingerprint.Compute(groups),
        };
    }

    /// <summary>
    /// A mixed union: a global input-classification error (missing module) <em>and</em> an attributed
    /// leaf culprit in the same failure. The real attributor classifies missing modules per diagnostic
    /// while still attributing other errors to units, so this shape is reachable in production.
    /// </summary>
    private static AttributionResult MixedInputAndLeafFailure(RecoveryUnitId leaf, string missingModule = "Foo")
    {
        var moduleGroup = ErrorGroup($"no such module '{missingModule}'");
        var leafGroup = ErrorGroup($"cannot bind {leaf.Canonical}");
        var diagnostics = ImmutableArray.Create(
            new AttributedDiagnostic
            {
                Diagnostic = moduleGroup,
                Kind = AttributionKind.Classification,
                Owner = CauseOwner.InputConfiguration,
                ClassificationDetail = missingModule,
                Source = ProvenanceSource.None,
            },
            new AttributedDiagnostic
            {
                Diagnostic = leafGroup,
                Kind = AttributionKind.Unit,
                Artifact = ArtifactId.Create(leaf.Decl, ArtifactRole.SwiftWrapper),
                Unit = leaf,
                Source = ProvenanceSource.SymbolAnchor,
            });

        return new AttributionResult
        {
            Diagnostics = diagnostics,
            Culprits = ImmutableArray.Create(leaf),
            Fingerprint = DiagnosticFingerprint.Compute(new[] { moduleGroup, leafGroup }),
        };
    }

    /// <summary>
    /// A mixed union: an unplaceable (unattributed) error <em>and</em> an attributed leaf culprit in
    /// the same failure — a cascade where one error tied to no unit sits beside one that did.
    /// </summary>
    private static AttributionResult MixedUnattributableAndLeafFailure(RecoveryUnitId leaf)
    {
        var unattrGroup = ErrorGroup("internal compiler error");
        var leafGroup = ErrorGroup($"cannot bind {leaf.Canonical}");
        var diagnostics = ImmutableArray.Create(
            new AttributedDiagnostic
            {
                Diagnostic = unattrGroup,
                Kind = AttributionKind.Unattributed,
                Source = ProvenanceSource.None,
            },
            new AttributedDiagnostic
            {
                Diagnostic = leafGroup,
                Kind = AttributionKind.Unit,
                Artifact = ArtifactId.Create(leaf.Decl, ArtifactRole.SwiftWrapper),
                Unit = leaf,
                Source = ProvenanceSource.SymbolAnchor,
            });

        return new AttributionResult
        {
            Diagnostics = diagnostics,
            Culprits = ImmutableArray.Create(leaf),
            Fingerprint = DiagnosticFingerprint.Compute(new[] { unattrGroup, leafGroup }),
        };
    }

    // ---- drivers -----------------------------------------------------------------------------

    /// <summary>Returns a scripted sequence of rounds, recording every denylist it was handed.</summary>
    private sealed class ScriptedDriver : IWrapperRecoveryDriver
    {
        private readonly Queue<AttributionResult?> _rounds;
        public List<ImmutableArray<RecoveryUnitId>> SeenDenylists { get; } = new();

        public ScriptedDriver(params AttributionResult?[] rounds) => _rounds = new(rounds);

        public AttributionResult? RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist)
        {
            SeenDenylists.Add(denylist.OrderBy(u => u.Canonical, StringComparer.Ordinal).ToImmutableArray());
            if (_rounds.Count == 0)
                throw new InvalidOperationException("driver ran out of scripted rounds — controller iterated further than expected.");
            return _rounds.Dequeue();
        }
    }

    /// <summary>Decides each round from the denylist it is handed, so tests can model real convergence.</summary>
    private sealed class PolicyDriver : IWrapperRecoveryDriver
    {
        private readonly Func<IReadOnlySet<RecoveryUnitId>, AttributionResult?> _policy;
        public List<ImmutableArray<RecoveryUnitId>> SeenDenylists { get; } = new();

        public PolicyDriver(Func<IReadOnlySet<RecoveryUnitId>, AttributionResult?> policy) => _policy = policy;

        public AttributionResult? RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist)
        {
            SeenDenylists.Add(denylist.OrderBy(u => u.Canonical, StringComparer.Ordinal).ToImmutableArray());
            return _policy(denylist);
        }
    }

    // ---- convergence -------------------------------------------------------------------------

    [Fact]
    public void CleanFirstCompile_ConvergesWithEmptyDenylist()
    {
        var driver = new ScriptedDriver((AttributionResult?)null);

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.None, result.Cause);
        Assert.Empty(result.Denylist);
        Assert.Equal(1, result.Rounds);
    }

    [Fact]
    public void LeafFailure_WithdrawnThenCleanCompile_Converges()
    {
        var bad = Leaf("brokenMember");
        // The driver models a real render: once the culprit is denied, the next render compiles clean.
        var driver = new PolicyDriver(denylist =>
            denylist.Contains(bad) ? null : AttributedFailure(bad));

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        Assert.Equal(new[] { bad }, result.Denylist);
        Assert.Equal(2, result.Rounds);
    }

    [Fact]
    public void MultipleLeafCulprits_AllWithdrawnInOneRound_Converges()
    {
        var a = Leaf("alpha");
        var b = Leaf("bravo");
        var driver = new PolicyDriver(denylist =>
            (denylist.Contains(a) && denylist.Contains(b)) ? null : AttributedFailure(a, b));

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        Assert.Equal(2, result.Denylist.Length);
        Assert.Contains(a, result.Denylist);
        Assert.Contains(b, result.Denylist);
        // Both culprits came out of the first failing round: converged on the second render.
        Assert.Equal(2, result.Rounds);
    }

    [Fact]
    public void AccessorGroupCulprit_IsRecoverable()
    {
        var prop = Accessor("someProperty");
        var driver = new PolicyDriver(denylist =>
            denylist.Contains(prop) ? null : AttributedFailure(prop));

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        Assert.Equal(new[] { prop }, result.Denylist);
    }

    [Fact]
    public void StagedLeafFailures_EachRoundSurfacesTheNext_Converges()
    {
        var a = Leaf("first");
        var b = Leaf("second");
        // b only surfaces after a is withdrawn (a real cascade-behind-a-cascade): still monotonic.
        var driver = new PolicyDriver(denylist =>
        {
            if (!denylist.Contains(a))
                return AttributedFailure(a);
            if (!denylist.Contains(b))
                return AttributedFailure(b);
            return null;
        });

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        Assert.Equal(new[] { a, b }, result.Denylist);
        Assert.Equal(3, result.Rounds);
    }

    [Fact]
    public void StagedLeafFailures_IdenticalDiagnosticText_StillConverges()
    {
        // A real swiftc cascade reuses the SAME diagnostic template across members, so the failure
        // fingerprint is identical round to round even as a new leaf surfaces. Progress is measured by
        // the fresh culprit, not the fingerprint: the newly-exposed leaf must still be withdrawn, and
        // the loop must not abort on the repeated message digest.
        var a = Leaf("first");
        var b = Leaf("second");
        const string sameText = "cannot find type 'Missing' in scope";
        var driver = new PolicyDriver(denylist =>
        {
            if (!denylist.Contains(a))
                return AttributedFailureWithMessage(sameText, a);
            if (!denylist.Contains(b))
                return AttributedFailureWithMessage(sameText, b);
            return null;
        });

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        Assert.Equal(new[] { a, b }, result.Denylist);
        Assert.Equal(3, result.Rounds);
    }

    // ---- one channel / monotonic progress ----------------------------------------------------

    [Fact]
    public void EveryDenylistHandedToDriver_IsMonotonicAndAdditiveOnly()
    {
        var a = Leaf("first");
        var b = Leaf("second");
        var driver = new PolicyDriver(denylist =>
        {
            if (!denylist.Contains(a))
                return AttributedFailure(a);
            if (!denylist.Contains(b))
                return AttributedFailure(b);
            return null;
        });

        var result = WrapperRecoveryController.Run(driver);
        Assert.True(result.Converged);

        // The only removal channel is the denylist, and it only ever grows: each successive denylist
        // handed to the driver is a strict superset of the one before. Nothing is ever re-enabled.
        for (int i = 1; i < driver.SeenDenylists.Count; i++)
        {
            var prev = driver.SeenDenylists[i - 1];
            var curr = driver.SeenDenylists[i];
            Assert.True(prev.Length < curr.Length, "denylist did not grow between rounds");
            Assert.True(prev.All(curr.Contains), "a previously-withdrawn unit was dropped from the denylist");
        }
    }

    [Fact]
    public void RepeatedCulprit_IsNeverWithdrawnTwice()
    {
        var a = Leaf("only");
        var driver = new PolicyDriver(denylist =>
            denylist.Contains(a) ? null : AttributedFailure(a));

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        // Even though 'a' is a culprit and then denied, it appears exactly once in the settled denylist.
        Assert.Single(result.Denylist);
        Assert.Equal(a, result.Denylist[0]);
    }

    // ---- target-slice consistency ------------------------------------------------------------

    [Fact]
    public void Denylist_IsSliceAgnostic_AppliedIdenticallyEveryRound()
    {
        // A unit that failed on "one slice" is withdrawn globally: the controller holds one denylist,
        // keyed on the unit alone, and hands that same set to every render. There is no per-slice
        // denylist that could diverge — this test pins that the driver only ever sees the one set.
        var deviceOnly = Leaf("deviceOnlyBreakage");
        var driver = new PolicyDriver(denylist =>
            denylist.Contains(deviceOnly) ? null : AttributedFailure(deviceOnly));

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        // First render: empty. Second render: exactly {deviceOnly}. No slice qualifier anywhere.
        Assert.Equal(2, driver.SeenDenylists.Count);
        Assert.Empty(driver.SeenDenylists[0]);
        Assert.Equal(new[] { deviceOnly }, driver.SeenDenylists[1]);
    }

    // ---- fail-closed: coarse scopes need the recovery graph ----------------------------------

    [Theory]
    [InlineData(RecoveryScope.ForwardProtocolView)]
    [InlineData(RecoveryScope.ManagedProtocolConformance)]
    [InlineData(RecoveryScope.ConformanceEdge)]
    [InlineData(RecoveryScope.SharedHelperBundle)]
    [InlineData(RecoveryScope.TypeRepresentation)]
    [InlineData(RecoveryScope.TypeSurface)]
    [InlineData(RecoveryScope.Module)]
    public void CoarseScopeCulprit_FailsClosedRequiringGraphClosure(RecoveryScope scope)
    {
        var coarse = Coarse("needsClosure", scope);
        var driver = new ScriptedDriver(AttributedFailure(coarse));

        var result = WrapperRecoveryController.Run(driver);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.RequiresGraphClosure, result.Cause);
        Assert.Contains(coarse, result.Blocking);
        // Fail-closed means the coarse unit is NOT withdrawn — no leaf-poisoning of a multi-artifact unit.
        Assert.Empty(result.Denylist);
    }

    [Fact]
    public void MixedLeafAndCoarseCulprits_FailClosedWithoutWithdrawingTheLeaf()
    {
        var leaf = Leaf("wouldBeRecoverable");
        var coarse = Coarse("sharedHelper", RecoveryScope.SharedHelperBundle);
        var driver = new ScriptedDriver(AttributedFailure(leaf, coarse));

        var result = WrapperRecoveryController.Run(driver);

        // A coarse culprit in the same round blocks the whole module: withdrawing the leaf while a
        // coarse unit is stranded is exactly the partial-recovery hazard wave-1 refuses.
        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.RequiresGraphClosure, result.Cause);
        Assert.Contains(coarse, result.Blocking);
        Assert.DoesNotContain(leaf, result.Blocking);
        Assert.Empty(result.Denylist);
    }

    // ---- fail-closed: input configuration ----------------------------------------------------

    [Fact]
    public void GlobalInputFailure_FailsClosedAsInputConfiguration()
    {
        var driver = new ScriptedDriver(InputConfigurationFailure("MissingDep"));

        var result = WrapperRecoveryController.Run(driver);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.InputConfiguration, result.Cause);
        Assert.Empty(result.Denylist);
        Assert.Empty(result.Blocking);
    }

    [Fact]
    public void InputFailure_IsCheckedBeforeNoProgress_NotMislabeled()
    {
        // A missing-module failure attributes nothing, which would also trip the no-progress detector's
        // "attributed nothing" arm. The controller must classify it as InputConfiguration, not NoProgress.
        var driver = new ScriptedDriver(InputConfigurationFailure());

        var result = WrapperRecoveryController.Run(driver);

        Assert.Equal(WrapperRecoveryFailureCause.InputConfiguration, result.Cause);
    }

    [Fact]
    public void MixedInputAndLeafCulprit_FailsClosedAsInputConfiguration_WithoutWithdrawingTheLeaf()
    {
        // The failing union pairs a missing-module classification with a leaf error. The leaf error is
        // almost certainly a cascade of the missing module: withdrawing it is futile (the module error
        // remains) and unsound (it would tombstone a healthy member). The input classification must
        // fail the module closed even though an attributed leaf coexists in the same union.
        var leaf = Leaf("cascadeOfMissingModule");
        var driver = new ScriptedDriver(MixedInputAndLeafFailure(leaf, "MissingDep"));

        var result = WrapperRecoveryController.Run(driver);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.InputConfiguration, result.Cause);
        Assert.Empty(result.Denylist);
        Assert.Empty(result.Blocking);
    }

    // ---- fail-closed: unattributable ---------------------------------------------------------

    [Fact]
    public void UnattributableError_FailsClosed()
    {
        var driver = new ScriptedDriver(UnattributableFailure());

        var result = WrapperRecoveryController.Run(driver);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.Unattributable, result.Cause);
        Assert.Empty(result.Denylist);
    }

    [Fact]
    public void MixedUnattributableAndLeafCulprit_FailsClosedAsUnattributable_WithoutWithdrawingTheLeaf()
    {
        // An unplaceable error alongside an attributed leaf: withdrawing the leaf can never clear the
        // unattributable error, so partial recovery is pointless. Wave-1 fails closed rather than ship
        // a binding that dropped a healthy leaf and still would not compile.
        var leaf = Leaf("besideAnUnplaceableError");
        var driver = new ScriptedDriver(MixedUnattributableAndLeafFailure(leaf));

        var result = WrapperRecoveryController.Run(driver);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.Unattributable, result.Cause);
        Assert.Empty(result.Denylist);
    }

    // ---- no progress -------------------------------------------------------------------------

    [Fact]
    public void RepeatedIdenticalFailure_FailsClosedAsNoProgress()
    {
        // The same attributed culprit twice running with no clean compile. After 'a' is withdrawn the
        // second round re-blames only 'a', which is now denied — no fresh leaf remains to withdraw
        // (the design's D' == D). Wave-1 has no rung above a leaf, so it fails closed.
        var a = Leaf("stubborn");
        // Driver ignores the denylist: the failure never clears even after 'a' is withdrawn.
        var driver = new ScriptedDriver(AttributedFailure(a), AttributedFailure(a));

        var result = WrapperRecoveryController.Run(driver);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.NoProgress, result.Cause);
        // 'a' was withdrawn once on the first round; the repeat produced no new withdrawal.
        Assert.Equal(new[] { a }, result.Denylist);
    }

    [Fact]
    public void RepeatedFingerprintThenCoarseFreshCulprit_FailsClosedRequiringGraphClosure()
    {
        // Round 1 withdraws a leaf; round 2 surfaces a COARSE unit under the identical diagnostic text.
        // A fingerprint-first termination would swallow the coarse blocker as "no progress" and hide
        // it. Progress is read from the fresh culprit set, so the coarse unit is seen: it still needs
        // the recovery graph, so the cause is RequiresGraphClosure with the coarse unit in Blocking —
        // and the leaf legitimately withdrawn in round 1 stays in the denylist.
        var leaf = Leaf("firstLeaf");
        var coarse = Coarse("sharedHelper", RecoveryScope.SharedHelperBundle);
        const string sameText = "cannot find type 'Missing' in scope";
        var driver = new PolicyDriver(denylist =>
            denylist.Contains(leaf)
                ? AttributedFailureWithMessage(sameText, coarse)
                : AttributedFailureWithMessage(sameText, leaf));

        var result = WrapperRecoveryController.Run(driver);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.RequiresGraphClosure, result.Cause);
        Assert.Contains(coarse, result.Blocking);
        Assert.Equal(new[] { leaf }, result.Denylist);
    }

    [Fact]
    public void AllFreshCulpritsAlreadyDenied_FailsClosedAsNoProgress()
    {
        // Round 2 re-blames the same unit but with a *different* error text, so the fingerprint differs
        // (no repeat) yet there is no fresh culprit to withdraw. That is still no progress.
        var a = Leaf("recurring");
        var round1 = AttributedFailure(a);
        var round2 = new AttributionResult
        {
            Diagnostics = ImmutableArray.Create(new AttributedDiagnostic
            {
                Diagnostic = ErrorGroup("a differently-worded error blaming the same already-denied unit"),
                Kind = AttributionKind.Unit,
                Artifact = ArtifactId.Create(a.Decl, ArtifactRole.SwiftWrapper),
                Unit = a,
                Source = ProvenanceSource.SymbolAnchor,
            }),
            Culprits = ImmutableArray.Create(a),
            Fingerprint = DiagnosticFingerprint.Compute(new[] { ErrorGroup("a differently-worded error blaming the same already-denied unit") }),
        };
        var driver = new ScriptedDriver(round1, round2);

        var result = WrapperRecoveryController.Run(driver);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.NoProgress, result.Cause);
        Assert.Equal(new[] { a }, result.Denylist);
    }

    // ---- cross-verifier joint fixed-point (a C# withdrawal that breaks a Swift sibling) -------

    [Fact]
    public void CSharpWithdrawalThatBreaksASwiftSibling_ConvergesWithBothWithdrawn_WithoutOscillating()
    {
        // The joint fixed-point across BOTH verifiers, seen at the controller layer where it is
        // verifier-agnostic: X is the C#-plane culprit; withdrawing X removes its Swift wrapper too,
        // which exposes a Swift-plane sibling Y that only fails once X is gone. A non-monotonic loop
        // that re-rendered pristine and "forgot" X when Y surfaced would re-break X on the very next
        // render and oscillate {X} ⇄ {Y} forever. The controller holds ONE monotonic denylist across
        // both planes, so it settles at {X, Y} — both withdrawn, clean under both verifiers.
        var x = Leaf("cSharpCulprit");   // fails the C# compile whenever it is present
        var y = Leaf("swiftSibling");    // fails the Swift wrapper once X is withdrawn
        var driver = new PolicyDriver(denylist =>
        {
            if (!denylist.Contains(x))
                return AttributedFailure(x);   // C#-plane failure on X
            if (!denylist.Contains(y))
                return AttributedFailure(y);   // withdrawing X broke the Swift sibling Y
            return null;                       // both withdrawn ⇒ clean under both verifiers
        });

        var result = WrapperRecoveryController.Run(driver);

        Assert.True(result.Converged);
        Assert.Equal(new[] { x, y }, result.Denylist);
        // Three renders: {} → fail(X) → {X} → fail(Y) → {X,Y} → clean. The middle render is exactly the
        // one a non-monotonic loop would have dropped X from, re-breaking the C# compile.
        Assert.Equal(3, result.Rounds);

        // Non-oscillation is the property under test: every denylist handed to the driver grew and never
        // re-enabled a withdrawn unit, so the C# culprit X is never rendered again once withdrawn.
        for (int i = 1; i < driver.SeenDenylists.Count; i++)
        {
            var prev = driver.SeenDenylists[i - 1];
            var curr = driver.SeenDenylists[i];
            Assert.True(prev.Length < curr.Length, "denylist did not grow — a re-enable would oscillate");
            Assert.True(prev.All(curr.Contains), "a withdrawn unit was re-enabled — the C# culprit would re-break");
        }
        // From the second render onward X stays withdrawn; the driver never sees a denylist that dropped
        // it, which is precisely what stops the {X} ⇄ {Y} oscillation.
        Assert.All(driver.SeenDenylists.Skip(1), d => Assert.Contains(x, d));
    }

    // ---- iteration cap -----------------------------------------------------------------------

    [Fact]
    public void ProgressingButNeverConverging_ExhaustsCapAndFailsClosed()
    {
        // Every round withdraws a genuinely new leaf, so the fresh-culprit set is never empty and the
        // no-progress terminus never fires — the run keeps making progress but never reaches a clean
        // compile. The cap is the floor that stops it.
        var driver = new PolicyDriver(denylist =>
            AttributedFailure(Leaf($"leaf{denylist.Count}")));

        var result = WrapperRecoveryController.Run(driver, iterationCap: 3);

        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.IterationCapExhausted, result.Cause);
        Assert.Equal(3, result.Rounds);
        // Made progress on every round: one new withdrawal per iteration up to the cap.
        Assert.Equal(3, result.Denylist.Length);
    }

    [Fact]
    public void IterationCapOfOne_StillWithdrawsThatRoundsCulpritsBeforeStopping()
    {
        var a = Leaf("once");
        var driver = new PolicyDriver(denylist =>
            denylist.Contains(a) ? null : AttributedFailure(a));

        var result = WrapperRecoveryController.Run(driver, iterationCap: 1);

        // One render only: it fails and attributes 'a', but there is no second render to confirm the
        // clean compile, so it exhausts the cap. The withdrawal it made is still reported.
        Assert.False(result.Converged);
        Assert.Equal(WrapperRecoveryFailureCause.IterationCapExhausted, result.Cause);
        Assert.Equal(new[] { a }, result.Denylist);
    }

    // ---- guards ------------------------------------------------------------------------------

    [Fact]
    public void NullDriver_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WrapperRecoveryController.Run(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveIterationCap_Throws(int cap)
    {
        var driver = new ScriptedDriver((AttributionResult?)null);
        Assert.Throws<ArgumentOutOfRangeException>(() => WrapperRecoveryController.Run(driver, cap));
    }

    [Theory]
    [InlineData(RecoveryScope.LeafApi, true)]
    [InlineData(RecoveryScope.AccessorGroup, true)]
    [InlineData(RecoveryScope.ForwardProtocolView, false)]
    [InlineData(RecoveryScope.ManagedProtocolConformance, false)]
    [InlineData(RecoveryScope.ConformanceEdge, false)]
    [InlineData(RecoveryScope.SharedHelperBundle, false)]
    [InlineData(RecoveryScope.TypeRepresentation, false)]
    [InlineData(RecoveryScope.TypeSurface, false)]
    [InlineData(RecoveryScope.Module, false)]
    public void IsLeafRecoverable_OnlyLeafAndAccessorGroup(RecoveryScope scope, bool expected)
    {
        Assert.Equal(expected, WrapperRecoveryController.IsLeafRecoverable(scope));
    }
}
