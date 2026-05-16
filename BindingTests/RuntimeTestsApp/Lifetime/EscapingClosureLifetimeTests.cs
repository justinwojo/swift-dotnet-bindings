// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Runtime tests for the legacy <c>SwiftClosureData</c> escaping-closure
/// owner-token plumbing (N-3 in sdk-0.11.0-residual-gaps.md). Confirms that a
/// managed delegate captured by an escaping closure handed to Swift becomes
/// collectible after Swift releases its strong reference — proving the
/// <c>_SBClosureCtx</c> box's deinit upcalls the C# free trampoline and frees
/// the captured <see cref="System.Runtime.InteropServices.GCHandle"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pre-fix: <c>setStreamingCallback</c> created a <c>GCHandle</c> pinning the
/// managed delegate. The wrapper's <c>finally</c> could not free it (Swift may
/// fire the callback later), and Swift's release of the closure had no
/// notification channel back to managed code — so the handle leaked for the
/// lifetime of the process. After N rounds, <c>WeakReference.IsAlive</c>
/// stayed <c>true</c> indefinitely.
/// </para>
/// <para>
/// Post-fix: the C# wrapper allocates an <c>_SBClosureCtx</c> box wrapping the
/// <c>GCHandle</c> pointer (via <c>SwiftClosureMarshaller.TryAllocateBoxedContext</c>)
/// and stores the box pointer in <c>SwiftClosureData.context</c>. Swift's
/// release of the closure releases the box, fires its deinit, and frees the
/// handle. After <c>clearStreamingCallback()</c> + GC, the weak reference
/// must transition to dead.
/// </para>
/// <para>
/// The closure-roundtrip assertion confirms <c>fire(value:)</c> still
/// dispatches correctly through <c>GetDelegateFromBoxedContext</c> — the new
/// extraction path that unboxes via the runtime helper.
/// </para>
/// </remarks>
public class EscapingClosureLifetimeTests : TestBase
{
    public EscapingClosureLifetimeTests(TestResults results) : base(results) { }

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
    /// Sanity check: the stored closure must dispatch correctly across the
    /// boxed-context unbox path. If <c>GetDelegateFromBoxedContext</c> were
    /// broken (e.g. dylib-state latch out of sync), <c>fire</c> would either
    /// crash or invoke the wrong target.
    /// </summary>
    public void TestStoredClosureFiresCorrectly()
    {
        using var harness = new StreamingCallbackHarness();
        int seen = 0;
        harness.SetStreamingCallback(value => seen = value);

        harness.Fire(value: 42);
        AssertEqual(42, seen, "Stored closure dispatches with correct argument");

        harness.Fire(value: 99);
        AssertEqual(99, seen, "Stored closure can fire multiple times without crashing");

        harness.ClearStreamingCallback();
    }

    /// <summary>
    /// Core regression: after Swift releases the stored closure (via
    /// <c>clearStreamingCallback</c>) and a GC + finalizer-queue spin, the
    /// underlying managed delegate target must become collectible. Pre-fix
    /// the <see cref="WeakReference"/> stayed alive for the lifetime of the
    /// process because the <c>GCHandle</c> leaked.
    /// </summary>
    [SkipOnSimulator("N-3 owner-token box lives in libSwiftBindingsRuntime.dylib. " +
        "RuntimeTestsApp sets IncludeSwiftBindingsRuntimeNative=false (to avoid the " +
        "InstallNameTool .dylib.tmp rename failure documented in AGENTS.md), so on " +
        "simulator the wrapper falls back to _SBClosureCtxFallback — a no-deinit class " +
        "that intentionally preserves the prior leak behaviour (see ClosureContextHelperEmitter.cs " +
        "lines 55-60 and SwiftClosureContext.cs catch DllNotFoundException). The device " +
        "build loads SwiftBindingsRuntime.xcframework as a NativeReference, so the " +
        "real _SBClosureCtx deinit fires and this assertion holds there.")]
    public void TestClearedClosureReleasesDelegateTarget()
    {
        var weakTarget = SetCallbackAndReturnWeakRef();

        // Caller's frame still holds the harness via the helper's return path?
        // No — the helper disposes the harness so Swift drops the closure, then
        // returns only the WeakReference. Worker GC scrubs the stack.
        ForceGc();

        AssertTrue(
            !weakTarget.IsAlive,
            "Cleared escaping closure's delegate target must become collectible after Swift releases the box. " +
            "If the box's deinit didn't fire, the captured GCHandle would still root the target.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference SetCallbackAndReturnWeakRef()
    {
        var harness = new StreamingCallbackHarness();
        var target = new CapturedTarget();
        // Form a delegate whose target is `target`. When the closure's
        // GCHandle is freed, the delegate becomes collectible, which in turn
        // makes `target` collectible.
        Action<int> callback = target.Receive;
        var weak = new WeakReference(target);

        harness.SetStreamingCallback(callback);
        // Smoke that the closure works through the box.
        harness.Fire(value: 7);
        // Drop Swift's strong reference — releases the _SBClosureCtx box,
        // which deinits and frees the underlying GCHandle.
        harness.ClearStreamingCallback();

        // Drop local roots. Both harness and callback become unreachable
        // when this frame returns; only `weak` survives via the return.
        callback = null!;
        target = null!;
        harness.Dispose();
        return weak;
    }

    /// <summary>
    /// Bulk regression: N rounds of set+clear must not accumulate live
    /// delegate targets. Pre-fix the count grew linearly; post-fix the count
    /// must collapse to a small constant (conservative-stack-scan noise floor).
    /// </summary>
    [SkipOnSimulator("N-3 owner-token box lives in libSwiftBindingsRuntime.dylib; " +
        "RuntimeTestsApp omits the dylib via IncludeSwiftBindingsRuntimeNative=false, so " +
        "the wrapper's no-deinit _SBClosureCtxFallback preserves the prior leak on " +
        "simulator by design. Device build loads the framework via NativeReference and " +
        "this assertion holds. Same root cause as TestClearedClosureReleasesDelegateTarget.")]
    public void TestBulkSetClearDoesNotAccumulateTargets()
    {
        var weaks = new List<WeakReference>(BulkIterations);
        for (int i = 0; i < BulkIterations; i++)
            weaks.Add(SetClearRound(i));

        ForceGc();

        int alive = 0;
        foreach (var w in weaks)
            if (w.IsAlive) alive++;

        TestLogger.Info($"[EscapingClosureLifetime] {BulkIterations}x set+clear: alive={alive}");
        AssertTrue(
            alive <= MaxResidualAlive,
            $"{alive} of {BulkIterations} escaping-closure targets stayed alive after GC " +
            $"(tolerance {MaxResidualAlive}). The _SBClosureCtx box deinit is not firing.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference SetClearRound(int seed)
    {
        var harness = new StreamingCallbackHarness();
        var target = new CapturedTarget();
        Action<int> callback = target.Receive;
        var weak = new WeakReference(target);

        harness.SetStreamingCallback(callback);
        harness.Fire(value: seed);
        harness.ClearStreamingCallback();

        callback = null!;
        target = null!;
        harness.Dispose();
        return weak;
    }
}

/// <summary>
/// Cheap allocation site used as the WeakReference target so we can detect
/// whether the captured delegate's hidden GCHandle has been freed. The field
/// makes the type non-empty so it survives Roslyn dead-code-style collapse.
/// </summary>
internal sealed class CapturedTarget
{
    private int _last;
    public void Receive(int value) => _last = value;
}
