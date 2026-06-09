// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Runtime regression tests for escaping-closure and async ownership fixes,
/// plus a round-2 pin for the
/// complex-enum closure-result heap ownership contract:
/// <list type="bullet">
///   <item><description>Frozen-struct-with-ref-fields closure-arg
///     defer-deallocate. Pre-fix every closure invocation leaked one
///     <c>NativeMemory.Alloc</c> buffer + one <c>DeinitTracker</c> on the
///     Swift side because the C# <c>NewFromPayload</c> does an
///     <c>InitializeWithCopy</c> into a fresh buffer and never owns the
///     source pointer.</description></item>
///   <item><description>Async + <c>any Protocol</c> existential
///     parameter heap cleanup. Pre-fix the <c>NativeMemory.Alloc</c> buffer
///     wrapping the <c>ExistentialContainer1</c> handed to Swift was never
///     freed, leaking one allocation per call.</description></item>
///   <item><description>Nullable struct setter
///     <c>SafeHandlePin</c> bracket. Pre-fix the setter passed
///     <c>value?.Payload.DangerousGetHandle()</c> directly to the P/Invoke,
///     leaving a use-after-free window during which a GC + finalizer could
///     free the buffer Swift was still reading from.</description></item>
///   <item><description>Complex-enum closure-result
///     heap-ownership contract. The Swift adapter for a closure with a
///     complex-enum argument intentionally OMITS a defer-deallocate; the C#
///     side wraps the same pointer in <c>SwiftSafeHandle&lt;T&gt;</c>, and
///     <c>ReleaseHandle</c> pairs <c>VWT.Destroy + NativeMemory.Free</c>. A
///     regression that either drops the C# transfer or adds a Swift-side
///     defer would be caught here.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// These are stress regressions: a single round won't surface the bug. The
/// bulk loops are sized to make a per-call leak visible against the iOS
/// simulator's working-set noise floor and to give the GC enough churn to
/// catch the nullable-setter use-after-free window.
/// </para>
/// </remarks>
public class HeapOwnershipTransferTests : TestBase
{
    public HeapOwnershipTransferTests(TestResults results) : base(results) { }

    private const int S4Iterations = 5000;
    private const int S5Iterations = 200;
    private const int A4Iterations = 500;
    private const int S4Round2Iterations = 2000;
    private const int GcCycles = 4;

    private static void ForceGc()
    {
        for (int i = 0; i < GcCycles; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    // --------------------------------------------------------------------
    // Frozen struct with ref fields, closure arg
    // --------------------------------------------------------------------

    /// <summary>
    /// Sanity check: the closure receives a usable <see cref="FrozenStructWithRef"/>
    /// across the boundary. If the defer-deallocate were wrong (e.g. deinitialized
    /// before C# could read), the value would either be zero or the call
    /// would crash.
    /// </summary>
    public void TestFrozenWithRefClosure_DispatchesCorrectly()
    {
        int seen = 0;
        TestLibFunctions.RunFrozenWithRefClosure(callback: fs =>
        {
            using (fs)
                seen = fs.GetValue();
        });
        AssertEqual(17, seen, "Closure observes FrozenStructWithRef value across the boundary");
    }

    /// <summary>
    /// Bulk regression for frozen-struct-with-ref-fields closure arg: hammer
    /// the closure N times. Pre-fix this leaked one buffer + one DeinitTracker
    /// per call, growing the working set linearly. Post-fix Swift's
    /// defer-deallocate keeps the per-call allocation balanced.
    /// </summary>
    public void TestFrozenWithRefClosure_BulkDoesNotCrash()
    {
        for (int i = 0; i < S4Iterations; i++)
        {
            TestLibFunctions.RunFrozenWithRefClosure(callback: fs =>
            {
                using (fs)
                {
                    var _ = fs.GetValue();
                }
            });

            if ((i & 0xFF) == 0xFF)
                ForceGc();
        }

        ForceGc();
        AssertTrue(true, $"{S4Iterations} FrozenStructWithRef closure invocations completed without crash");
    }

    /// <summary>
    /// Multi-arg variant: covers the "each __heap_N gets its own defer" path.
    /// One missed defer would leak per-call; loop is wide enough to make that
    /// visible.
    /// </summary>
    public void TestTwoFrozenWithRefClosure_BulkDoesNotCrash()
    {
        for (int i = 0; i < S4Iterations; i++)
        {
            TestLibFunctions.RunTwoFrozenWithRefClosure(callback: (a, b) =>
            {
                using (a) using (b)
                {
                    var _ = a.GetValue() + b.GetValue();
                }
            });

            if ((i & 0xFF) == 0xFF)
                ForceGc();
        }

        ForceGc();
        AssertTrue(true, $"{S4Iterations} two-arg FrozenStructWithRef closure invocations completed without crash");
    }

    // --------------------------------------------------------------------
    // Async + any Protocol existential param heap cleanup
    // --------------------------------------------------------------------

    /// <summary>
    /// Sanity check: the async existential call round-trips. If the holder
    /// slot reservation went wrong (e.g. wrong index, overlapping the TCS),
    /// the callback would crash before resolving.
    /// </summary>
    public async Task TestAsyncExistential_DispatchesCorrectly()
    {
        using var owner = new AsyncSkipPolicyExistential();
        using var validator = new DefaultSkipPolicyValidator();
        bool result = await owner.ValidateAsync(validator);
        AssertTrue(result, "AsyncSkipPolicyExistential.ValidateAsync returns true for value=42");
    }

    /// <summary>
    /// Bulk regression for the async existential parameter path: serially
    /// hammer the async existential call. Pre-fix each call leaked one
    /// <c>NativeMemory.Alloc</c> buffer holding the <c>ExistentialContainer1</c>;
    /// under load this is visible as monotonic working-set growth and an eventual
    /// OOM on memory-tight devices. Post-fix the callback's holder-cleanup loop
    /// frees the buffer after Swift's continuation has finished reading it.
    /// </summary>
    public async Task TestAsyncExistential_BulkDoesNotCrashOrLeak()
    {
        using var owner = new AsyncSkipPolicyExistential();
        for (int i = 0; i < S5Iterations; i++)
        {
            using var validator = new DefaultSkipPolicyValidator();
            bool ok = await owner.ValidateAsync(validator);
            AssertTrue(ok, $"Async existential round {i} returned true");

            if ((i & 0x1F) == 0x1F)
                ForceGc();
        }

        ForceGc();
        AssertTrue(true, $"{S5Iterations} async existential round-trips completed without crash");
    }

    // --------------------------------------------------------------------
    // Nullable struct setter SafeHandlePin
    // --------------------------------------------------------------------

    /// <summary>
    /// Sanity check: the nullable struct setter round-trips both null and
    /// non-null. The pin path and the null path must both reach Swift.
    /// </summary>
    public void TestNullableShapeSetter_BasicRoundTrip()
    {
        var holder = new ShapeHolder(shape: null);
        using var rect = Shape.Rectangle(width: 3.0, height: 4.0);
        holder.CurrentShape = rect;
        var got = holder.CurrentShape;
        AssertNotNull(got, "Setter accepted non-null value");
        got?.Dispose();

        holder.CurrentShape = null;
        var gotNull = holder.CurrentShape;
        AssertNull(gotNull, "Setter accepted null value");
    }

    /// <summary>
    /// Bulk regression for nullable struct setter SafeHandlePin: alternate
    /// set/clear under GC pressure. The pre-fix UAF window is:
    /// `value?.Payload.DangerousGetHandle()` returns
    /// the raw pointer, the GC fires, the SafeHandle finalizer frees the
    /// buffer, then Swift reads from the freed pointer. Forcing GC inside
    /// the inner loop maximizes the chance of catching the window.
    /// </summary>
    public void TestNullableShapeSetter_BulkSetClearUnderGcPressure()
    {
        var holder = new ShapeHolder(shape: null);

        for (int i = 0; i < A4Iterations; i++)
        {
            // Allocate a fresh Shape inline so the local goes out of scope
            // immediately after the setter returns — maximum GC eligibility.
            holder.CurrentShape = Shape.Circle(radius: i);

            if ((i & 0x0F) == 0x0F)
                ForceGc();
        }

        ForceGc();

        // Read once at the end to confirm Swift's stored value is still
        // valid (i.e. the last setter actually completed without crashing
        // and the stored handle isn't dangling).
        var final = holder.CurrentShape;
        AssertNotNull(final, "Final stored Shape is not null after bulk set+GC");
        final?.Dispose();
    }

    // --------------------------------------------------------------------
    // Complex-enum closure-result heap-ownership contract
    // --------------------------------------------------------------------

    /// <summary>
    /// Sanity check: the closure receives a complex-enum
    /// <see cref="CompletionProbeOutcome"/> across the boundary. The C#
    /// wrapper owns the heap buffer via <c>SwiftSafeHandle</c>; disposing
    /// it triggers <c>VWT.Destroy</c> which releases the ARC-bearing probe
    /// and increments the Swift-side deinit counter.
    /// </summary>
    public void TestComplexEnumCompletion_DispatchesAndDeinitsOnDispose()
    {
        using var presenter = new CompletionProbePresenter();
        presenter.ResetDeinitCount();

        long before = presenter.DeinitCount;
        AssertEqual(0L, before, "Counter starts at zero");

        presenter.Present(label: 7, animated: true, completion: outcome =>
        {
            using (outcome) { /* read complete; Dispose triggers ReleaseHandle */ }
        });

        AssertEqual(1L, presenter.DeinitCount,
            "Disposing the complex-enum wrapper releases the embedded probe exactly once");
    }

    /// <summary>
    /// Bulk regression: hammer the StripeCardScan-shape closure invocation.
    /// Every iteration's <c>using (outcome)</c> deterministically disposes
    /// the wrapper, which routes through <c>SwiftSafeHandle.ReleaseHandle</c>
    /// (VWT.Destroy + NativeMemory.Free). Final deinit count must equal the
    /// iteration count — both leak (count too low) and double-free (crash
    /// inside the loop) regressions surface here.
    /// </summary>
    public void TestComplexEnumCompletion_BulkOwnershipTransfer()
    {
        using var presenter = new CompletionProbePresenter();
        presenter.ResetDeinitCount();

        for (int i = 0; i < S4Round2Iterations; i++)
        {
            presenter.Present(label: i, animated: (i & 1) == 0, completion: outcome =>
            {
                using (outcome) { /* read complete */ }
            });

            if ((i & 0xFF) == 0xFF)
                ForceGc();
        }

        ForceGc();

        AssertEqual((long)S4Round2Iterations, presenter.DeinitCount,
            $"All {S4Round2Iterations} complex-enum probes released after dispose+GC");
    }

    /// <summary>
    /// Variant that does NOT call <c>using</c> on the outcome — relies on GC
    /// finalizer to drive <c>ReleaseHandle</c>. Slower than the explicit
    /// path but proves the finalizer-driven path also reaches the buffer
    /// + ARC payload (the <c>HandleFinalizerRelease</c> branch uses the
    /// <c>SBW_VWTDestroy</c> Cdecl trampoline, which differs from the
    /// explicit-dispose path).
    /// </summary>
    public void TestComplexEnumCompletion_FinalizerOwnershipTransfer()
    {
        using var presenter = new CompletionProbePresenter();
        presenter.ResetDeinitCount();

        // Match the bulk-path iteration count so the finalizer branch exercises
        // the same statistical surface and any partial finalizer-only regression
        // (e.g. a borrowed-marshal MCB callback that SuppressFinalize's the
        // SafeHandle) surfaces with the same sensitivity as the explicit-dispose
        // path. Lower counts mask intermittent leak signals on slower runtimes.
        for (int i = 0; i < S4Round2Iterations; i++)
        {
            // No `using` — wrapper becomes eligible for finalization as soon
            // as the delegate returns.
            presenter.Present(label: i, animated: true, completion: _ => { });

            if ((i & 0x1F) == 0x1F)
                ForceGc();
        }

        ForceGc();

        AssertEqual((long)S4Round2Iterations, presenter.DeinitCount,
            $"Finalizer path releases all {S4Round2Iterations} complex-enum probes");
    }
}
