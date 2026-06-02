// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// Regression coverage for issue #40 / P1-01 in the async-self direction: an async instance
/// method on an <c>@objc … : NSObject</c>-rooted Swift class. The generated wrapper keeps
/// <c>self</c> alive across the Task continuation by retaining the self pointer into the call
/// holder and releasing it in the completion callback. For an NSObject-rooted self that
/// retain/release MUST be the isa-dispatching <c>swift_unknownObjectRetain</c> /
/// <c>swift_unknownObjectRelease</c> (objc_retain / objc_release) — native <c>swift_retain</c>
/// touches the wrong refcount word, so the self can be deallocated under the in-flight
/// continuation or its count skewed.
///
/// <para>
/// <see cref="ObjCAsyncSelf"/> feeds the shared LifetimeTracker counters in init/deinit, so the
/// no-leak test asserts ARC <b>balance</b> of <c>self</c> across the await boundary — not merely
/// the absence of a crash. Exercised on Mono JIT (sim) and NativeAOT (device) because async
/// holder cleanup and self-pointer marshalling differ between the two runtimes.
/// </para>
/// </summary>
public class ObjCAsyncSelfTests : TestBase
{
    public ObjCAsyncSelfTests(TestResults results) : base(results) { }

    /// <summary>
    /// Drain for <c>@objc:NSObject</c> peers whose native <c>dealloc</c> is deferred to the
    /// main-thread finalization queue (Microsoft.iOS) — a plain GC drain runs the C# finalizer
    /// but the native dealloc (and <c>recordTrackedDeallocation</c>) only fires on a runloop
    /// iteration. Mirrors <c>ClassParamCallbackTests.DrainObjCFinalizers</c>.
    /// </summary>
    private static void DrainObjCFinalizers()
    {
        for (int i = 0; i < 6; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Foundation.NSRunLoop.Current.RunUntil((Foundation.NSDate)Foundation.NSDate.Now.AddSeconds(0.05));
        }
    }

    /// <summary>Async instance method WITH parameters on an @objc:NSObject self (with-params self-retain branch).</summary>
    public async Task TestObjCAsyncSelf_ComputeAsync_WithParams()
    {
        var self = new ObjCAsyncSelf(6);
        var result = await WithTimeout(self.ComputeAsync(7), DefaultAsyncTimeout);
        AssertEqual(42, result, "ComputeAsync(7) on @objc self with base 6 returns 42");
        // Dispose deterministically (not GC.KeepAlive): leaving these peers to finalization lets a
        // deferred dealloc land inside the sibling no-leak test's measurement window and skew its
        // global LifetimeTracker counters. Dispose also keeps self rooted across the await above.
        self.Dispose();
    }

    /// <summary>Async instance method WITHOUT parameters on an @objc:NSObject self (no-params self-retain branch).</summary>
    public async Task TestObjCAsyncSelf_PingAsync_NoParams()
    {
        var self = new ObjCAsyncSelf(13);
        var result = await WithTimeout(self.PingAsync(), DefaultAsyncTimeout);
        AssertEqual(13, result, "PingAsync() on @objc self returns base");
        self.Dispose(); // deterministic teardown — see ComputeAsync_WithParams for rationale
    }

    /// <summary>
    /// ARC balance of <c>self</c> across the await boundary. Each iteration constructs a tracked
    /// @objc:NSObject self, awaits both async instance methods (each retains/releases self into the
    /// call holder), then deterministically disposes it. With the UnknownObjectRetain /
    /// UnknownObjectRelease fix the per-call self-retain balances, so <c>Dispose</c> drops the last
    /// reference and every self deallocs; a native swift_retain/swift_release pair on the NSObject
    /// self would leave an unmatched native +1 that <c>Dispose</c> cannot drop (live &gt; 0) or
    /// over-release and crash. The assertion (all instances dealloc) is unchanged.
    ///
    /// <para>
    /// Teardown is by explicit <c>Dispose</c> rather than finalization on purpose. Under Mono's
    /// conservative GC the final iteration's <c>self</c>, hoisted into the lingering async
    /// state-machine box, is reliably false-rooted by a stale slot/register copy and never collects
    /// — a spurious "1 object not deallocated" straggler even though ARC is balanced (verified: the
    /// straggler survives 15 GC + runloop cycles, then disappears the moment <c>Dispose</c> force-
    /// releases the peer). The synchronous sibling <c>ClassPayloadEnumTests.ExtractObjCEnumPayloads</c>
    /// can rely on finalization because its stack frame is reused and cleared; an async frame's
    /// heap-allocated state machine cannot. Deterministic disposal sidesteps the false root entirely
    /// without weakening detection of a real ARC imbalance.
    /// </para>
    /// </summary>
    public async Task TestObjCAsyncSelf_SelfRetainBalancesArc_NoLeak()
    {
        DrainObjCFinalizers();
        LifetimeTracker.Reset();

        await RunSelfIterationsAsync(40);

        DrainObjCFinalizers();
        LifetimeTracker.AssertNoLeaks(
            "async @objc:NSObject self-retain/release must balance ARC across the await boundary (UnknownObjectRetain/Release)");
        TestLogger.Info("async @objc self: 40 instances ran 2 async methods each and all deallocated");
    }

    /// <summary>
    /// Runs the iteration loop in its own async frame (kept out-of-line via
    /// <see cref="MethodImplOptions.NoInlining"/> so the JIT can't fold it back into the asserting
    /// caller's still-live frame) and disposes each <c>self</c> at the end of its iteration. The
    /// per-call self-retain is already balanced (<c>UnknownObjectRetain</c>/<c>UnknownObjectRelease</c>);
    /// the explicit <c>Dispose</c> is what guarantees deterministic teardown — see
    /// <see cref="TestObjCAsyncSelf_SelfRetainBalancesArc_NoLeak"/> for why finalization is unreliable
    /// for the last hoisted instance.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task RunSelfIterationsAsync(int n)
    {
        for (int i = 1; i <= n; i++)
        {
            var self = new ObjCAsyncSelf(i);
            var computed = await WithTimeout(self.ComputeAsync(2), DefaultAsyncTimeout);
            AssertEqual(i * 2, computed, $"iter {i}: ComputeAsync(2) == base*2");
            var pinged = await WithTimeout(self.PingAsync(), DefaultAsyncTimeout);
            AssertEqual(i, pinged, $"iter {i}: PingAsync() == base");
            self.Dispose();
        }
    }
}
