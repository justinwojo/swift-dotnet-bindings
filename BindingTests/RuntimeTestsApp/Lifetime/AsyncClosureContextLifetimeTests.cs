// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Runtime tests for the ASYNC-closure GCHandle lifetime fix
/// (<c>AsyncClosureHelper.cs</c>). The async-closure bridge hands Swift a
/// <c>GCHandle</c> rooting the managed delegate's captured graph and deliberately
/// does NOT free it per-invocation (Swift may invoke the same context more than
/// once). Ownership instead rides on the Swift-side <c>_SBClosureCtx</c> owner-token
/// box wired into the async wrapper's <c>_SBW_AsyncClosureHandoff.ctxOwner</c>: when
/// Swift ARC releases the adapter closure (after the one-shot <c>await closure()</c>
/// and the outer async method returns), the box's deinit upcalls the C# free
/// trampoline and frees the handle exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Pre-fix (<c>AsyncClosureHelper</c> "intentionally leaked" the handle), every
/// async-closure call leaked the delegate and its captured graph for the process
/// lifetime, so a <see cref="WeakReference"/> to the captured target stayed alive
/// indefinitely and N calls accumulated N live targets.
/// </para>
/// <para>
/// This is the async sibling of <see cref="EscapingClosureLifetimeTests"/> (which
/// covers the sync escaping-closure <c>SwiftClosureData</c> path). Both rely on the
/// same <c>_SBClosureCtx</c> deinit → free-trampoline channel, hence the same
/// device-only skip: <c>RuntimeTestsApp</c> sets
/// <c>IncludeSwiftBindingsRuntimeNative=false</c>, so on the Mono simulator the
/// wrapper falls back to the no-deinit <c>_SBClosureCtxFallback</c> that intentionally
/// preserves the prior leak; the NativeAOT device build loads
/// SwiftBindingsRuntime.xcframework as a NativeReference, so the real deinit fires and
/// these assertions hold there.
/// </para>
/// </remarks>
public class AsyncClosureContextLifetimeTests : TestBase
{
    public AsyncClosureContextLifetimeTests(TestResults results) : base(results) { }

    private const int GcCycles = 6;
    private const int BulkIterations = 25;
    private const int MaxResidualAlive = 5;

    private static void ForceGc()
    {
        var worker = new System.Threading.Thread(ForceGcWorker) { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ForceGcWorker()
    {
        var scratch = new object[256];
        for (int i = 0; i < scratch.Length; i++)
            scratch[i] = new object();
        GC.KeepAlive(scratch);

        for (int i = 0; i < GcCycles; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// Sanity check: a one-shot async closure still produces the correct result
    /// across the boxed-context path. If the <c>_SBClosureCtx</c> wiring corrupted
    /// the context pointer, the await would crash or return the wrong value.
    /// </summary>
    public async Task TestAsyncClosureFiresCorrectly()
    {
        Func<Task<int>> userLambda = () => Task.FromResult(42);
        var result = await WithTimeout(
            Functions.CallAsyncThrowingClosureAsync(userLambda),
            DefaultAsyncTimeout);
        AssertEqual(42, result, "Async closure dispatches correctly through the _SBClosureCtx box path");
    }

    /// <summary>
    /// Core regression: after a one-shot async-closure call completes and the
    /// outer Swift method returns, Swift releases the adapter closure — its
    /// <c>_SBClosureCtx</c> box deinits and frees the captured <c>GCHandle</c>.
    /// The delegate's captured target must then become collectible. Pre-fix the
    /// handle was never freed, so the weak reference stayed alive forever.
    /// </summary>
    [SkipOnSimulator("Async-closure owner-token box lives in libSwiftBindingsRuntime.dylib. " +
        "RuntimeTestsApp sets IncludeSwiftBindingsRuntimeNative=false, so on the Mono simulator the " +
        "async wrapper falls back to _SBClosureCtxFallback — a no-deinit class that intentionally " +
        "preserves the prior leak (see ClosureContextHelperEmitter.cs and SwiftClosureContext.cs " +
        "catch DllNotFoundException). The NativeAOT device build loads SwiftBindingsRuntime.xcframework " +
        "as a NativeReference, so the real _SBClosureCtx deinit fires and this assertion holds. Same " +
        "root cause / skip rationale as EscapingClosureLifetimeTests.")]
    public async Task TestOneShotAsyncClosureReleasesDelegateTarget()
    {
        var weakTarget = await InvokeAsyncClosureAndReturnWeakRef();

        ForceGc();

        AssertTrue(
            !weakTarget.IsAlive,
            "After a one-shot async-closure call completes, the captured delegate target must become " +
            "collectible. If the _SBClosureCtx box deinit didn't fire, the per-call GCHandle would still " +
            "root the target (the pre-fix intentional leak).");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<WeakReference> InvokeAsyncClosureAndReturnWeakRef()
    {
        var target = new CapturedTarget();
        // The lambda captures `target`; while the async closure's GCHandle is live,
        // the lambda (and `target`) stay rooted. Freeing the handle makes both collectible.
        Func<Task<int>> userLambda = () => { target.Receive(7); return Task.FromResult(7); };
        var weak = new WeakReference(target);

        var result = await WithTimeout(
            Functions.CallAsyncThrowingClosureAsync(userLambda),
            DefaultAsyncTimeout);
        AssertEqual(7, result, "Async closure round-trip returned the expected value");

        // Drop local roots; only `weak` survives via the return. After this Task
        // completes, the only remaining root would be Swift's per-call GCHandle.
        userLambda = null!;
        target = null!;
        return weak;
    }

    /// <summary>
    /// Bulk regression — the most faithful probe for a PER-CALL leak: N independent
    /// one-shot async-closure calls must not accumulate live delegate targets. Pre-fix
    /// the live count grew linearly with the number of calls; post-fix it collapses to a
    /// small constant (conservative-stack-scan noise floor).
    /// </summary>
    [SkipOnSimulator("Async-closure owner-token box lives in libSwiftBindingsRuntime.dylib; " +
        "RuntimeTestsApp omits the dylib via IncludeSwiftBindingsRuntimeNative=false, so the async " +
        "wrapper's no-deinit _SBClosureCtxFallback preserves the prior per-call leak on the simulator " +
        "by design. The NativeAOT device build loads the framework via NativeReference and this " +
        "assertion holds. Same root cause as TestOneShotAsyncClosureReleasesDelegateTarget.")]
    public async Task TestBulkAsyncInvokeDoesNotAccumulateTargets()
    {
        var weaks = new List<WeakReference>(BulkIterations);
        for (int i = 0; i < BulkIterations; i++)
            weaks.Add(await AsyncInvokeRound(i));

        ForceGc();

        int alive = 0;
        foreach (var w in weaks)
            if (w.IsAlive) alive++;

        TestLogger.Info($"[AsyncClosureContextLifetime] {BulkIterations}x async invoke: alive={alive}");
        AssertTrue(
            alive <= MaxResidualAlive,
            $"{alive} of {BulkIterations} async-closure targets stayed alive after GC " +
            $"(tolerance {MaxResidualAlive}). The per-call GCHandle is leaking — _SBClosureCtx deinit not firing.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<WeakReference> AsyncInvokeRound(int seed)
    {
        var target = new CapturedTarget();
        Func<Task<int>> userLambda = () => { target.Receive(seed); return Task.FromResult(seed); };
        var weak = new WeakReference(target);

        await WithTimeout(
            Functions.CallAsyncThrowingClosureAsync(userLambda),
            DefaultAsyncTimeout);

        userLambda = null!;
        target = null!;
        return weak;
    }
}
