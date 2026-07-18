// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The soundness rule: a removal is safe iff it alters no retained ABI footprint and leaves no
/// retained capability with an unsatisfied obligation.
/// </summary>
public class RecoveryPolicyTests
{
    private static DeclId Type(string name, string declPath = "") =>
        DeclId.Create("M", declPath, BindingItemKind.Type, name);

    private static DeclId Method(string name) =>
        DeclId.Create("M", "T", BindingItemKind.Method, name);

    private static DeclId Module() =>
        DeclId.Create("M", string.Empty, BindingItemKind.Module, "M");

    private static IReadOnlySet<RecoveryUnitId> Retained(params RecoveryUnitId[] ids) => ids.ToHashSet();

    /// <summary>A module, one type under it, and two leaves under the type.</summary>
    private static (RecoveryGraph Graph, RecoveryUnitId ModuleId, RecoveryUnitId TypeId,
        RecoveryUnitId LeafA, RecoveryUnitId LeafB) Fixture(bool leafAContributesLayout = false)
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var a = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type, leafAContributesLayout);
        var b = builder.DeclareUnit(Method("b"), RecoveryScope.LeafApi, type);
        return (builder.Build(), module, type, a, b);
    }

    // ── SafeToDrop ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SafeToDrop_AllowsAnIndependentLeaf()
    {
        var (graph, module, type, a, b) = Fixture();
        var verdict = RecoveryPolicy.SafeToDrop(graph, a, Retained(module, type, b));

        Assert.True(verdict.IsSafe);
        Assert.Equal(RecoveryObstruction.None, verdict.Obstruction);
    }

    /// <summary>Fail-closed: an unmodelled surface is never assumed droppable.</summary>
    [Fact]
    public void SafeToDrop_RefusesAnUnknownUnit()
    {
        var (graph, module, _, _, _) = Fixture();
        var ghost = RecoveryUnitId.Create(Type("Ghost"), RecoveryScope.TypeSurface);

        var verdict = RecoveryPolicy.SafeToDrop(graph, ghost, Retained(module));
        Assert.False(verdict.IsSafe);
        Assert.Equal(RecoveryObstruction.UnknownUnit, verdict.Obstruction);
    }

    [Fact]
    public void SafeToDrop_RefusesAContributorWhileItsParentIsRetained()
    {
        var (graph, module, type, a, b) = Fixture(leafAContributesLayout: true);

        var verdict = RecoveryPolicy.SafeToDrop(graph, a, Retained(module, type, b));
        Assert.False(verdict.IsSafe);
        Assert.Equal(RecoveryObstruction.ParentLayoutRetained, verdict.Obstruction);
        Assert.Equal(type, verdict.Blocker);
    }

    /// <summary>
    /// The same contributor is safe once its parent is going too: the layout it contributed to is no
    /// longer exposed, so nothing is left describing a shape that moved.
    /// </summary>
    [Fact]
    public void SafeToDrop_AllowsAContributorWhenItsParentIsAlsoWithdrawn()
    {
        var (graph, module, _, a, _) = Fixture(leafAContributesLayout: true);

        var verdict = RecoveryPolicy.SafeToDrop(graph, a, Retained(module));
        Assert.True(verdict.IsSafe);
    }

    [Fact]
    public void SafeToDrop_RefusesAUnitARetainedUnitRequires()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var helper = builder.DeclareSharedHelper(Module(), "utf8", module);
        var leaf = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        builder.AddRequires(leaf, helper);
        var graph = builder.Build();

        var verdict = RecoveryPolicy.SafeToDrop(graph, helper, Retained(module, type, leaf));
        Assert.False(verdict.IsSafe);
        Assert.Equal(RecoveryObstruction.RetainedDependent, verdict.Obstruction);
        Assert.Equal(leaf, verdict.Blocker);
    }

    [Fact]
    public void SafeToDrop_AllowsAUnitWhoseOnlyDependentIsAlsoWithdrawn()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var helper = builder.DeclareSharedHelper(Module(), "utf8", module);
        var leaf = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        builder.AddRequires(leaf, helper);
        var graph = builder.Build();

        Assert.True(RecoveryPolicy.SafeToDrop(graph, helper, Retained(module, type)).IsSafe);
    }

    // ── Escalate ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Escalate_LeavesAnAlreadySoundSeedAlone()
    {
        var (graph, _, _, a, _) = Fixture();
        var result = RecoveryPolicy.Escalate(graph, new[] { a });

        Assert.Equal(EscalationOutcome.Closed, result.Outcome);
        Assert.Equal(new[] { a }, result.Withdrawn.ToArray());
        Assert.Equal(0, result.Rounds);
    }

    [Fact]
    public void Escalate_OnNoSeedsIsANoOp()
    {
        var (graph, _, _, _, _) = Fixture();
        var result = RecoveryPolicy.Escalate(graph, System.Array.Empty<RecoveryUnitId>());

        Assert.Equal(EscalationOutcome.Closed, result.Outcome);
        Assert.Empty(result.Withdrawn);
    }

    /// <summary>
    /// A stored-field cell cannot leave while its struct stays, so recovery walks up and withdraws the
    /// type — the smallest sound answer, not the module.
    /// </summary>
    [Fact]
    public void Escalate_WalksAContributorUpToItsOwner()
    {
        var (graph, module, type, a, _) = Fixture(leafAContributesLayout: true);
        var result = RecoveryPolicy.Escalate(graph, new[] { a });

        Assert.Equal(EscalationOutcome.Closed, result.Outcome);
        Assert.Contains(a, result.Withdrawn);
        Assert.Contains(type, result.Withdrawn);
        Assert.DoesNotContain(module, result.Withdrawn);
        Assert.True(result.Rounds >= 1);
    }

    /// <summary>
    /// The obligation half is discharged structurally: the closure is dependent-closed, so a dependent
    /// of a withdrawn unit is pulled in rather than left promising a capability that has gone.
    /// </summary>
    [Fact]
    public void Escalate_PullsInDependentsOfTheSeed()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var helper = builder.DeclareSharedHelper(Module(), "utf8", module);
        var leaf = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        builder.AddRequires(leaf, helper);
        var graph = builder.Build();

        var result = RecoveryPolicy.Escalate(graph, new[] { helper });

        Assert.Equal(EscalationOutcome.Closed, result.Outcome);
        Assert.Contains(leaf, result.Withdrawn);
        Assert.DoesNotContain(type, result.Withdrawn);
    }

    /// <summary>
    /// A nested type escalates into its containing type rather than the module — the edge the
    /// rank-strict ordering could not express.
    /// </summary>
    [Fact]
    public void Escalate_WalksANestedTypeIntoItsContainingType()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var outer = builder.DeclareUnit(Type("Outer"), RecoveryScope.TypeSurface, module);
        var inner = builder.DeclareUnit(Type("Inner", "Outer"), RecoveryScope.TypeSurface, outer, contributesToParentLayout: true);
        var graph = builder.Build();

        var result = RecoveryPolicy.Escalate(graph, new[] { inner });

        Assert.Equal(EscalationOutcome.Closed, result.Outcome);
        Assert.Contains(outer, result.Withdrawn);
        Assert.DoesNotContain(module, result.Withdrawn);
    }

    /// <summary>
    /// When nothing coarser-but-still-sound exists the walk reaches the module and says so. That is a
    /// failure of the binding, not a degradation of it, and the caller has to be able to tell.
    /// </summary>
    [Fact]
    public void Escalate_ReportsReachingTheModule()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module, contributesToParentLayout: true);
        var graph = builder.Build();

        var result = RecoveryPolicy.Escalate(graph, new[] { type });

        Assert.Equal(EscalationOutcome.ReachedModule, result.Outcome);
        Assert.False(result.IsUsable);
        Assert.Contains(module, result.Withdrawn);
    }

    /// <summary>An unknown seed has nowhere to escalate, so the walk refuses to propose a set.</summary>
    [Fact]
    public void Escalate_BlocksOnAnUnknownSeed()
    {
        var (graph, _, _, _, _) = Fixture();
        var ghost = RecoveryUnitId.Create(Type("Ghost"), RecoveryScope.TypeSurface);

        var result = RecoveryPolicy.Escalate(graph, new[] { ghost });

        Assert.Equal(EscalationOutcome.Blocked, result.Outcome);
        Assert.False(result.IsUsable);
        Assert.Equal(ghost, result.BlockedAt);
    }

    /// <summary>
    /// The fixpoint must recompute what is retained after each expansion. A chain of contributors
    /// tests that: each round pulls in one more parent, and testing against a stale retained set would
    /// leave an offender whose parent has already joined the closure permanently offending.
    /// </summary>
    [Fact]
    public void Escalate_ConvergesThroughAChainOfContributors()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var outer = builder.DeclareUnit(Type("Outer"), RecoveryScope.TypeSurface, module);
        var mid = builder.DeclareUnit(Type("Mid", "Outer"), RecoveryScope.TypeSurface, outer, contributesToParentLayout: true);
        var inner = builder.DeclareUnit(Type("Inner", "Outer.Mid"), RecoveryScope.TypeSurface, mid, contributesToParentLayout: true);
        var graph = builder.Build();

        var result = RecoveryPolicy.Escalate(graph, new[] { inner });

        Assert.Equal(EscalationOutcome.Closed, result.Outcome);
        Assert.Contains(mid, result.Withdrawn);
        Assert.Contains(outer, result.Withdrawn);
        Assert.DoesNotContain(module, result.Withdrawn);
    }

    /// <summary>
    /// The termination argument rests on the obligation half being discharged structurally: because
    /// the closure is dependent-closed, no member of it can have a dependent that is still retained,
    /// so <see cref="RecoveryObstruction.RetainedDependent"/> cannot fire inside the escalation loop
    /// and layout contribution is the only obstruction left to drive it. If that ever stopped holding,
    /// the loop could cycle between two obstructions without growing, so it is pinned directly rather
    /// than inferred from a passing walk.
    /// </summary>
    [Fact]
    public void DependentClosure_LeavesNoMemberWithARetainedDependent()
    {
        var builder = new RecoveryGraphBuilder();
        var module = builder.DeclareModule(Module());
        var type = builder.DeclareUnit(Type("T"), RecoveryScope.TypeSurface, module);
        var helper = builder.DeclareSharedHelper(Module(), "utf8", module);
        var shared = builder.DeclareSharedHelper(Module(), "error-registry", module);
        var a = builder.DeclareUnit(Method("a"), RecoveryScope.LeafApi, type);
        var b = builder.DeclareUnit(Method("b"), RecoveryScope.LeafApi, type);
        var c = builder.DeclareUnit(Method("c"), RecoveryScope.LeafApi, type);

        // A web with a fan-out, a chain, and a shared dependency, so the closure is non-trivial.
        builder.AddRequires(a, helper);
        builder.AddRequires(b, helper);
        builder.AddRequires(c, shared);
        builder.AddRequires(shared, helper);
        var graph = builder.Build();

        var universe = graph.Units.Select(u => u.Id).ToHashSet();
        foreach (var seed in universe)
        {
            var closure = graph.DependentClosure(new[] { seed });
            var retained = universe.Where(id => !closure.Contains(id)).ToHashSet();

            foreach (var member in closure)
            {
                var verdict = RecoveryPolicy.SafeToDrop(graph, member, retained);
                Assert.NotEqual(RecoveryObstruction.RetainedDependent, verdict.Obstruction);
            }
        }
    }

    /// <summary>Whatever the walk settles on must actually satisfy the rule for every member.</summary>
    [Fact]
    public void Escalate_ProducesASetEveryMemberOfWhichIsSafe()
    {
        var (graph, _, _, a, _) = Fixture(leafAContributesLayout: true);
        var result = RecoveryPolicy.Escalate(graph, new[] { a });

        var retained = graph.Units.Select(u => u.Id).Where(id => !result.Withdrawn.Contains(id)).ToHashSet();
        foreach (var member in result.Withdrawn)
            Assert.True(RecoveryPolicy.SafeToDrop(graph, member, retained).IsSafe, member.Canonical);
    }
}
