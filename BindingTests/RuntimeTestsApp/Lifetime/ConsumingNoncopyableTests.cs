// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// A <c>~Copyable</c> value handed to a <c>consuming</c> function must have its
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
        // Consuming non-copyable param on a THROWING function: Swift owns the
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

    public void TestConsumingSelfMethodRunsDeinitExactlyOnce()
    {
        // The consuming-SELF analogue of TestConsumeRunsDeinitExactlyOnce: instead of handing the
        // value to a consuming free-function PARAMETER, we call a `consuming func` instance METHOD.
        // The generated @_cdecl wrapper must move() self out of the C# buffer (a .pointee borrow
        // cannot be consumed), so Swift runs deinit synchronously inside the call, and the generated
        // C# marks the SELF handle consumed so a later Dispose is a no-op — not a second destroy.
        LifetimeTracker.Reset();

        var resource = TestLibFunctions.CreateTrackedResource(7);
        LifetimeTracker.AssertLiveCount(1, "TrackedResource live after create (init ran once)");

        var id = resource.ConsumeSelf();
        AssertEqual(7, id, "consuming-self call returns the id");

        // A live count of 0 (not -1, not 2) proves the consuming-self move() ran deinit exactly once.
        LifetimeTracker.AssertLiveCount(0, "consuming-self call ran deinit exactly once");

        // The self handle was marked consumed by the call site; Dispose must be a no-op, NOT a second
        // value-witness destroy. A double-free would crash here or drive the live count to -1.
        resource.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after consuming-self does not double-free");

        TestLogger.Info("TrackedResource consumeSelf: deinit ran exactly once, Dispose was a no-op");
    }

    public void TestConsumingSelfThenDoubleDisposeIsSafe()
    {
        // Independent of the counters: a self-consumed handle disposed twice must never crash.
        var resource = TestLibFunctions.CreateTrackedResource(99);
        var id = resource.ConsumeSelf();
        AssertEqual(99, id, "consuming-self call returns the id");

        resource.Dispose();
        resource.Dispose();

        TestLogger.Info("Consuming-self + double-dispose did not crash");
    }

    public void TestThrowingConsumingSelfRunsDeinitExactlyOnceOnThrowPath()
    {
        // Consuming-SELF on a THROWING method: Swift owns self regardless of control flow, so its
        // deinit runs exactly once inside the call even when the method throws. The generated C#
        // wrapper marks the self handle consumed BEFORE it rethrows the Swift error — so the throw
        // path must NOT leave a second value-witness destroy pending. This is the receiver analogue
        // of TestThrowingConsumeRunsDeinitExactlyOnceOnThrowPath, the half the non-throwing
        // consuming-self test cannot reach (the highest-risk path: move() then throw).
        LifetimeTracker.Reset();

        // id = -1 drives the Swift method to throw AFTER it has taken ownership of self.
        var resource = TestLibFunctions.CreateTrackedResource(-1);
        LifetimeTracker.AssertLiveCount(1, "TrackedResource live after create");

        SwiftException? caught = null;
        try
        {
            resource.ConsumeSelfOrThrow();
        }
        catch (SwiftException ex)
        {
            caught = ex;
        }

        AssertNotNull(caught, "negative id must throw TrackedResourceError.rejected");
        AssertTrue(caught!.Message.Contains("rejected"),
            $"thrown error must survive the consuming-self+throw path, got: {caught.Message}");

        // deinit ran exactly once even though the call threw — a live count of 0 (not -1, not 2).
        LifetimeTracker.AssertLiveCount(0, "throwing consuming-self call ran deinit exactly once");

        // The self handle was marked consumed before the rethrow; Dispose must be a no-op.
        resource.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after throwing consuming-self does not double-free");

        TestLogger.Info("Throwing consuming-self: deinit ran once on the throw path, Dispose was a no-op");
    }

    public void TestThrowingConsumingSelfSuccessPathPreservesValue()
    {
        // The non-throwing branch of the same throwing consuming-self method: returns the id and
        // still consumes exactly once. Guards against the fix double-marking on the success path.
        LifetimeTracker.Reset();

        var resource = TestLibFunctions.CreateTrackedResource(55);
        LifetimeTracker.AssertLiveCount(1, "TrackedResource live after create");

        var id = resource.ConsumeSelfOrThrow();
        AssertEqual(55, id, "non-negative id returns the value without throwing");
        LifetimeTracker.AssertLiveCount(0, "success-path throwing consuming-self ran deinit exactly once");

        resource.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after success consuming-self does not double-free");

        TestLogger.Info("Throwing consuming-self success path: value preserved, deinit ran once");
    }

    public void TestUseAfterConsumingSelfThrows()
    {
        // After a `consuming` self method moves the value out, the C# object still exists (a class
        // reference — unlike Swift, which rejects post-consume use at compile time). Any further self
        // call would otherwise borrow or move from a deinitialized buffer (use-after-move → silent
        // corruption). The generated guard makes every instance method fail fast with
        // ObjectDisposedException instead. This is the receiver-side analogue of disposing a handle
        // and then touching it.
        LifetimeTracker.Reset();

        var resource = TestLibFunctions.CreateTrackedResource(7);
        var id = resource.ConsumeSelf();
        AssertEqual(7, id, "consuming-self call returns the id");
        LifetimeTracker.AssertLiveCount(0, "consuming-self ran deinit exactly once");

        // A borrowing read after consume must throw, NOT read the moved-out buffer.
        AssertThrows<ObjectDisposedException>(() => resource.GetPeek(),
            "borrowing read after consuming-self must throw ObjectDisposedException");
        // A second consume must also throw — no double move() of an already-empty buffer.
        AssertThrows<ObjectDisposedException>(() => resource.ConsumeSelf(),
            "second consuming-self must throw ObjectDisposedException");

        // The guard fired before any P/Invoke, so no further deinit ran: live count is still 0.
        LifetimeTracker.AssertLiveCount(0, "guarded reuse did not run a second deinit");

        TestLogger.Info("Use-after-consuming-self fails fast and runs no second deinit");
    }

    public void TestUseAfterThrowingConsumingSelfThrows()
    {
        // The throw path of a throwing `consuming` self method still moves self out before the throw,
        // so the receiver is consumed even though the call threw. Reuse after catching the error must
        // fail fast rather than touch the moved-out buffer.
        LifetimeTracker.Reset();

        var resource = TestLibFunctions.CreateTrackedResource(-1);
        SwiftException? caught = null;
        try
        {
            resource.ConsumeSelfOrThrow();
        }
        catch (SwiftException ex)
        {
            caught = ex;
        }
        AssertNotNull(caught, "negative id must throw TrackedResourceError.rejected");
        LifetimeTracker.AssertLiveCount(0, "throwing consuming-self ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(() => resource.GetPeek(),
            "borrowing read after a thrown consuming-self must throw ObjectDisposedException");

        LifetimeTracker.AssertLiveCount(0, "guarded reuse after throw did not run a second deinit");

        TestLogger.Info("Use-after-thrown-consuming-self fails fast and runs no second deinit");
    }

    public void TestUseAfterConsumeThrowsOnPropertyAndSubscript()
    {
        // The "already consumed" guard is emitted only once, on the instance-method wrapper path, yet
        // it must also protect property and subscript reads: those public accessors delegate to backing
        // accessor methods that route through the SAME wrapper emitter, so they inherit the guard. This
        // pins that transitive coverage — if a future refactor moved accessor emission off the method
        // path, the property/subscript reads below would stop throwing and this test would fail.
        var resource = TestLibFunctions.CreateGuardedResource(42);

        // Before consume, every read path works and sees the live value.
        AssertEqual(42, resource.CurrentId, "property getter reads the live value before consume");
        AssertEqual(43, resource[1], "subscript getter reads the live value before consume");
        AssertEqual(42, resource.GetPeek(), "borrowing method reads the live value before consume");

        var id = resource.Finish(); // consuming self — moves the value out
        AssertEqual(42, id, "consuming finish() returns the id");

        // After consume, ALL self-reads must fail fast rather than touch the moved-out buffer.
        AssertThrows<ObjectDisposedException>(() => { _ = resource.CurrentId; },
            "property getter after consume must throw ObjectDisposedException");
        AssertThrows<ObjectDisposedException>(() => { _ = resource[1]; },
            "subscript getter after consume must throw ObjectDisposedException");
        AssertThrows<ObjectDisposedException>(() => resource.GetPeek(),
            "borrowing method after consume must throw ObjectDisposedException");

        resource.Dispose();
        TestLogger.Info("Property/subscript/borrowing reads after consume all fail fast via the inherited guard");
    }
}
