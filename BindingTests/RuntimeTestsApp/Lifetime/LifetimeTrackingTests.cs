// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Tests for TrackedObject, TrackedContainer, and ValuePoint lifecycle patterns.
/// Exercises allocation tracking, reference chains, closure capture, and value semantics.
/// </summary>
public class LifetimeTrackingTests : TestBase
{
    public LifetimeTrackingTests(TestResults results) : base(results) { }

    // ---- 0.10.0 Layer C lifetime harness (populated by Bundles 1 and 3) ------
    //
    // Long-running / GC-pressure assertions for this class are gated by
    // `TestRunFlags.Lifetime` — set via `nuke binding-tests --lifetime`. Off by
    // default for inner-loop simulator runs; enabled unconditionally on the
    // integration serial gate. The 0.10.0 SafeHandle-refcount and
    // closure-lifetime bundles will populate methods here that loop a repro
    // pattern ~10k times with `GC.Collect()` between runs and assert
    // deterministic Swift alloc/dealloc counters return to baseline,
    // `CFGetRetainCount` returns to baseline for bridged ObjC objects, RSS
    // stays under a budget, and no finalizer-thread exceptions are logged.
    // See `src/docs/0.10.0-fix-plan.md` §"Layer C — lifetime harness".

    #region TrackedObject Construction

    public void TestTrackedObjectCreation()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(1);
        AssertEqual(1, obj.ObjectId, "ObjectId preserved after construction");
    }

    public void TestTrackedObjectWithLabel()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(2, "custom");
        AssertEqual(2, obj.ObjectId, "ObjectId preserved");
        AssertEqual("custom", obj.Label.ToString(), "Label preserved");
    }

    public void TestTrackedObjectIsAlive()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(3);
        AssertTrue(obj.IsAlive(), "Object reports alive");
    }

    public void TestTrackedObjectDescribe()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(4, "test");
        var desc = obj.GetDescribe();
        AssertTrue(desc.Contains("4"), "Describe contains object ID");
        AssertTrue(desc.Contains("test"), "Describe contains label");
    }

    #endregion

    #region TrackedContainer

    public void TestTrackedContainerEmpty()
    {
        using var container = TestLibFunctions.CreateTrackedContainer(10, null);
        AssertEqual(10, container.ContainerId, "ContainerId preserved");
        AssertFalse(container.HasChild(), "Empty container has no child");
    }

    public void TestTrackedContainerWithChild()
    {
        using var container = TestLibFunctions.CreateTrackedContainer(11, 42);
        AssertTrue(container.HasChild(), "Container has child after creation");
    }

    #endregion

    #region Reference Semantics

    public void TestIdentityPreservesObject()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(20);
        using var same = TestLibFunctions.Identity(obj);
        AssertEqual(20, same.ObjectId, "Identity returns same object ID");
    }

    public void TestCloneCreatesNewObject()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(30, "original");
        using var cloned = TestLibFunctions.Clone(obj);
        AssertEqual(30, cloned.ObjectId, "Clone preserves object ID");
        AssertTrue(cloned.Label.Contains("clone"), "Clone label indicates copy");
    }

    #endregion

    #region ValuePoint (Frozen Struct Value Semantics)

    public void TestValuePointCreation()
    {
        var pt = TestLibFunctions.CreateValuePoint(3, 4);
        AssertEqual(3, pt.X, "X preserved");
        AssertEqual(4, pt.Y, "Y preserved");
    }

    public void TestValuePointModifyCopy()
    {
        var original = TestLibFunctions.CreateValuePoint(10, 20);
        var modified = TestLibFunctions.ModifyValuePoint(original, 99);
        AssertEqual(99, modified.X, "Modified copy has new X");
        AssertEqual(20, modified.Y, "Modified copy preserves Y");
        AssertEqual(10, original.X, "Original X unchanged (value semantics)");
    }

    #endregion

    #region Closure Capture

    public void TestCapturingClosureRetainsObject()
    {
        // createCapturingClosure captures a TrackedObject(objectId) and returns its objectId
        var closure = TestLibFunctions.CreateCapturingClosure(objectId: 99);
        AssertNotNull(closure, "CreateCapturingClosure returned non-null");
        var result = closure();
        AssertEqual(99, result, "Captured closure returns objectId 99");
        // Call again to verify closure is stable
        var result2 = closure();
        AssertEqual(99, result2, "Second invocation still returns 99");
        TestLogger.Info($"CreateCapturingClosure(99)() = {result}");
    }

    public void TestMutatingClosureCaptureLifetime()
    {
        // createMutatingClosure captures a TrackedObject + callCount, returns incremented callCount
        var closure = TestLibFunctions.CreateMutatingClosure(objectId: 42);
        AssertNotNull(closure, "CreateMutatingClosure returned non-null");
        var call1 = closure();
        AssertEqual(1, call1, "First call returns count 1");
        var call2 = closure();
        AssertEqual(2, call2, "Second call returns count 2");
        var call3 = closure();
        AssertEqual(3, call3, "Third call returns count 3");
        TestLogger.Info($"CreateMutatingClosure: call counts = {call1}, {call2}, {call3}");
    }

    /// <summary>
    /// Baseline assertion of the closure GCHandle / wrapper-lifetime fix.
    /// Requires the Swift-side ARC destroy mechanism (closure-context owner
    /// token; informally "Category 3") — the cdecl adapter boxes the C#
    /// `(funcPtr, GCHandle)` pair in a Swift class whose deinit upcalls a
    /// registered C# destroy callback. Without that, the GCHandle leaks →
    /// the C# delegate is permanently rooted → any SafeHandle the delegate
    /// captured leaks → tracker.live grows.
    ///
    /// Deferred to 0.11: a prior 0.10.0 attempt boxed every thunk-bridged
    /// closure (incl. non-escaping), shipped a non-Sendable Swift class that
    /// failed Swift 6 strict concurrency in Apple frameworks, used
    /// `-undefined dynamic_lookup` to paper over linkage, and lost lifetime
    /// to weak property capture. Codex review (session
    /// 019dffa9-27b7-7bb1-86f8-1ff2f8288db9) recommended a narrower
    /// trampoline-Free for "shapes proven single-shot"; round 2 proved no
    /// such gate exists in the current generator (counterexample:
    /// AsyncCallbackClosures.processMultiple is MCB-eligible and fires
    /// multiple times). See bug-0.10.0-callback-trampoline-gchandle-leak.md.
    /// </summary>
    [Skip("0.10.0: closure-context owner token (Cat 3) deferred to 0.11; see bug-0.10.0-callback-trampoline-gchandle-leak.md")]
    public void TestEphemeralClosureReleasesCapturedSafeHandle()
    {
        LifetimeTracker.Reset();

        // Capture a fresh TrackedObject inside a delegate, pass the delegate
        // into a Swift closure-accepting API, drop the local reference, then
        // force finalizers. Post-fix the captured TrackedObject must
        // deallocate because the GCHandle around the delegate is freed by
        // the Swift adapter's deinit.
        {
            var captured = TestLibFunctions.CreateTrackedObject(101);
            var (_, _, liveBefore) = LifetimeTracker.GetStats();
            AssertTrue(liveBefore >= 1, "TrackedObject created (live >= 1)");

            var result = TestLibFunctions.CallWithInt32(x =>
            {
                _ = captured.IsAlive();
                return x + captured.ObjectId;
            });
            AssertEqual(42 + 101, result, "Closure invocation includes captured ObjectId");

            // Drop the local reference. Closure delegate is now the only
            // C# rooting path to `captured` — and only if the GCHandle is
            // still alive.
            captured = null!;
        }

        ForceGC();
        GC.WaitForPendingFinalizers();
        ForceGC();
        Thread.Sleep(50);
        ForceGC();
        GC.WaitForPendingFinalizers();
        ForceGC();

        var (alloc, dealloc, live) = LifetimeTracker.GetStats();
        TestLogger.Info(
            $"Ephemeral closure capture lifetime: alloc={alloc} dealloc={dealloc} live={live}");

        // Pre-fix this would be 1 (captured TrackedObject leaked via the
        // permanently-rooted delegate). Post-fix it must be 0.
        AssertEqual(0, live, "Captured TrackedObject deallocated after ephemeral closure call");
    }

    #endregion

    #region Protocol Conformance

    public void TestOwnableProtocolConformance()
    {
        // getOwnerId accepts `some Ownable` — uses CallConvSwift generic dispatch.
        // CallConvSwift is safe here: all P/Invoke params are IntPtr (blittable),
        // and single-type-param generics work on both Mono and NativeAOT.
        using var obj = TestLibFunctions.CreateTrackedObject(42);
#pragma warning disable SB0001
        var ownerId = TestLibFunctions.GetOwnerId(obj);
#pragma warning restore SB0001
        AssertEqual(42, ownerId, "GetOwnerId returns objectId");
        TestLogger.Info($"GetOwnerId(TrackedObject(42)) = {ownerId}");
    }

    #endregion

    #region Async Ownership

    public void TestAsyncCreateObject()
    {
        var task = TestLibFunctions.CreateObjectAsync(objectId: 77);
        // Async returns a Task — verify it starts without crash.
        // Full async/await in test harness requires MainRunLoop pumping; verify non-null return.
        AssertNotNull(task, "CreateObjectAsync returns non-null Task");
        TestLogger.Info("CreateObjectAsync invoked successfully");
    }

    public void TestAsyncRoundTrip()
    {
        using var obj = TestLibFunctions.CreateTrackedObject(88);
        var task = TestLibFunctions.RoundTripAsync(obj);
        AssertNotNull(task, "RoundTripAsync returns non-null Task");
        TestLogger.Info("RoundTripAsync invoked successfully");
    }

    #endregion
}
