// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

public class NoncopyableArgumentTests : TestBase
{
    public NoncopyableArgumentTests(TestResults results) : base(results) { }

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_ResetNoncopyableHandoff")]
    private static extern void ResetHandoff();
    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_WaitNoncopyableHandoff")]
    private static extern int WaitHandoff();
    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_ReleaseNoncopyableHandoff")]
    private static extern void ReleaseHandoff();

    public void TestConsumedArgumentRejectsConsumeAndBorrow()
    {
        LifetimeTracker.Reset();
        using var resource = TestLibFunctions.CreateTrackedResource(31);
        AssertEqual(31, TestLibFunctions.BorrowTrackedResource(resource), "healthy borrowed argument");
        AssertEqual(0, LifetimeTracker.GetStats().deallocations, "borrow preserves ownership");
        AssertEqual(31, TestLibFunctions.ConsumeTrackedResource(resource), "healthy consumption");
        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.ConsumeTrackedResource(resource));
        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.BorrowTrackedResource(resource));
        resource.Dispose();
        AssertEqual(1, LifetimeTracker.GetStats().deallocations, "rejected reuse and Dispose never destroy moved bytes");
    }

    public void TestThrownConsumptionRejectsConsumeAndBorrow()
    {
        LifetimeTracker.Reset();
        using var resource = TestLibFunctions.CreateTrackedResource(-1);
        AssertThrows<SwiftException>(() => TestLibFunctions.ConsumeTrackedResourceOrThrow(resource));
        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.ConsumeTrackedResource(resource));
        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.BorrowTrackedResource(resource));
        resource.Dispose();
        AssertEqual(1, LifetimeTracker.GetStats().deallocations, "throwing native consumption destroys once");
    }

    public void TestDisposedLaterArgumentPreservesEarlierOwnership()
    {
        LifetimeTracker.Reset();
        using var resource = TestLibFunctions.CreateTrackedResource(20);
        using var witness = TestLibFunctions.CreateTrackedResource(2);
        witness.Dispose();
        int before = LifetimeTracker.GetStats().deallocations;
        AssertEqual(1, before, "only witness destroyed before call");
        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.ConsumeTrackedResourceWithWitness(resource, witness));
        AssertFalse(resource.Payload.IsConsumed, "pre-entry argument failure must not consume first argument");
        AssertEqual(20, TestLibFunctions.BorrowTrackedResource(resource), "first value remains readable");
        AssertEqual(before, LifetimeTracker.GetStats().deallocations, "pre-entry unwind preserves first value");
        resource.Dispose();
        AssertEqual(before + 1, LifetimeTracker.GetStats().deallocations, "first value still owns normal destruction");
    }

    public void TestDisposeDuringConsumingArgumentHandoff() => AssertDisposeDuringHandoff(false);

    public void TestDisposeDuringThrowingArgumentHandoff() => AssertDisposeDuringHandoff(true);

    private void AssertDisposeDuringHandoff(bool throws)
    {
        LifetimeTracker.Reset();
        ResetHandoff();
        using var resource = TestLibFunctions.CreateTrackedResource(throws ? -1 : 44);
        var payload = resource.Payload;
        var invocation = Task.Run(() =>
        {
            if (throws)
                AssertThrows<SwiftException>(() => TestLibFunctions.ConsumeTrackedResourceBlockedOrThrow(resource));
            else
                AssertEqual(44, TestLibFunctions.ConsumeTrackedResourceBlocked(resource), "blocked consume returns original id");
        });
        try
        {
            AssertEqual(1, WaitHandoff(), "native entry must precede racing Dispose");
            resource.Dispose();
            AssertEqual(0, LifetimeTracker.GetStats().deallocations, "lease prevents destruction while Swift owns live moved value");
        }
        finally
        {
            ReleaseHandoff();
            invocation.GetAwaiter().GetResult();
        }
        AssertTrue(payload.IsConsumed, "captured payload must be marked even after concurrent Dispose");
        AssertTrue(payload.IsClosed, "pending Dispose completes after the outer lease exits");
        AssertEqual(1, LifetimeTracker.GetStats().deallocations, "native move plus pending Dispose destroys exactly once");
    }
}
