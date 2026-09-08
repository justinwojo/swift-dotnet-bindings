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

    public void TestFactoryReturnedValueSurvivesWithoutConsume()
    {
        // The non-frozen lane's half of the returning-factory story. A `~Copyable` return travels
        // back through the indirect result buffer; this projection ADOPTS that buffer rather than
        // constructing a second value from it, so the wire buffer must be neither value-witness
        // destroyed nor freed underneath the SafeHandle that now owns it. If the return path
        // destroyed the buffer, the live count would already be 0 on the next line and every read
        // below would be a use-after-free; if it freed the storage, Dispose would double-free.
        LifetimeTracker.Reset();

        var resource = TestLibFunctions.CreateTrackedResource(21);
        LifetimeTracker.AssertLiveCount(1, "factory-returned TrackedResource is live exactly once");

        AssertEqual(21, resource.GetPeek(), "factory-returned value reads back its id");
        AssertEqual(21, TestLibFunctions.BorrowTrackedResource(resource),
            "factory-returned value can be borrowed");
        LifetimeTracker.AssertLiveCount(1, "reads through the adopted payload consumed nothing");

        resource.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose of an unconsumed adopted value runs deinit once");
        resource.Dispose();
        LifetimeTracker.AssertLiveCount(0, "second Dispose is a no-op — no double-destroy");

        TestLogger.Info("An unconsumed factory-returned TrackedResource is owned by C# and deinits exactly once");
    }

    public void TestFactoryReturnedValueRoundTripsThroughConsume()
    {
        // Same handoff, consumed rather than disposed: exactly one deinit in total across the
        // return path and the consuming call. A live count of -1 here would mean the wire buffer
        // and the wrapper each ran a deinit.
        LifetimeTracker.Reset();

        var resource = TestLibFunctions.CreateTrackedResource(22);
        LifetimeTracker.AssertLiveCount(1, "factory-returned TrackedResource is live exactly once");

        AssertEqual(22, TestLibFunctions.ConsumeTrackedResource(resource),
            "factory-returned value can be consumed");
        LifetimeTracker.AssertLiveCount(0, "consume of a factory-returned value ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(() => resource.GetPeek(),
            "factory-returned value is guarded after consume like any other");

        resource.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after consume of a factory-returned value is a no-op");

        TestLogger.Info("createTrackedResource hands ownership across, runs one deinit, stays guarded");
    }

    public void TestInitializerBorrowsWithoutConsuming()
    {
        // The non-frozen lane's half of the constructor story. A constructor taking a ~Copyable
        // parameter used to be denied the @_cdecl wrapper, which routed it to the callee-consumes
        // hand-over — a value-witness copy of the argument, and for a non-copyable type that copy
        // witness is `__swift_cannot_copy_noncopyable_type`, an unconditional trap. The constructor
        // now takes the same pointer-passing wrapper path an ordinary borrowing method does.
        LifetimeTracker.Reset();

        var resource = TestLibFunctions.CreateTrackedResource(31);
        LifetimeTracker.AssertLiveCount(1, "TrackedResource live after create");

        using (var receipt = new TrackedResourceReceipt(resource, 4))
        {
            AssertEqual(35, receipt.Peeked, "initializer read the borrowed resource and added its own argument");
        }
        LifetimeTracker.AssertLiveCount(1, "borrowing initializer did not consume the resource");

        AssertEqual(31, resource.GetPeek(), "resource still readable after a borrowing initializer");
        AssertEqual(31, TestLibFunctions.ConsumeTrackedResource(resource),
            "resource is still consumable after a borrowing initializer");
        LifetimeTracker.AssertLiveCount(0, "the later consume ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(() => new TrackedResourceReceipt(resource, 1),
            "a borrowing initializer must refuse a consumed resource");
        LifetimeTracker.AssertLiveCount(0, "the guarded initializer ran no second deinit");

        TestLogger.Info("TrackedResourceReceipt init borrows the resource and refuses a consumed one");
    }

    public void TestInitializerConsumesExactlyOnce()
    {
        // The `consuming` half on the opaque-payload lane: Swift runs deinit inside the initializer
        // and C# marks the handle consumed, so Dispose is a no-op. A live count of -1 would be the
        // double-destroy this whole guard exists to prevent.
        LifetimeTracker.Reset();

        var resource = TestLibFunctions.CreateTrackedResource(32);
        LifetimeTracker.AssertLiveCount(1, "TrackedResource live after create");

        using (var vault = new TrackedResourceVault(resource))
        {
            AssertEqual(32, vault.Peeked, "consuming initializer read the resource it took");
            LifetimeTracker.AssertLiveCount(0, "consuming initializer ran deinit exactly once");
        }
        LifetimeTracker.AssertLiveCount(0, "disposing the vault ran no further resource deinit");

        AssertThrows<ObjectDisposedException>(() => resource.GetPeek(),
            "the resource is guarded after a consuming initializer");
        AssertThrows<ObjectDisposedException>(() => new TrackedResourceVault(resource),
            "a second consuming initializer must throw ObjectDisposedException");

        resource.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after a consuming initializer does not double-free");

        TestLogger.Info("TrackedResourceVault init consumed the resource once and left it guarded");
    }

    public void TestGenericSlotMovesTheValueWithoutTrapping()
    {
        // `<T: ~Copyable>` is the only erased slot Swift permits a non-copyable value in, and it
        // drives the generic marshalling path, whose argument buffer is built by
        // `SwiftMarshal.MarshalToSwift`. For this projection that used to reach the type's
        // `initializeWithCopy` witness — an unconditional trap for a ~Copyable type. It now MOVES
        // (`initializeWithTake`) and marks the source handle consumed, so the value legitimately
        // leaves C# (a move is the only sound way to place a non-copyable value in a caller-owned
        // buffer) and exactly one deinit runs, from the caller-side destroy of that buffer.
        LifetimeTracker.Reset();

        var resource = TestLibFunctions.CreateTrackedResource(33);
        LifetimeTracker.AssertLiveCount(1, "TrackedResource live after create");

#pragma warning disable SB0001 // method-generic direct P/Invoke; that is what this test exercises
        AssertEqual(7, TestLibFunctions.InspectNoncopyableGenerically(resource),
            "the ~Copyable value reached the generic callee instead of trapping");
#pragma warning restore SB0001

        LifetimeTracker.AssertLiveCount(0, "the generic hand-over ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(() => resource.GetPeek(),
            "the moved-out resource is guarded afterwards");

        resource.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after the generic hand-over is a no-op");

        TestLogger.Info("A non-frozen ~Copyable through <T: ~Copyable> moves rather than copies, one deinit, no trap");
    }
}
