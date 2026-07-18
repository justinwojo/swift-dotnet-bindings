// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The boundary bookkeeping under the interval maps, and specifically the rollback path — a
/// speculative member emission opens a scope, writes into the buffer, then truncates it back when
/// the member turns out to be unemittable, all while the scope's <c>using</c> block is still
/// pending. Whether that pending dispose closes nothing or closes an unrelated neighbour is not
/// visible in the rendered bytes, so nothing else in the suite can catch it.
/// </summary>
public class FragmentRecorderTests
{
    [Fact]
    public void BuildTiling_WithNoScopes_AttributesTheWholeBufferToTheRoot()
    {
        var recorder = new FragmentRecorder();

        var tiling = recorder.BuildTiling(20, Owner("root"));

        var leaf = Assert.Single(tiling);
        Assert.Equal(Owner("root"), leaf.Owner);
        Assert.Equal(0, leaf.Start);
        Assert.Equal(20, leaf.End);
    }

    [Fact]
    public void BuildTiling_IsTotalAndOrdered()
    {
        var recorder = new FragmentRecorder();
        var outer = recorder.Open(Owner("outer"), 5);
        var inner = recorder.Open(Owner("inner"), 8);
        recorder.Close(inner, 12);
        recorder.Close(outer, 15);

        var tiling = recorder.BuildTiling(20, Owner("root"));

        var expectedStart = 0;
        foreach (var leaf in tiling)
        {
            Assert.Equal(expectedStart, leaf.Start);
            expectedStart = leaf.End;
        }
        Assert.Equal(20, expectedStart);
        Assert.Contains(tiling, l => l.Owner == Owner("inner") && l.IsWholeScope);
    }

    [Fact]
    public void Close_WithAnUnknownToken_IsANoOp()
    {
        var recorder = new FragmentRecorder();
        var open = recorder.Open(Owner("a"), 0);
        recorder.Close(open, 10);

        Assert.False(recorder.Close(open, 12));
        Assert.False(recorder.Close(9999, 12));
    }

    /// <summary>
    /// The defect the token exists to prevent: after a rollback erases the inner scope's open, the
    /// inner scope's pending dispose must not close the outer scope that is still legitimately open.
    /// </summary>
    [Fact]
    public void Close_AfterARollbackErasedTheScope_DoesNotCloseAnUnrelatedNeighbour()
    {
        var recorder = new FragmentRecorder();
        var outer = recorder.Open(Owner("outer"), 0);
        var checkpoint = recorder.Checkpoint();
        var inner = recorder.Open(Owner("inner"), 10);

        recorder.RollbackTo(checkpoint);

        // The inner scope no longer exists; its stale dispose must find nothing.
        Assert.False(recorder.Close(inner, 10));
        Assert.Equal(1, recorder.OpenDepth);

        // And the outer scope must still be closable by its own holder, at its real end.
        Assert.True(recorder.Close(outer, 30));
        Assert.Equal(0, recorder.OpenDepth);

        var scope = Assert.Single(recorder.BuildScopes(30));
        Assert.Equal(Owner("outer"), scope.Owner);
        Assert.Equal(0, scope.Start);
        Assert.Equal(30, scope.End);
    }

    [Fact]
    public void RollbackTo_RestoresTheOpenScopeStackByValue()
    {
        var recorder = new FragmentRecorder();
        var outer = recorder.Open(Owner("outer"), 0);
        var inner = recorder.Open(Owner("inner"), 5);
        var checkpoint = recorder.Checkpoint();
        recorder.Close(inner, 8);
        recorder.Open(Owner("later"), 9);

        recorder.RollbackTo(checkpoint);

        Assert.Equal(2, recorder.OpenDepth);
        // A scope that was open at checkpoint time comes back closable by its original token.
        Assert.True(recorder.Close(inner, 12));
        Assert.True(recorder.Close(outer, 14));
    }

    /// <summary>
    /// Disposing out of order still has to leave a laminar event list, or the tiling walk's
    /// open/close stack desynchronizes and every later interval shifts.
    /// </summary>
    [Fact]
    public void Close_OnANonInnermostScope_ClosesTheScopesNestedInsideItToo()
    {
        var recorder = new FragmentRecorder();
        var outer = recorder.Open(Owner("outer"), 0);
        recorder.Open(Owner("inner"), 5);

        Assert.True(recorder.Close(outer, 20));

        Assert.Equal(0, recorder.OpenDepth);
        var scopes = recorder.BuildScopes(20);
        Assert.Equal(2, scopes.Count);
        Assert.All(scopes, s => Assert.Equal(20, s.End));

        var tiling = recorder.BuildTiling(20, Owner("root"));
        var expectedStart = 0;
        foreach (var leaf in tiling)
        {
            Assert.Equal(expectedStart, leaf.Start);
            expectedStart = leaf.End;
        }
        Assert.Equal(20, expectedStart);
    }

    [Fact]
    public void Retag_RewritesTheInnermostOpenScopeOwnerOnBothTheStackAndTheEvent()
    {
        var recorder = new FragmentRecorder();
        var outer = recorder.Open(Owner("outer"), 0);
        var inner = recorder.Open(Owner("before"), 5);

        Assert.True(recorder.Retag(Owner("after")));
        recorder.Close(inner, 10);
        recorder.Close(outer, 12);

        var scopes = recorder.BuildScopes(12);
        Assert.Contains(scopes, s => s.Owner == Owner("after"));
        Assert.DoesNotContain(scopes, s => s.Owner == Owner("before"));
    }

    [Fact]
    public void Retag_WithNothingOpen_ReportsFailure()
    {
        Assert.False(new FragmentRecorder().Retag(Owner("a")));
    }

    [Fact]
    public void RollbackToAndCapture_ThenReplay_RebasesTheBoundariesOntoTheNewOffset()
    {
        var recorder = new FragmentRecorder();
        var outer = recorder.Open(Owner("outer"), 0);
        var checkpoint = recorder.Checkpoint();
        var inner = recorder.Open(Owner("member"), 10);
        recorder.Close(inner, 20);

        var captured = recorder.RollbackToAndCapture(checkpoint, regionStart: 10);
        recorder.ReplayCaptured(captured, regionStart: 40);
        recorder.Close(outer, 60);

        var member = Assert.Single(recorder.BuildScopes(60), s => s.Owner == Owner("member"));
        Assert.Equal(40, member.Start);
        Assert.Equal(50, member.End);
    }

    /// <summary>
    /// A capture that straddles a scope boundary cannot be rebased without inventing nesting, so it
    /// is dropped — provenance degrades to the enclosing scope instead of going wrong.
    /// </summary>
    [Fact]
    public void ReplayCaptured_WithAnUnbalancedCapture_IsDropped()
    {
        var recorder = new FragmentRecorder();
        var outer = recorder.Open(Owner("outer"), 0);
        var checkpoint = recorder.Checkpoint();
        recorder.Open(Owner("straddler"), 10);   // opened, never closed inside the region

        var captured = recorder.RollbackToAndCapture(checkpoint, regionStart: 10);
        recorder.ReplayCaptured(captured, regionStart: 40);
        recorder.Close(outer, 60);

        Assert.DoesNotContain(recorder.BuildScopes(60), s => s.Owner == Owner("straddler"));
    }

    [Fact]
    public void AbsorbFrom_MergesABalancedSideRecorderAtTheGivenShift()
    {
        var side = new FragmentRecorder();
        var sideScope = side.Open(Owner("side-member"), 0);
        side.Close(sideScope, 12);

        var main = new FragmentRecorder();
        var outer = main.Open(Owner("outer"), 0);

        Assert.True(main.AbsorbFrom(side, shift: 30));
        main.Close(outer, 60);

        var merged = Assert.Single(main.BuildScopes(60), s => s.Owner == Owner("side-member"));
        Assert.Equal(30, merged.Start);
        Assert.Equal(42, merged.End);
    }

    [Fact]
    public void AbsorbFrom_WithAnUnbalancedSideRecorder_RefusesRatherThanSkewingTheEventList()
    {
        var side = new FragmentRecorder();
        side.Open(Owner("never-closed"), 0);

        var main = new FragmentRecorder();

        Assert.False(main.AbsorbFrom(side, shift: 30));
        Assert.True(main.IsEmpty);
    }

    [Fact]
    public void AbsorbFrom_WithAnEmptySideRecorder_SucceedsAndRecordsNothing()
    {
        var main = new FragmentRecorder();

        Assert.True(main.AbsorbFrom(new FragmentRecorder(), shift: 5));
        Assert.True(main.IsEmpty);
    }

    /// <summary>
    /// A boundary that moves backwards means the recorder and the writer buffer have diverged, and
    /// every interval built after it would be wrong. Failing loudly is the only safe response —
    /// silently accepting it produces a map that looks valid and is not.
    /// </summary>
    [Fact]
    public void Open_AtAnOffsetBeforeThePreviousBoundary_Throws()
    {
        var recorder = new FragmentRecorder();
        recorder.Open(Owner("a"), 20);

        Assert.Throws<InvalidOperationException>(() => recorder.Open(Owner("b"), 10));
    }

    [Fact]
    public void RecordedOwners_ListsEachOwnerOnceInFirstOpenOrder()
    {
        var recorder = new FragmentRecorder();
        var a = recorder.Open(Owner("a"), 0);
        recorder.Close(a, 5);
        var b = recorder.Open(Owner("b"), 5);
        recorder.Close(b, 10);
        var aAgain = recorder.Open(Owner("a"), 10);
        recorder.Close(aAgain, 15);

        Assert.Equal(new[] { Owner("a"), Owner("b") }, recorder.RecordedOwners().ToArray());
    }

    [Fact]
    public void BuildScopes_ClosesScopesLeftOpenAtTheBufferEnd()
    {
        var recorder = new FragmentRecorder();
        recorder.Open(Owner("unclosed"), 4);

        var scope = Assert.Single(recorder.BuildScopes(30));
        Assert.Equal(4, scope.Start);
        Assert.Equal(30, scope.End);
    }

    private static FragmentOwner Owner(string name) =>
        FragmentOwners.ForModule(DeclIdFactory.ForModule(name));
}
