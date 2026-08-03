// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Properties;

/// <summary>
/// Runtime proof for what a C# consumer actually gets when it writes through a Swift-struct-typed
/// property. Swift structs have value semantics, so the generated getter returns a fresh wrapper
/// over a fresh copy of the native buffer. Because non-frozen structs project as C# <i>classes</i>,
/// <c>owner.Prop.Field = x</c> compiles and runs and silently changes nothing on the owner — the
/// shape SB1003 warns about.
///
/// Three things are pinned here, none of which any existing test could show (every other
/// struct-typed property in the harness is get-only):
/// <list type="number">
/// <item>the direct write is lost, and it is lost because it lands on a copy — not because the
/// setter is broken;</item>
/// <item>the copy-modify-write-back idiom reaches the owner, so the write-back primitive already
/// exists and no generator change is needed to make correct code expressible;</item>
/// <item>the discarded copies are reclaimed rather than leaked without bound — the payload is a
/// <c>SafeHandle</c>, whose critical finalizer frees the native buffer, so the cost of the
/// no-op idiom is deferred (and non-deterministic) reclamation, not unbounded growth.</item>
/// </list>
/// </summary>
public class StructPropertyWriteBackTests : TestBase
{
    public StructPropertyWriteBackTests(TestResults results) : base(results) { }

    #region The direct write is lost

    public void TestDirectMemberWriteThroughStructPropertyIsLost()
    {
        var owner = new DataContainer(1, "original");

        // The idiomatic C# spelling a consumer reaches for first. It mutates the copy the getter
        // just produced, then discards it. SB1003 flags exactly this statement.
#pragma warning disable SB1003
        owner.MutableData.Value = 99;
#pragma warning restore SB1003

        using var reread = owner.MutableData;
        AssertEqual(1, reread.Value, "owner.MutableData.Value after a direct member write");
        TestLogger.Info($"Direct write through MutableData discarded: owner still reads {reread.Value}");
    }

    public void TestDirectWriteLandsOnTheCopyNotTheOwner()
    {
        // Decomposes the no-op: the same write, with the intermediate held, shows the setter works
        // and the copy really does take the value. Only the link back to the owner is missing.
        var owner = new DataContainer(1, "original");

        using var copy = owner.MutableData;
        copy.Value = 99;
        copy.Name = "mutated";
        AssertEqual(99, copy.Value, "the intermediate copy took the write");
        AssertEqual("mutated", copy.Name, "the intermediate copy took the string write");

        using var reread = owner.MutableData;
        AssertEqual(1, reread.Value, "the owner never saw the copy's write");
        AssertEqual("original", reread.Name, "the owner never saw the copy's string write");
    }

    #endregion

    #region The copy-modify-write-back idiom reaches the owner

    public void TestCopyModifyWriteBackReachesTheOwner()
    {
        var owner = new DataContainer(1, "original");

        using (var copy = owner.MutableData)
        {
            copy.Value = 99;
            copy.Name = "written back";
            owner.MutableData = copy;
        }

        using var reread = owner.MutableData;
        AssertEqual(99, reread.Value, "owner.MutableData.Value after write-back");
        AssertEqual("written back", reread.Name, "owner.MutableData.Name after write-back");
        TestLogger.Info("Copy-modify-write-back reached the owner through the generated setter");
    }

    public void TestWholeValueAssignmentReachesTheOwner()
    {
        // The other correct spelling: build a fresh value and assign the whole property.
        var owner = new DataContainer(1, "original");

        using (var replacement = new InnerData(42, "replaced"))
        {
            owner.MutableData = replacement;
        }

        using var reread = owner.MutableData;
        AssertEqual(42, reread.Value, "owner.MutableData.Value after whole-value assignment");
        AssertEqual("replaced", reread.Name, "owner.MutableData.Name after whole-value assignment");
    }

    public void TestRepeatedWriteBackAccumulates()
    {
        var owner = new DataContainer(0, "acc");

        for (int i = 1; i <= 10; i++)
        {
            using var copy = owner.MutableData;
            copy.Value = copy.Value + i;
            owner.MutableData = copy;
        }

        using var reread = owner.MutableData;
        AssertEqual(55, reread.Value, "ten write-back rounds accumulated on the owner");
    }

    #endregion

    #region Discarded copies are reclaimed, not leaked without bound

    public void TestDiscardedStructCopiesAreReclaimed()
    {
        // The claim under test is NOT "no memory is held" — it is "the hold is bounded by
        // finalization, not permanent". Each read allocates a native buffer owned by a
        // SwiftSafeHandle; nothing here disposes one. Weak references to those handles let the
        // probe observe reclamation directly instead of inferring it from process RSS.
        //
        // The threshold is a floor, not a target: finalization order and timing are not
        // contractual, so demanding "all zero" would be a flaky assertion about GC scheduling
        // rather than about the binding. A 10% survivor allowance is loose enough to absorb a few
        // handles still rooted by a stale stack slot, and tight enough that a real regression —
        // buffers never handed to the finalizer at all — fails instead of squeaking past on one
        // reclaimed copy. Observed on both runtimes: 1 survivor out of 200.
        const int Reads = 200;
        const int MaxSurvivors = Reads / 10;
        var owner = new DataContainer(7, "reclaim");
        var handles = new List<WeakReference>(Reads);

        var before = LifetimeTracker.GetStats();

        for (int i = 0; i < Reads; i++)
        {
            var copy = owner.MutableData;
            handles.Add(new WeakReference(copy.Payload));
            AssertEqual(7, copy.Value, $"copy {i} read the owner's value");
            // copy is deliberately NOT disposed — this is the consumer's no-op idiom.
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int stillAlive = handles.Count(w => w.IsAlive);
        var after = LifetimeTracker.GetStats();

        // Diagnostic, not an assertion: the copies are managed-side native buffers, so the Swift
        // allocation tracker (which counts registered Swift class instances) legitimately sees no
        // churn here. Logging it keeps that distinction on the record.
        TestLogger.Info(
            $"{Reads} discarded struct copies: {Reads - stillAlive} payload handles reclaimed, " +
            $"{stillAlive} still rooted; Swift tracked-object live count {before.live} -> {after.live}");

        AssertTrue(stillAlive <= MaxSurvivors,
            $"finalization reclaimed the discarded copies ({stillAlive}/{Reads} still alive, " +
            $"at most {MaxSurvivors} allowed)");

        // The owner outlives every copy and is still readable: reclaiming a copy destroys the
        // copy's buffer, never the value the owner holds.
        using var reread = owner.MutableData;
        AssertEqual(7, reread.Value, "owner still readable after its copies were reclaimed");
    }

    public void TestDisposedCopyReleasesItsPayloadImmediately()
    {
        // The deterministic half of the same story: disposing the intermediate closes its handle
        // right away rather than waiting for the finalizer. This is why the taught idiom uses
        // `using var`.
        var owner = new DataContainer(7, "dispose");

        SafeHandle payload;
        using (var copy = owner.MutableData)
        {
            payload = copy.Payload;
            AssertFalse(payload.IsClosed, "payload is open while the copy is in scope");
        }

        AssertTrue(payload.IsClosed, "payload closed as soon as the copy was disposed");
    }

    #endregion
}
