// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Lifetime tests for the concrete-specialization ("CSM") overloads' receiver and arguments.
/// A specialized overload forwards its receiver and every native-backed argument to a Swift
/// wrapper; those handles must be leased for the duration of the call so a concurrent
/// <c>Dispose()</c> cannot free the native storage under a call that is still inside Swift, and a
/// handle that is ALREADY disposed must be rejected before the call rather than dereferenced.
///
/// The gate functions below park the specialized Swift body so a test can dispose an argument
/// while the call is demonstrably still running — a <c>GC.KeepAlive</c>-shaped guard would pass a
/// GC-pressure test but still fail this one.
/// </summary>
public class CsmArgumentLeaseTests : TestBase
{
    public CsmArgumentLeaseTests(TestResults results) : base(results) { }

    private const int GateTimeoutMs = 15000;

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_LeaseGateArm")]
    private static extern void LeaseGateArm();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_LeaseGateReset")]
    private static extern void LeaseGateReset();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_LeaseGateEntryCount")]
    private static extern int LeaseGateEntryCount();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_LeaseGateAwaitEntry")]
    private static extern int LeaseGateAwaitEntry(int timeoutMilliseconds);

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_LeaseGateRelease")]
    private static extern void LeaseGateRelease();

    #region Live call vs. concurrent Dispose

    /// <summary>
    /// Disposes a CLASS conformer argument on another thread while the specialized call is parked
    /// inside Swift. Without a lease the ARC payload is released mid-call and the Swift body reads
    /// freed storage; with one, the handle stays alive until the call returns.
    /// </summary>
    public void TestClassArgumentSurvivesDisposeDuringLiveCall()
    {
        LeaseGateReset();
        using var probe = new LeaseProbe(realm: "live-ref");
        var material = new LeasedRefMaterial(token: "m1");
        var context = new LeaseContext(label: "ctx");

        var outcome = RunGatedCall(() => probe.ConsumeGated(material, context), () =>
        {
            material.Dispose();
            context.Dispose();
        });

        AssertNull(outcome.Failure, "specialized call with a concurrently-disposed class argument must not throw");
        AssertEqual("live-ref|ctx|ref:m1", outcome.Result, "leased class argument survives until the call returns");
    }

    /// <summary>
    /// Same race with a NON-FROZEN STRUCT conformer, whose payload is an opaque buffer handle
    /// rather than an ARC reference — the sibling payload category through the same parameter arm.
    /// Only the struct argument is disposed here, so a green result is attributable to that arm
    /// rather than to the class parameter beside it. Note this is a no-crash/no-corruption probe,
    /// not the discriminating check for the struct arm: the wrapper copies the value out of the
    /// buffer (<c>assumingMemoryBound(...).pointee</c>) before the body parks, so freeing the
    /// buffer mid-call no longer reaches the copy Swift is reading.
    /// <see cref="TestDisposedStructArgumentRejectedBeforeNativeCall"/> is the check that goes red
    /// on an unleased struct argument.
    /// </summary>
    public void TestStructArgumentSurvivesDisposeDuringLiveCall()
    {
        LeaseGateReset();
        using var probe = new LeaseProbe(realm: "live-val");
        var material = new LeasedValueMaterial(token: "m2");
        using var context = new LeaseContext(label: "ctx");

        var outcome = RunGatedCall(() => probe.ConsumeGated(material, context), () => material.Dispose());

        AssertNull(outcome.Failure, "specialized call with a concurrently-disposed struct argument must not throw");
        AssertEqual("live-val|ctx|value:m2", outcome.Result, "leased struct argument survives until the call returns");
    }

    /// <summary>
    /// Disposes the RECEIVER of a class-hosted specialized call while it is parked inside Swift.
    /// The receiver is forwarded through the same mechanism as the arguments, so it must be leased
    /// too.
    /// </summary>
    public void TestClassReceiverSurvivesDisposeDuringLiveCall()
    {
        LeaseGateReset();
        var probe = new LeaseProbeRef(realm: "live-self");
        using var material = new LeasedRefMaterial(token: "m3");

        var outcome = RunGatedCall(() => probe.ConsumeGated(material), () => probe.Dispose());

        AssertNull(outcome.Failure, "specialized call with a concurrently-disposed receiver must not throw");
        AssertEqual("live-self|ref:m3", outcome.Result, "leased receiver survives until the call returns");
    }

    #endregion

    #region Disposed input rejected before the native call

    /// <summary>
    /// An already-disposed argument must be rejected with <see cref="ObjectDisposedException"/>
    /// and the Swift body must never run. Passing the raw pointer out of a closed handle would
    /// instead dereference freed storage.
    /// </summary>
    public void TestDisposedClassArgumentRejectedBeforeNativeCall()
    {
        LeaseGateReset();
        using var probe = new LeaseProbe(realm: "dead");
        var material = new LeasedRefMaterial(token: "gone");
        using var context = new LeaseContext(label: "ctx");
        material.Dispose();

        AssertThrows<ObjectDisposedException>(
            () => probe.ConsumeGated(material, context),
            "disposed class argument is rejected before the specialized call");
        AssertEqual(0, LeaseGateEntryCount(), "no specialized Swift body ran for the disposed class argument");
    }

    /// <summary>Same rejection for a disposed non-frozen-struct conformer argument.</summary>
    public void TestDisposedStructArgumentRejectedBeforeNativeCall()
    {
        LeaseGateReset();
        using var probe = new LeaseProbe(realm: "dead");
        var material = new LeasedValueMaterial(token: "gone");
        using var context = new LeaseContext(label: "ctx");
        material.Dispose();

        AssertThrows<ObjectDisposedException>(
            () => probe.ConsumeGated(material, context),
            "disposed struct argument is rejected before the specialized call");
        AssertEqual(0, LeaseGateEntryCount(), "no specialized Swift body ran for the disposed struct argument");
    }

    /// <summary>
    /// Same rejection for a disposed CONCRETE class parameter — it rides the plain payload-handle
    /// parameter arm rather than the conformer arm, so it is leased by a different branch of the
    /// emitter and needs its own assertion.
    /// </summary>
    public void TestDisposedConcreteParameterRejectedBeforeNativeCall()
    {
        LeaseGateReset();
        using var probe = new LeaseProbe(realm: "dead");
        using var material = new LeasedRefMaterial(token: "live");
        var context = new LeaseContext(label: "gone");
        context.Dispose();

        AssertThrows<ObjectDisposedException>(
            () => probe.ConsumeGated(material, context),
            "disposed concrete class parameter is rejected before the specialized call");
        AssertEqual(0, LeaseGateEntryCount(), "no specialized Swift body ran for the disposed concrete parameter");
    }

    /// <summary>Same rejection when the RECEIVER itself is already disposed.</summary>
    public void TestDisposedReceiverRejectedBeforeNativeCall()
    {
        LeaseGateReset();
        var probe = new LeaseProbeRef(realm: "dead");
        using var material = new LeasedRefMaterial(token: "live");
        probe.Dispose();

        AssertThrows<ObjectDisposedException>(
            () => probe.ConsumeGated(material),
            "disposed receiver is rejected before the specialized call");
        AssertEqual(0, LeaseGateEntryCount(), "no specialized Swift body ran for the disposed receiver");
    }

    /// <summary>
    /// Same rejection when the receiver is a STRUCT host rather than a class. The two receiver
    /// shapes reach the wrapper through different emitted expressions (a class forwards its public
    /// <c>Payload</c> property, a struct/enum host forwards its <c>_payload</c> field), so a
    /// regression that left only the struct shape passing a raw pointer would not be visible in the
    /// class test above. This is the discriminating check for that arm — the concurrent-dispose
    /// shape is not, because the wrapper copies the value out of the buffer before the body parks.
    /// </summary>
    public void TestDisposedStructReceiverRejectedBeforeNativeCall()
    {
        LeaseGateReset();
        var probe = new LeaseProbe(realm: "dead");
        using var material = new LeasedRefMaterial(token: "live");
        using var context = new LeaseContext(label: "ctx");
        probe.Dispose();

        AssertThrows<ObjectDisposedException>(
            () => probe.ConsumeGated(material, context),
            "disposed struct receiver is rejected before the specialized call");
        AssertEqual(0, LeaseGateEntryCount(), "no specialized Swift body ran for the disposed struct receiver");
    }

    #endregion

    #region Ownership-transfer return, rejected before the native call

    /// <summary>
    /// A specialized factory whose result travels through an indirect-result buffer that the
    /// RETURNED handle adopts allocates that buffer BEFORE the call. If an argument is already
    /// disposed, the <c>SafeHandle</c> marshaller throws before native code is entered — no handle
    /// ever takes the buffer, so the emitted factory has to reclaim it itself on that path.
    ///
    /// What this test can and cannot observe: the byte-level leak is not deterministically visible
    /// from managed code (the buffer comes from <c>NativeMemory.Alloc</c>, which exposes no
    /// allocation counter, and resident-size sampling is far too noisy at 16-byte granularity).
    /// What it does pin is the half a unit test cannot: that the rejection really happens on the
    /// pre-native side of the boundary — the Swift body never runs — which is what makes the free
    /// the caller's responsibility in the first place. The emitter unit tests carry the matching
    /// assertion that the emitted factory frees on exactly that path.
    /// </summary>
    public void TestDisposedArgumentRejectedBeforeOwnershipTransferReturn()
    {
        LeaseGateReset();
        var material = new LeasedRefMaterial(token: "gone");
        material.Dispose();

        AssertThrows<ObjectDisposedException>(
            () => LeasedResultBox.FromSwiftBindingsTestLibLeasedRefMaterial(material),
            "disposed conformer argument is rejected before the ownership-transfer factory calls Swift");
        AssertEqual(0, LeaseGateEntryCount(), "no specialized Swift body ran for the rejected ownership-transfer factory");
    }

    /// <summary>Same rejection through the non-frozen-struct conformer arm of the same factory.</summary>
    public void TestDisposedStructArgumentRejectedBeforeOwnershipTransferReturn()
    {
        LeaseGateReset();
        var material = new LeasedValueMaterial(token: "gone");
        material.Dispose();

        AssertThrows<ObjectDisposedException>(
            () => LeasedResultBox.FromSwiftBindingsTestLibLeasedValueMaterial(material),
            "disposed struct conformer argument is rejected before the ownership-transfer factory calls Swift");
        AssertEqual(0, LeaseGateEntryCount(), "no specialized Swift body ran for the rejected struct ownership-transfer factory");
    }

    /// <summary>
    /// The success half of the same factory: the result must reach the caller intact through the
    /// buffer the returned handle adopts, and the handle must still own it afterwards. Same
    /// observability limit as the rejection tests — managed code cannot count native frees — so
    /// what is asserted is what is visible: the value round-trips, the Swift body ran, and the
    /// handle releases on Dispose. Reclaiming the buffer unconditionally in the emitted factory
    /// instead of only on the pre-handoff paths would free it out from under this handle, which
    /// shows up here as a crash or a corrupted read rather than as a failed assertion; the
    /// single-reclaim-site assertion lives in the emitter unit tests.
    /// </summary>
    public void TestOwnershipTransferReturnRoundTripsAndReleasesOnce()
    {
        LeaseGateReset();
        using var material = new LeasedRefMaterial(token: "ok");

        var box = LeasedResultBox.FromSwiftBindingsTestLibLeasedRefMaterial(material);
        AssertEqual("boxed[ref:ok]", box.Descriptor, "ownership-transfer factory round-trips its result through the adopted buffer");
        AssertEqual(1, LeaseGateEntryCount(), "the specialized Swift body ran exactly once");

        // The handle owns the buffer from the marshal call onward; disposing it is the single free.
        box.Dispose();
        AssertThrows<ObjectDisposedException>(
            () => { _ = box.Descriptor; },
            "the returned handle owns the transferred buffer and releases it on Dispose");
    }

    #endregion

    #region GC pressure

    /// <summary>
    /// Drives many specialized calls whose arguments are reachable ONLY through the call
    /// expression, collecting and draining finalizers between batches. Collection happens BETWEEN
    /// calls rather than while one is parked in Swift, so this is a churn/stability probe over the
    /// leased path — it does not by itself distinguish a lease from a raw pointer. The gated
    /// dispose tests above are the discriminating checks; this one guards against the leased
    /// SafeHandle arguments accumulating or being torn down out from under a later call.
    /// </summary>
    public void TestArgumentsSurviveGCPressureAcrossSpecializedCalls()
    {
        LeaseGateReset();
        using var probe = new LeaseProbe(realm: "gc");

        for (int i = 0; i < 200; i++)
        {
            // Both arguments are temporaries: after the call is entered, nothing but the call
            // itself keeps them reachable.
            var result = probe.ConsumeGated(new LeasedRefMaterial(token: $"m{i}"), new LeaseContext(label: "c"));
            AssertEqual($"gc|c|ref:m{i}", result, $"specialized call {i} round-trips under GC pressure");

            if (i % 25 == 0)
            {
                ForceGC();
            }
        }

        // Drain what the loop's last batch left queued, so the handles this test created are
        // released inside this test rather than during whichever later test next drains.
        ForceGC();

        AssertEqual(200, LeaseGateEntryCount(), "every specialized call reached the Swift body");
    }

    #endregion

    #region Helpers

    private readonly struct GatedOutcome
    {
        public GatedOutcome(string? result, Exception? failure)
        {
            Result = result;
            Failure = failure;
        }

        public string? Result { get; }
        public Exception? Failure { get; }
    }

    /// <summary>
    /// Arms the Swift gate, runs <paramref name="call"/> on a worker thread until it parks inside
    /// the specialized body, runs <paramref name="whileParked"/> on THIS thread, then releases the
    /// call and returns its outcome.
    /// </summary>
    private GatedOutcome RunGatedCall(Func<string> call, Action whileParked)
    {
        string? result = null;
        Exception? failure = null;
        bool joined = false;

        LeaseGateArm();
        var worker = new Thread(() =>
        {
            try
            {
                result = call();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
        };
        worker.Start();

        try
        {
            AssertEqual(1, LeaseGateAwaitEntry(GateTimeoutMs), "specialized call parked inside the Swift body");
            whileParked();
        }
        finally
        {
            LeaseGateRelease();
            joined = worker.Join(GateTimeoutMs);
            if (joined)
            {
                // Only safe to reset the process-global gate once the worker is provably out of
                // the Swift body — resetting under a still-parked worker would hand the next test
                // a counter and a pair of semaphores the previous call is still using.
                LeaseGateReset();
            }
            else
            {
                TestLogger.Info("Gated specialized call did not return before the timeout; leaving the gate untouched");
            }
        }

        AssertTrue(joined, "gated specialized call returned before the timeout");

        return new GatedOutcome(result, failure);
    }

    #endregion
}
