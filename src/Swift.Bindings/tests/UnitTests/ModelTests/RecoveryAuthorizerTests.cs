// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The authorized entry point in front of <see cref="RecoveryPolicy"/>: a coarse withdrawal passes only
/// when its scope is witnessable and the capture is complete, and the completeness gate makes the
/// wave-1 false-safe hole (a <c>Safe</c> verdict on a graph missing a real dependency edge) impossible
/// to reach.
/// </summary>
public class RecoveryAuthorizerTests
{
    private static DeclId Type(string name) =>
        DeclId.Create("M", string.Empty, BindingItemKind.Type, name);

    private static DeclId Method(string name) =>
        DeclId.Create("M", "T", BindingItemKind.Method, name);

    private static DeclId Module() =>
        DeclId.Create("M", string.Empty, BindingItemKind.Module, "M");

    private static IReadOnlySet<RecoveryUnitId> Retained(params RecoveryUnitId[] ids) => ids.ToHashSet();

    // ── The false-safe hole is unreachable through the authorizer ──────────────────────────────

    /// <summary>
    /// The red-first pin. A consumer leaf references a provider leaf's symbol, but the graph never
    /// captured the edge. The pure policy is false-safe — it sees no modelled dependent and says
    /// <c>Safe</c> — but the witness proves the reference exists, so the completeness gate refuses and
    /// the authorizer never reaches <c>Safe</c>. Provider scope is deliberately witnessable so it is the
    /// completeness gate, not the scope gate, doing the refusing.
    /// </summary>
    [Fact]
    public void Authorizer_RefusesWithdrawalWhenAWitnessedEdgeWasNotCaptured()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var provider = builder.DeclareUnit(Method("provider"), RecoveryScope.LeafApi, type);
        var consumer = builder.DeclareUnit(Method("consumer"), RecoveryScope.LeafApi, type);
        // Deliberately DO NOT AddRequires(consumer, provider): the graph is edge-incomplete.
        var graph = builder.Build();

        var witness = new[]
        {
            new WitnessedReference
            {
                Consumer = consumer,
                Provider = provider,
                Kind = RecoveryEdgeKind.PInvokeToWrapperSymbol,
                Symbol = "SBW_M_T_provider",
            },
        };

        // The pure policy is false-safe on this partial graph — this is the hole.
        Assert.True(RecoveryPolicy.SafeToDrop(graph, provider, Retained(module, type, consumer)).IsSafe);

        // The completeness gate sees the orphan and the authorizer refuses — Safe is unreachable.
        var completeness = RecoveryCompletenessGate.Check(graph, witness);
        Assert.False(completeness.IsComplete);
        Assert.Single(completeness.Orphans);

        var authorization = RecoveryAuthorizer.SafeToDrop(
            graph, completeness, provider, Retained(module, type, consumer));
        Assert.False(authorization.IsAuthorized);
        Assert.Equal(RecoveryAuthorizationDenial.WitnessIncomplete, authorization.Denial);
    }

    /// <summary>
    /// Once the edge is captured the graph is complete, so the authorizer delegates to the policy — and
    /// the policy now correctly blocks the withdrawal because the retained consumer depends on it. The
    /// authorizer produces the <em>right</em> answer, not a permissive one.
    /// </summary>
    [Fact]
    public void Authorizer_DelegatesToPolicyWhenComplete_AndBlocksARetainedDependent()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var provider = builder.DeclareUnit(Method("provider"), RecoveryScope.LeafApi, type);
        var consumer = builder.DeclareUnit(Method("consumer"), RecoveryScope.LeafApi, type);
        builder.AddRequires(consumer, provider, RecoveryEdgeKind.PInvokeToWrapperSymbol);
        var graph = builder.Build();

        var witness = new[]
        {
            new WitnessedReference
            {
                Consumer = consumer,
                Provider = provider,
                Kind = RecoveryEdgeKind.PInvokeToWrapperSymbol,
                Symbol = "SBW_M_T_provider",
            },
        };

        var completeness = RecoveryCompletenessGate.Check(graph, witness);
        Assert.True(completeness.IsComplete);

        var blocked = RecoveryAuthorizer.SafeToDrop(
            graph, completeness, provider, Retained(module, type, consumer));
        Assert.False(blocked.IsAuthorized);
        Assert.Equal(RecoveryAuthorizationDenial.PolicyBlocked, blocked.Denial);
        Assert.Equal(RecoveryObstruction.RetainedDependent, blocked.PolicyVerdict.Obstruction);

        // With the dependent also leaving, the same withdrawal is authorized.
        var authorized = RecoveryAuthorizer.SafeToDrop(graph, completeness, provider, Retained(module, type));
        Assert.True(authorized.IsAuthorized);
    }

    // ── Completeness gate: per-occurrence, orphan-fail-closed ──────────────────────────────────

    /// <summary>
    /// Per-occurrence is the load-bearing property. Two consumers reference the same provider symbol;
    /// only one edge is captured. A symbol-membership check would pass (the symbol is referenced-by-
    /// someone); the per-occurrence gate catches the uncaptured second consumer as an orphan.
    /// </summary>
    [Fact]
    public void Completeness_SharedSymbolWithOneUncapturedConsumer_IsOrphan()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var helper = builder.DeclareSharedHelper(Module(), "utf8", module);
        var a = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        var b = builder.DeclareUnit(Method("b"), RecoveryScope.LeafApi, type);
        builder.AddRequires(a, helper, RecoveryEdgeKind.HelperCall);
        // b also references the helper symbol but its edge was never captured.
        var graph = builder.Build();

        var witness = new[]
        {
            new WitnessedReference { Consumer = a, Provider = helper, Kind = RecoveryEdgeKind.HelperCall, Symbol = "SBW_Free_M" },
            new WitnessedReference { Consumer = b, Provider = helper, Kind = RecoveryEdgeKind.HelperCall, Symbol = "SBW_Free_M" },
        };

        var report = RecoveryCompletenessGate.Check(graph, witness);
        Assert.False(report.IsComplete);
        Assert.Single(report.Orphans);
        Assert.Equal(b, report.Orphans[0].Consumer);
    }

    /// <summary>A self-reference strands nothing — the symbol and its reference leave together.</summary>
    [Fact]
    public void Completeness_SelfReference_IsSatisfiedTrivially()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var leaf = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        var graph = builder.Build();

        var witness = new[]
        {
            new WitnessedReference { Consumer = leaf, Provider = leaf, Kind = RecoveryEdgeKind.PInvokeToWrapperSymbol },
        };
        Assert.True(RecoveryCompletenessGate.Check(graph, witness).IsComplete);
    }

    /// <summary>
    /// An <see cref="RecoveryEdgeKind.Unspecified"/> captured edge does not satisfy a witnessable
    /// reference: the conservative default cannot vouch for completeness of the witnessable output.
    /// </summary>
    [Fact]
    public void Completeness_UnspecifiedEdge_DoesNotSatisfyAWitnessableReference()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var helper = builder.DeclareSharedHelper(Module(), "utf8", module);
        var a = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        builder.AddRequires(a, helper); // Unspecified kind
        var graph = builder.Build();

        var witness = new[]
        {
            new WitnessedReference { Consumer = a, Provider = helper, Kind = RecoveryEdgeKind.HelperCall },
        };
        Assert.False(RecoveryCompletenessGate.Check(graph, witness).IsComplete);
    }

    /// <summary>
    /// Completeness matches the witnessed <em>kind</em>, not merely "some witnessable kind". A captured
    /// <see cref="RecoveryEdgeKind.HelperCall"/> does not vouch for a witnessed
    /// <see cref="RecoveryEdgeKind.PInvokeToWrapperSymbol"/> reference — the two are distinct symbol edges,
    /// so the mismatch is an orphan; the same reference witnessed as its captured kind is satisfied.
    /// </summary>
    [Fact]
    public void Completeness_CapturedKindMustMatchWitnessedKind_MismatchIsOrphan()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var provider = builder.DeclareUnit(Method("provider"), RecoveryScope.LeafApi, type);
        var consumer = builder.DeclareUnit(Method("consumer"), RecoveryScope.LeafApi, type);
        builder.AddRequires(consumer, provider, RecoveryEdgeKind.HelperCall);
        var graph = builder.Build();

        var witness = new WitnessedReference
        {
            Consumer = consumer,
            Provider = provider,
            Kind = RecoveryEdgeKind.PInvokeToWrapperSymbol,
            Symbol = "SBW_M_T_provider",
        };

        var mismatched = RecoveryCompletenessGate.Check(graph, new[] { witness });
        Assert.False(mismatched.IsComplete);
        Assert.Single(mismatched.Orphans);

        // The mismatch, not the consumer→provider pair, is what strands it: witnessed as its captured kind
        // the same reference is complete.
        var matched = RecoveryCompletenessGate.Check(
            graph, new[] { witness with { Kind = RecoveryEdgeKind.HelperCall } });
        Assert.True(matched.IsComplete);
    }

    /// <summary>
    /// A default-constructed report — the value a caller gets by forgetting to run the gate — has an empty
    /// orphan list but was never checked, so it must NOT read as complete; only an explicit checked result
    /// (the <see cref="RecoveryCompletenessReport.Complete"/> constant or a real <c>Check</c>) does. A
    /// witnessable scope handed an unchecked report is refused by the completeness gate, not waved through.
    /// </summary>
    [Fact]
    public void DefaultReport_IsNotComplete_ButAnExplicitCheckedResultIs()
    {
        Assert.False(default(RecoveryCompletenessReport).IsComplete);
        Assert.True(RecoveryCompletenessReport.Complete.IsComplete);

        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var leaf = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        var graph = builder.Build();

        // A real check over an empty witness is checked-and-complete.
        Assert.True(RecoveryCompletenessGate.Check(graph, System.Array.Empty<WitnessedReference>()).IsComplete);

        // The unchecked default fails the completeness gate for a witnessable scope.
        var authorization = RecoveryAuthorizer.SafeToDrop(graph, default, leaf, Retained(module, type));
        Assert.False(authorization.IsAuthorized);
        Assert.Equal(RecoveryAuthorizationDenial.WitnessIncomplete, authorization.Denial);
    }

    // ── Scope gate: the reviewer-driven partition ──────────────────────────────────────────────

    /// <summary>
    /// The scope gate refuses <see cref="RecoveryScope.SharedHelperBundle"/> even against a fully
    /// complete, policy-safe graph. The bundle scope also classifies the NativeAOT registration helper,
    /// whose retained conformer depends on it semantically, so the scope carries a non-witnessable
    /// possible-incoming kind and stays fail-closed this wave.
    /// </summary>
    [Fact]
    public void Authorizer_RefusesSharedHelperBundleScope_EvenWhenCompleteAndPolicySafe()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var helper = builder.DeclareSharedHelper(Module(), "utf8", module);
        var graph = builder.Build();

        // Nothing depends on the helper and the witness is empty, so policy + completeness both pass;
        // only the scope gate stands between this and Safe.
        Assert.True(RecoveryPolicy.SafeToDrop(graph, helper, Retained(module)).IsSafe);

        var authorization = RecoveryAuthorizer.SafeToDrop(
            graph, RecoveryCompletenessReport.Complete, helper, Retained(module));
        Assert.False(authorization.IsAuthorized);
        Assert.Equal(RecoveryAuthorizationDenial.NotWitnessableScope, authorization.Denial);
    }

    [Theory]
    [InlineData(RecoveryScope.LeafApi, true)]
    [InlineData(RecoveryScope.AccessorGroup, true)]
    [InlineData(RecoveryScope.SharedHelperBundle, false)]
    [InlineData(RecoveryScope.ForwardProtocolView, false)]
    [InlineData(RecoveryScope.ManagedProtocolConformance, false)]
    [InlineData(RecoveryScope.ConformanceEdge, false)]
    [InlineData(RecoveryScope.TypeRepresentation, false)]
    [InlineData(RecoveryScope.TypeSurface, false)]
    [InlineData(RecoveryScope.Module, false)]
    public void ScopePartition_OnlyLeafAndAccessorAreWitnessableThisWave(RecoveryScope scope, bool witnessable)
    {
        Assert.Equal(witnessable, RecoveryEdgeKinds.IsCoarseWithdrawalWitnessable(scope));
    }

    // ── Escalate authorization: whole-closure, closed-only ─────────────────────────────────────

    /// <summary>
    /// A witnessable seed whose escalation closure pulls in a non-witnessable member is refused — the
    /// seed being authorizable is not enough; everything the withdrawal actually removes must be. Here a
    /// reverse conformance depends on a leaf witness, so withdrawing the leaf drags the conformance into
    /// the closure; the conformance is the only non-witnessable member, so the refusal is deterministic.
    /// This is the back-door a seed-only check would leave open.
    /// </summary>
    [Fact]
    public void Escalate_RefusesWhenClosurePullsInANonWitnessableMember()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var leaf = builder.DeclareUnit(Method("witness"), RecoveryScope.LeafApi, type);
        // A reverse conformance (non-witnessable) whose witness is the leaf — withdrawing the leaf drags
        // the conformance into the dependent closure.
        var conformance = builder.DeclareUnit(Type("T"), RecoveryScope.ManagedProtocolConformance, type);
        builder.AddRequires(conformance, leaf, RecoveryEdgeKind.ConformanceObligation);
        var graph = builder.Build();

        var result = RecoveryAuthorizer.Escalate(
            graph, RecoveryCompletenessReport.Complete, new[] { leaf });
        Assert.False(result.IsAuthorized);
        Assert.Equal(RecoveryAuthorizationDenial.NotWitnessableScope, result.Denial);
        Assert.Equal(conformance, result.UnwitnessableMember);
    }

    /// <summary>An escalation that cannot close (reaches the module) is not an authorizable set.</summary>
    [Fact]
    public void Escalate_RefusesWhenTheWalkReachesTheModule()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var graph = builder.Build();

        var result = RecoveryAuthorizer.Escalate(
            graph, RecoveryCompletenessReport.Complete, new[] { module });
        Assert.False(result.IsAuthorized);
        Assert.Equal(RecoveryAuthorizationDenial.EscalationNotClosed, result.Denial);
    }

    /// <summary>An incomplete graph refuses any escalation before the policy is consulted.</summary>
    [Fact]
    public void Escalate_RefusesAnIncompleteGraph()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var leaf = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        var graph = builder.Build();

        var incomplete = new RecoveryCompletenessReport
        {
            Orphans = new[]
            {
                new WitnessedReference { Consumer = leaf, Provider = leaf, Kind = RecoveryEdgeKind.HelperCall },
            }.ToImmutableArray(),
        };

        var result = RecoveryAuthorizer.Escalate(graph, incomplete, new[] { leaf });
        Assert.False(result.IsAuthorized);
        Assert.Equal(RecoveryAuthorizationDenial.WitnessIncomplete, result.Denial);
    }

    /// <summary>A clean leaf seed with a complete graph produces an authorized single-unit closure.</summary>
    [Fact]
    public void Escalate_AuthorizesAWitnessableClosedClosure()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var leaf = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        var graph = builder.Build();

        var result = RecoveryAuthorizer.Escalate(
            graph, RecoveryCompletenessReport.Complete, new[] { leaf });
        Assert.True(result.IsAuthorized);
        Assert.Contains(leaf, result.Withdrawn);
    }
}
