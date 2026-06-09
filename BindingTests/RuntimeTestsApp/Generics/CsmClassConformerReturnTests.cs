// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime gate for the CSM (Concrete Specialization Mechanism) class-conformer
/// return path. A generic method returning its own type parameter with a CLASS conformer
/// (<c>func f&lt;T: CarrierItem&gt;(_ t: T) -&gt; T</c>) routes through the carrier path:
/// the Swift @_cdecl wrapper stores the instance pointer INTO a one-word indirect-return
/// carrier via <c>initializeMemory</c> (carrier owns +1), and the generated C# must read the
/// slot's contents and adopt that +1, then raw-free the carrier — NOT wrap the carrier
/// address as the instance.
///
/// The old emission wrapped the freed carrier ADDRESS as the instance, which is
/// (a) a use-after-free — every read dereferences the freed one-word carrier — and (b) a leak
/// of the real instance, whose carrier-held +1 was never adopted. These tests pin both halves:
/// value round-trips + survival-after-churn catch the UAF; the live-count 1-then-0 probe
/// catches the leak.
///
/// <see cref="CarrierBox.RelayThrough"/> takes a user parameter literally named
/// <c>resultPtr</c> — the spelling of the synthetic indirect-return local the CSM emitter
/// hardcodes. The binding compiles only because the synthetic is escaped (to <c>__resultPtr</c>);
/// this test confirms the escaped path still round-trips at runtime.
/// </summary>
public class CsmClassConformerReturnTests : TestBase
{
    public CsmClassConformerReturnTests(TestResults results) : base(results) { }

    /// <summary>
    /// UAF probe: the returned instance's stored tag must equal the input's, and must stay
    /// readable after heap churn. A carrier-address use-after-free would read reused/garbage
    /// memory on the second read (or crash).
    /// </summary>
    public void TestCarry_ClassConformer_RoundTripsAndSurvivesChurn()
    {
        using var box = new CarrierBox();
        using var item = new CarrierClass(carrierTag: 42);
        using var result = box.Carry(item);
        AssertEqual(42, result.CarrierTag, "Carry<CarrierClass> first read");

        // Perturb the heap. If `result` wrapped a freed carrier word, the freed memory may
        // now be reused and the re-read would observe a different value (or fault).
        using var churn1 = new CarrierClass(carrierTag: 7);
        using var churn2 = new CarrierClass(carrierTag: 99);
        AssertEqual(42, result.CarrierTag, "Carry<CarrierClass> read survives allocation churn (UAF probe)");
    }

    /// <summary>
    /// Leak probe structured around a surviving owner (live count 1 then 0). After the input
    /// wrapper is released, the distinct result wrapper still owns the SAME Swift instance via
    /// the adopted +1, so live count is 1; releasing it brings live to 0. The old carrier-wrap
    /// bug never adopted the carrier's +1, so the real instance leaked (live stuck above 0).
    /// </summary>
    public void TestCarry_ClassConformer_NoLeak()
    {
        LifetimeTracker.Reset();
        using var box = new CarrierBox();

        CarrierClass result;
        {
            using var item = new CarrierClass(carrierTag: 314);
            result = box.Carry(item);
            AssertEqual(314, result.CarrierTag, "Carry round-trip before input release");
        } // input wrapper disposed here — its +1 released

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        // Carry returns the SAME Swift instance (`return item`); `result` holds the adopted +1.
        LifetimeTracker.AssertLiveCount(1, "result keeps the shared instance alive after input release");
        AssertEqual(314, result.CarrierTag, "CarrierTag still readable after input wrapper released");

        result.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        LifetimeTracker.AssertLiveCount(0, "instance fully released once the last owner is gone");
    }

    /// <summary>
    /// A user parameter named <c>resultPtr</c> collides with the synthetic
    /// indirect-return local; the escaped emission must still round-trip the class instance.
    /// </summary>
    public void TestRelayThrough_SyntheticParamCollision_RoundTrips()
    {
        using var box = new CarrierBox();
        using var item = new CarrierClass(carrierTag: 2718);
        using var result = box.RelayThrough(resultPtr: 1234, item: item);
        AssertEqual(2718, result.CarrierTag, "RelayThrough round-trip with a resultPtr-named user param");
    }
}
