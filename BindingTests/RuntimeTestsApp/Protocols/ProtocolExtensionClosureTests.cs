// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end coverage for the ProtocolExtensionClosureBridge (PExtCB) emitter:
/// protocol extension methods taking an @escaping closure must close the
/// alloc-before-P/Invoke leak window and route the GCHandle through the
/// `_SBClosureCtx` owner-token so the deinit upcall frees the handle.
/// </summary>
public class ProtocolExtensionClosureTests : TestBase
{
    public ProtocolExtensionClosureTests(TestResults results) : base(results) { }

    public void TestEscapingVoidInvokedExactlyOnce()
    {
        using var seed = new PExtClosureSeed(seed: 0);
        var fired = 0;
        seed.RunEscapingVoid(() => fired++);
        AssertEqual(1, fired, "Extension escaping closure invoked exactly once");
    }

    public void TestEscapingVoidRepeatedCallsDoNotLeak()
    {
        // Re-invoking the bridge many times relies on `_SBClosureCtx` deinit
        // freeing each per-call GCHandle; if the throw-window fix or box wiring
        // regressed we'd accumulate dead handles and trip GC pressure.
        using var seed = new PExtClosureSeed(seed: 0);
        var fired = 0;
        for (var i = 0; i < 64; i++)
        {
            seed.RunEscapingVoid(() => fired++);
        }
        AssertEqual(64, fired, "Each repeated invocation runs the closure exactly once");
    }

    /// <summary>
    /// Owner-token contract: capture a tracked object inside the C# delegate,
    /// drop the local reference, force GC. The captured object must deallocate
    /// because the per-call GCHandle is freed by the `_SBClosureCtx` deinit
    /// upcall after the wrapper returns.
    /// </summary>
    [SkipOnSimulator("Requires the SwiftBindingsRuntime native framework; the simulator build sets IncludeSwiftBindingsRuntimeNative=false (InstallNameTool workaround), so the destroy hook degrades to the documented leak fallback. Validated on device (NativeAOT).")]
    public void TestEscapingVoidReleasesCapturedTrackedObject()
    {
        LifetimeTracker.Reset();

        using var seed = new PExtClosureSeed(seed: 0);

        // Capture in a separate frame that is popped before collecting: nulling a local is not
        // enough under a conservative stack scan, which reads the raw slot rather than the
        // variable. This is the same shape AutoWrappedDelegateTests uses for its weak-slot probe.
        CaptureTrackedObjectInEscapingClosure(seed);

        // Collect from a worker thread whose stack never held the object. NativeAOT scans
        // precisely, so plain GC.Collect() sufficed there; the device Mono full-AOT lane scans
        // the stack conservatively and a stale pointer left in this thread's frame poses as a
        // root, reporting a leak the bindings did not cause.
        ForceGCThorough();

        var (alloc, dealloc, live) = LifetimeTracker.GetStats();
        TestLogger.Info($"PExtCB closure capture lifetime: alloc={alloc} dealloc={dealloc} live={live}");
        AssertEqual(0, live, "Captured TrackedObject deallocated after PExtCB call");
    }

    /// <summary>
    /// Creates the tracked object, captures it in the escaping delegate, runs the bridge, and
    /// returns — so on return no live frame references the object. The closure delegate's
    /// GCHandle is then the only remaining root path to it, and only for as long as the
    /// <c>_SBClosureCtx</c> deinit upcall has not yet freed that handle.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void CaptureTrackedObjectInEscapingClosure(PExtClosureSeed seed)
    {
        var captured = TestLibFunctions.CreateTrackedObject(7777);
        var (_, _, liveBefore) = LifetimeTracker.GetStats();
        AssertTrue(liveBefore >= 1, "TrackedObject created (live >= 1)");

        seed.RunEscapingVoid(() => { _ = captured.IsAlive(); });
    }
}
