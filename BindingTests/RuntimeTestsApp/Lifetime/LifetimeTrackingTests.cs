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
    // Layer C — lifetime harness: exercises tracked-object alloc/dealloc counters.

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
    /// Exercises the Swift-side ARC destroy mechanism (closure-context owner
    /// token; informally "Category 3") — the cdecl adapter boxes the C#
    /// `(funcPtr, GCHandle)` pair in a Swift `_SBClosureCtx` whose deinit
    /// upcalls the C# destroy callback registered by
    /// `SwiftClosureContext.EnsureRegistered`. The GCHandle is freed exactly
    /// once when Swift releases the closure.
    /// </summary>
    [SkipOnSimulator("Requires the SwiftBindingsRuntime native framework; the simulator build sets IncludeSwiftBindingsRuntimeNative=false (InstallNameTool workaround), so the destroy hook degrades to the documented leak fallback. Validated on device (NativeAOT).")]
    public void TestEphemeralClosureReleasesCapturedSafeHandle()
    {
        LifetimeTracker.Reset();

        // The closure-and-invoke is intentionally inside a helper that takes the
        // TrackedObject as a parameter. C# lambdas capture variable slots, so if
        // we created `captured` here and then set it to null, the display class
        // field would null out and the TrackedObject would be collectable even
        // pre-fix — invalidating the regression. Routing through a helper means
        // the only path to the TrackedObject is via the delegate's display class,
        // which is itself only rooted by the GCHandle.
        InvokeEphemeralEscapingClosure(101, expectedSum: 42 + 101);

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

    private void InvokeEphemeralEscapingClosure(int objectId, int expectedSum)
    {
        var captured = TestLibFunctions.CreateTrackedObject(objectId);
        var (_, _, liveBefore) = LifetimeTracker.GetStats();
        AssertTrue(liveBefore >= 1, "TrackedObject created (live >= 1)");

        var result = TestLibFunctions.CallWithInt32(x =>
        {
            _ = captured.IsAlive();
            return x + captured.ObjectId;
        });
        AssertEqual(expectedSum, result, "Closure invocation includes captured ObjectId");
        // Do not null `captured`: see note in the caller. When this method
        // returns the local slot goes away on its own; the delegate's display
        // class still holds the TrackedObject and is rooted only by the
        // GCHandle (post-fix: freed) or by Swift retaining the closure
        // context (escaping case).
    }

    /// <summary>
    /// Regression test for the documented non-escaping leak previously at
    /// `MethodClosureBridge.cs:1220-1224`. The MCB emit path allocated a
    /// <c>GCHandle</c> rooting the C# delegate, but its try/finally was
    /// gated on <c>anyEscaping</c> — non-escaping closures fell through
    /// without freeing the handle, leaking the delegate (and anything it
    /// captured) for the process lifetime.
    /// </summary>
    /// <remarks>
    /// Runs on simulator: the fix lives entirely in the C# wrapper's
    /// <c>finally</c> block, which calls <c>ClosureHandle.Dispose()</c>
    /// for every closure regardless of policy. The non-escaping policy
    /// always frees; no Swift-side <c>_SBClosureCtx</c> deinit upcall is
    /// required, so the test does not need the runtime dylib that the
    /// escaping-closure ephemeral test depends on.
    /// </remarks>
    public void TestNonEscapingMcbClosureDoesNotLeakCapturedObject()
    {
        LifetimeTracker.Reset();

        // Closure-and-invoke lives inside a helper that takes the TrackedObject
        // as a parameter. C# lambdas capture the variable slot, not the object;
        // creating `captured` inline and then nulling it would null the display
        // class's field and let the TrackedObject collect even pre-fix. Routing
        // through a helper makes the delegate's display class the only path to
        // the TrackedObject, which is itself only rooted by the GCHandle.
        InvokeNonEscapingMcbClosure(303);

        ForceGC();
        GC.WaitForPendingFinalizers();
        ForceGC();
        Thread.Sleep(50);
        ForceGC();
        GC.WaitForPendingFinalizers();
        ForceGC();

        var (alloc, dealloc, live) = LifetimeTracker.GetStats();
        TestLogger.Info(
            $"Non-escaping MCB closure capture lifetime: alloc={alloc} dealloc={dealloc} live={live}");

        // Pre-fix: 1 (delegate leaked via the never-freed MCB GCHandle).
        // Post-fix: 0 (ClosureHandle.Dispose frees the handle in finally).
        AssertEqual(0, live, "Captured TrackedObject deallocated after non-escaping MCB call");
    }

    private void InvokeNonEscapingMcbClosure(int objectId)
    {
        using var fixture = new NonEscapingMCBFixture();
        var captured = TestLibFunctions.CreateTrackedObject(objectId);
        var (_, _, liveBefore) = LifetimeTracker.GetStats();
        AssertTrue(liveBefore >= 1, "TrackedObject created (live >= 1)");

        var result = fixture.RunSynchronously(pr =>
        {
            _ = pr; // discard ProcessResult arg
            _ = captured.IsAlive();
            return captured.ObjectId == objectId;
        });
        AssertTrue(result, $"Closure observed captured ObjectId (id=={objectId})");
        // When this method returns the `captured` and `fixture` slots die with
        // the frame. The delegate's display class is then only rooted by the
        // MCB GCHandle: pre-fix it was leaked forever; post-fix it was disposed
        // in the wrapper's finally before this method returned.
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
