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
/// same <c>_SBClosureCtx</c> deinit → free-trampoline channel. The Nuke
/// harness injects the native runtime into both simulator and device app bundles.
/// <see cref="ClosureOwnerTokenCapabilityTests"/> observes the actual factory,
/// unboxing and registered destroy callback; a missing injected runtime is a
/// qualification failure rather than a platform-based lifetime exclusion.
/// </para>
/// </remarks>
public class AsyncClosureContextLifetimeTests : TestBase
{
    public AsyncClosureContextLifetimeTests(TestResults results) : base(results) { }

    private const int BulkIterations = 25;
    private const int MaxResidualAlive = 5;

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
    public async Task TestOneShotAsyncClosureReleasesDelegateTarget()
    {
        // Keep the test runner free while the dedicated allocating thread waits
        // for native completion. The pool thread only joins; it never allocates a target.
        var weakTarget = await Task.Run(() => RunOnFinishedThread(InvokeAsyncClosureAndReturnWeakRef));

        ForceGCThorough();

        AssertTrue(
            !weakTarget.IsAlive,
            "After a one-shot async-closure call completes, the captured delegate target must become " +
            "collectible. If the _SBClosureCtx box deinit didn't fire, the per-call GCHandle would still " +
            "root the target (the pre-fix intentional leak).");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private WeakReference InvokeAsyncClosureAndReturnWeakRef()
    {
        var target = new CapturedTarget();
        // Capture in a separate method: clearing this method's local target must
        // not clear the delegate's captured field and disguise a leaked GCHandle.
        Func<Task<int>> userLambda = CaptureTarget(target, 7);
        var weak = new WeakReference(target);

        var result = WithTimeout(
            Functions.CallAsyncThrowingClosureAsync(userLambda),
            DefaultAsyncTimeout).GetAwaiter().GetResult();
        AssertEqual(7, result, "Async closure round-trip returned the expected value");

        // These locals are distinct from the delegate's captured field. Only the
        // weak observation leaves this helper and its dedicated allocating thread.
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
    public async Task TestBulkAsyncInvokeDoesNotAccumulateTargets()
    {
        var weaks = await Task.Run(() => RunOnFinishedThread(() =>
        {
            var observations = new List<WeakReference>(BulkIterations);
            for (int i = 0; i < BulkIterations; i++)
                observations.Add(AsyncInvokeRound(i));
            return observations;
        }));

        ForceGCThorough();

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
    private WeakReference AsyncInvokeRound(int seed)
    {
        var target = new CapturedTarget();
        Func<Task<int>> userLambda = CaptureTarget(target, seed);
        var weak = new WeakReference(target);

        WithTimeout(
            Functions.CallAsyncThrowingClosureAsync(userLambda),
            DefaultAsyncTimeout).GetAwaiter().GetResult();

        userLambda = null!;
        target = null!;
        return weak;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Func<Task<int>> CaptureTarget(CapturedTarget target, int value)
        => () => { target.Receive(value); return Task.FromResult(value); };
}
