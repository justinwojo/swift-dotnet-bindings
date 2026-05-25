// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Asserts the foundational ARC boundary invariants for class round-trips —
/// Ownership rules #1 (class inputs are borrowed: <c>takeUnretainedValue()</c>)
/// and #2 (class outputs are owned: <c>passRetained()</c>). Passing a Swift
/// class instance C# -> Swift -> C# must leave the underlying Swift refcount at
/// a predictable, balanced value, and the instance must deinit once every C#
/// wrapper is disposed. A regression to <c>takeRetainedValue()</c> on the input
/// (consuming the caller's reference) would drift these counts — and nothing
/// else in the suite probes the call-boundary retain balance directly
/// (<c>BulkCollectionStressTests</c> exercises the manual <c>Arc.RetainMultiple</c>
/// path, not the generated wrapper's borrow-in / owned-out behaviour).
///
/// Probes <c>swift_retainCount</c> via <see cref="Arc.RetainCount"/>, so it is
/// independent of GC/finalization and runs on both Mono (simulator) and
/// NativeAOT (device). The per-pointer refcount checks are the load-bearing
/// assertions — they are unaffected by tracked objects from other tests.
/// </summary>
public class ArcRoundTripTests : TestBase
{
    public ArcRoundTripTests(TestResults results) : base(results) { }

    private static IntPtr HandleOf(TrackedObject o) => ((ISwiftObject)o).SwiftHandle;

    private static void DrainFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>
    /// <c>identity(obj)</c> returns the SAME Swift instance. Rule #1 borrows the
    /// input (no net retain inbound); rule #2 returns it owned (+1 for the new
    /// wrapper). So after the round-trip the Swift refcount is 2 (original
    /// wrapper + returned wrapper); it drops to 1 when the returned wrapper is
    /// disposed, and the instance deinits (live == 0) once the original is too.
    /// </summary>
    public void TestClassRoundTripRetainBalance()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var obj = TestLibFunctions.CreateTrackedObject(1);
        IntPtr ptr = HandleOf(obj);

        // Fresh passRetained return: exactly one owning reference.
        AssertEqual<long>(1, Arc.RetainCount(ptr),
            "fresh CreateTrackedObject refcount should be 1");

        // Round-trip through identity(): borrow-in + owned-out => refcount 2.
        var returned = TestLibFunctions.Identity(obj);
        AssertEqual(ptr, HandleOf(returned),
            "identity() must return the same Swift instance");
        AssertEqual<long>(2, Arc.RetainCount(ptr),
            "after identity round-trip both wrappers own the instance (rc==2); "
            + "rc==1 would mean the input was consumed — a takeRetainedValue regression on the borrow");

        // Disposing the returned wrapper releases its +1.
        returned.Dispose();
        AssertEqual<long>(1, Arc.RetainCount(ptr),
            "after disposing the returned wrapper refcount should drop back to 1");

        // Disposing the last wrapper drives refcount to 0 => Swift deinit.
        // ptr is freed past this point — verify via the deinit counter, not RetainCount.
        obj.Dispose();
        LifetimeTracker.AssertNoLeaks("after disposing both wrappers");

        TestLogger.Info("Class round-trip ARC balance: borrow-in + owned-out balanced, instance deinit'd");
    }

    /// <summary>
    /// Borrowing an instance — instance-method self, or repeated <c>identity()</c>
    /// round-trips whose results are disposed — must not accumulate retains on
    /// the underlying Swift object. After N borrow cycles the refcount returns
    /// to its starting value of 1.
    /// </summary>
    public void TestClassBorrowDoesNotAccumulateRetains()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var obj = TestLibFunctions.CreateTrackedObject(2);
        IntPtr ptr = HandleOf(obj);
        AssertEqual<long>(1, Arc.RetainCount(ptr), "fresh refcount should be 1");

        // Instance-method self is borrowed: calling it must not net-retain.
        for (int i = 0; i < 100; i++)
            AssertTrue(obj.IsAlive(), "IsAlive() borrow call should return true");
        AssertEqual<long>(1, Arc.RetainCount(ptr),
            "100 instance-method borrows must not accumulate retains (rc still 1)");

        // Each identity() round-trip adds +1; the matching Dispose() releases it.
        // Net change across the loop must be zero.
        for (int i = 0; i < 100; i++)
        {
            var tmp = TestLibFunctions.Identity(obj);
            tmp.Dispose();
        }
        AssertEqual<long>(1, Arc.RetainCount(ptr),
            "100 balanced identity()+Dispose() cycles must return refcount to 1");

        obj.Dispose();
        LifetimeTracker.AssertNoLeaks("after borrow-cycle test");

        TestLogger.Info("Class borrow: 100 method borrows + 100 identity round-trips left refcount balanced");
    }
}
