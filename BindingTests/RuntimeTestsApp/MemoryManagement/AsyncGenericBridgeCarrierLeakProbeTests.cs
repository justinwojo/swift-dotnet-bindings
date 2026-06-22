// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Probes the async <em>generic-bridge</em> result-carrier release (a separate emission from the
/// non-generic async harness covered by <see cref="AsyncCollectionCarrierLeakProbeTests"/>).
///
/// A method-level generic parameter whose constraint protocol refines <c>AnyObject</c> routes an
/// async method through <c>AsyncMethodGenericBridgeEmitter</c>: the bridge opens the conformer via
/// <c>Unmanaged&lt;AnyObject&gt;.fromOpaque</c> and writes the result into a carrier via
/// <c>initializeMemory(as: T.self, repeating: result, count: 1)</c> — the type's copy witness, a +1
/// on any internal references. For a non-frozen (resilient) struct return the completion callback
/// takes the carrier-owns arm: it copies the carrier into a SafeHandle-owned buffer, then must
/// value-witness-Destroy the original carrier before <c>SBW_Free</c>, or the embedded reference's +1
/// is orphaned every call.
///
/// <see cref="GenericBridgeReturns.WrapAsync"/> returns a <c>TrackedRefStruct</c> embedding a
/// <see cref="LifetimeTracker"/>-counted <c>TrackedRef</c>, so a missed carrier Destroy surfaces as a
/// non-zero live count after the wrappers are disposed and the GC has drained — not merely as "does
/// not crash". The dispose loop runs in a <c>[MethodImpl(NoInlining)]</c> async helper so the
/// completed state machine (holding the awaited carrier local) is collectible before the assertion,
/// and each call is bounded by <c>WithTimeout(DefaultAsyncTimeout)</c> so a regressed callback fails
/// bounded instead of hanging the run.
/// </summary>
public class AsyncGenericBridgeCarrierLeakProbeTests : TestBase
{
    public AsyncGenericBridgeCarrierLeakProbeTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>
    /// <c>async&lt;Seed: AnyObject-bound&gt; -&gt; TrackedRefStruct</c> through the generic bridge: the
    /// carrier holds a +1 on the embedded <c>TrackedRef</c>. The callback's carrier-owns arm must
    /// value-witness-Destroy the carrier; a leak pins one <c>TrackedRef</c> per awaited call.
    /// </summary>
    public async Task TestAsyncGenericBridgeStructReturnReleasesCarrier()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        await AllocAndDisposeBridgeStructsAsync(50);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks(
            "async generic-bridge -> TrackedRefStruct must value-witness-Destroy the result carrier (releasing the embedded TrackedRef +1)");
        TestLogger.Info("async generic-bridge TrackedRefStruct: 50 awaited returns all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AllocAndDisposeBridgeStructsAsync(int iterations)
    {
        // The seed is a plain heap conformer (not LifetimeTracker-counted); reuse one across the
        // loop. The bridge opens it via takeUnretainedValue, so it must outlive each await.
        using var seed = new CountSeed(7);
        for (int i = 0; i < iterations; i++)
        {
            var result = await WithTimeout(GenericBridgeReturns.WrapAsync(seed), DefaultAsyncTimeout);
            // The SafeHandle owns the marshalled copy's +1 on the embedded TrackedRef; disposing it
            // runs VWT Destroy on that copy. The carrier's own +1 was released in the callback.
            result.Dispose();
        }
    }

    /// <summary>
    /// <c>async&lt;Seed: AnyObject-bound&gt; -&gt; FrozenTrackedRefStruct</c> through the generic bridge.
    /// A frozen-struct-with-ref-fields return projects to the ClassWithBufferStruct path, so the
    /// callback takes the SEPARATE <c>carrierNeedsDestroy</c> arm (distinct from <see
    /// cref="TestAsyncGenericBridgeStructReturnReleasesCarrier"/>'s non-frozen ClassWithOpaquePayload
    /// arm): <c>NewFromPayload</c> copies the payload into a managed buffer, and the callback must
    /// STILL value-witness-Destroy the original carrier. If that Destroy is dropped the embedded
    /// <c>TrackedRef</c> +1 is orphaned every awaited call — covering the frozen arm independently so a
    /// regression localized to it cannot hide behind the non-frozen probe.
    /// </summary>
    public async Task TestAsyncGenericBridgeFrozenStructReturnReleasesCarrier()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        await AllocAndDisposeBridgeFrozenStructsAsync(50);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks(
            "async generic-bridge -> FrozenTrackedRefStruct must value-witness-Destroy the result carrier (carrierNeedsDestroy arm) so the embedded TrackedRef +1 is released");
        TestLogger.Info("async generic-bridge FrozenTrackedRefStruct: 50 awaited returns all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AllocAndDisposeBridgeFrozenStructsAsync(int iterations)
    {
        using var seed = new CountSeed(7);
        for (int i = 0; i < iterations; i++)
        {
            var result = await WithTimeout(GenericBridgeReturns.WrapFrozenAsync(seed), DefaultAsyncTimeout);
            // The managed buffer owns the NewFromPayload copy's +1; disposing it releases that copy.
            // The original carrier's +1 must have been released in the callback's carrierNeedsDestroy arm.
            result.Dispose();
        }
    }
}
