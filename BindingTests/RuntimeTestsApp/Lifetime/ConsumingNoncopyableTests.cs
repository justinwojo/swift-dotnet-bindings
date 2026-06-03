// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// P0-06: a <c>~Copyable</c> value handed to a <c>consuming</c> function must have its
/// <c>deinit</c> run EXACTLY once — inside the Swift call — and the C# handle must then be marked
/// consumed so a later <c>Dispose()</c> is a no-op rather than a second value-witness destroy.
///
/// A non-copyable struct is address-only: the generator lowers it to an indirect (by-buffer-pointer)
/// parameter and routes it through the <c>@_cdecl</c> wrapper, which <c>move</c>s the value into the
/// Swift call. Before the fix the C# SafeHandle still ran the value-witness destroy on the
/// already-moved-from buffer at <c>Dispose()</c> — a double-free (SIGABRT) or, with the allocation
/// counters wired here, a <c>deinit</c> count of two (live count going negative). The fixture
/// <c>TrackedResource</c> feeds the same shared alloc/dealloc counters <see cref="LifetimeTracker"/>
/// reads (see <c>Lifetime/OwnershipTests.swift</c>), so the deinit-runs-exactly-once guarantee is a
/// deterministic live-count assertion — no GC, the consuming deinit is synchronous inside the call.
/// </summary>
public class ConsumingNoncopyableTests : TestBase
{
    public ConsumingNoncopyableTests(TestResults results) : base(results) { }

    public void TestConsumeRunsDeinitExactlyOnce()
    {
        LifetimeTracker.Reset();

        var resource = TestLibFunctions.CreateTrackedResource(7);
        LifetimeTracker.AssertLiveCount(1, "TrackedResource live after create (init ran once)");

        var id = TestLibFunctions.ConsumeTrackedResource(resource);
        AssertEqual(7, id, "consuming call returns the id");

        // Swift took ownership and ran deinit synchronously inside the consuming call — no GC needed.
        // A live count of 0 (not -1, not 2) proves deinit ran exactly once.
        LifetimeTracker.AssertLiveCount(0, "consuming call ran deinit exactly once");

        // The handle was marked consumed by the call site; Dispose must be a no-op, NOT a second
        // value-witness destroy. A double-free would crash here or drive the live count to -1.
        resource.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after consume does not double-free");

        TestLogger.Info("TrackedResource consume: deinit ran exactly once, Dispose was a no-op");
    }

    public void TestConsumeThenDoubleDisposeIsSafe()
    {
        // Independent of the counters: a consumed handle disposed twice must never crash.
        var resource = TestLibFunctions.CreateTrackedResource(99);
        var id = TestLibFunctions.ConsumeTrackedResource(resource);
        AssertEqual(99, id, "consuming call returns the id");

        resource.Dispose();
        resource.Dispose();

        TestLogger.Info("Consume + double-dispose did not crash");
    }

    public void TestThrowingConsumeRunsDeinitExactlyOnceOnThrowPath()
    {
        // P0-06 × throwing: a consuming non-copyable param on a THROWING function. Swift owns the
        // value regardless of control flow, so its deinit runs exactly once inside the call even
        // when the function throws. The generated C# wrapper marks the handle consumed BEFORE it
        // rethrows the Swift error — so the throw path must NOT leave a second value-witness destroy
        // pending. This is the half the non-throwing test cannot reach.
        LifetimeTracker.Reset();

        // id = -1 drives the Swift function to throw AFTER it has taken ownership.
        var resource = TestLibFunctions.CreateTrackedResource(-1);
        LifetimeTracker.AssertLiveCount(1, "TrackedResource live after create");

        SwiftException? caught = null;
        try
        {
            TestLibFunctions.ConsumeTrackedResourceOrThrow(resource);
        }
        catch (SwiftException ex)
        {
            caught = ex;
        }

        AssertNotNull(caught, "negative id must throw TrackedResourceError.rejected");
        AssertTrue(caught!.Message.Contains("rejected"),
            $"thrown error must survive the consume+throw path, got: {caught.Message}");

        // deinit ran exactly once even though the call threw — a live count of 0 (not -1, not 2).
        LifetimeTracker.AssertLiveCount(0, "throwing consuming call ran deinit exactly once");

        // The handle was marked consumed before the rethrow; Dispose must be a no-op.
        resource.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after throwing-consume does not double-free");

        TestLogger.Info("Throwing consume: deinit ran once on the throw path, Dispose was a no-op");
    }

    public void TestThrowingConsumeSuccessPathPreservesValue()
    {
        // The non-throwing branch of the same throwing function: returns the id and still consumes
        // exactly once. Guards against the fix accidentally double-marking on the success path.
        LifetimeTracker.Reset();

        var resource = TestLibFunctions.CreateTrackedResource(42);
        LifetimeTracker.AssertLiveCount(1, "TrackedResource live after create");

        var id = TestLibFunctions.ConsumeTrackedResourceOrThrow(resource);
        AssertEqual(42, id, "non-negative id returns the value without throwing");
        LifetimeTracker.AssertLiveCount(0, "success-path throwing-consume ran deinit exactly once");

        resource.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after success consume does not double-free");

        TestLogger.Info("Throwing consume success path: value preserved, deinit ran once");
    }
}
