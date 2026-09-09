// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Runtime coverage for the <c>@frozen ~Copyable</c> flavor that IS projected: a non-copyable
/// struct carrying a reference-bearing stored field reaches C# as a class with a real payload
/// handle, so the ownership machinery it needs — the "already consumed" preflight, the
/// <c>MarkConsumed</c> after a consuming call, a <c>Dispose()</c> that is then a no-op — all
/// exists and must work end to end.
///
/// Its payload-free sibling (<c>FrozenPlainToken</c>, same file in the Swift fixture) has no
/// runtime coverage BY CONSTRUCTION: it is refused at emission precisely because a payload-free
/// C# struct has no handle to guard, so there is nothing here to call. That half is asserted at
/// the unit layer instead, by scanning the generated corpus for the refusal marker.
///
/// The counter-based assertions construct through the C# initializer rather than the
/// <c>createFrozenLabeledToken</c> factory: the initializer adopts the buffer it allocated, so the
/// alloc/dealloc ledger reflects exactly one Swift value and the deinit-runs-exactly-once claim is
/// deterministic. The factory path is covered separately for value fidelity.
/// </summary>
public class FrozenNoncopyableProjectionTests : TestBase
{
    public FrozenNoncopyableProjectionTests(TestResults results) : base(results) { }

    public void TestBorrowDoesNotConsume()
    {
        // A `borrowing` parameter must leave the caller's value intact and usable: the generated
        // wrapper pins the payload for the call but never marks it consumed.
        LifetimeTracker.Reset();

        var token = new FrozenLabeledToken("ticket-1");
        LifetimeTracker.AssertLiveCount(1, "FrozenLabeledToken live after init");

        AssertEqual("ticket-1", TestLibFunctions.BorrowFrozenLabeledToken(token),
            "borrowing free function reads the label");
        LifetimeTracker.AssertLiveCount(1, "borrow did not consume the value");

        // Still fully usable afterwards, through every read path.
        AssertEqual("ticket-1", token.GetPeek(), "borrowing method still reads after a borrow");
        AssertEqual("ticket-1", token.Label, "property still reads after a borrow");
        AssertEqual("ticket-1", TestLibFunctions.BorrowFrozenLabeledToken(token),
            "a second borrow is fine — borrows do not accumulate ownership");
        LifetimeTracker.AssertLiveCount(1, "repeated borrows ran no deinit");

        token.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose of a never-consumed value runs deinit once");

        TestLogger.Info("FrozenLabeledToken borrow: value intact, deinit deferred to Dispose");
    }

    public void TestConsumeRunsDeinitExactlyOnce()
    {
        // The frozen analogue of the non-frozen TrackedResource consume test. Swift takes
        // ownership inside the call and runs deinit synchronously; the generated C# then marks the
        // handle consumed so Dispose is a no-op rather than a second value-witness destroy.
        LifetimeTracker.Reset();

        var token = new FrozenLabeledToken("ticket-2");
        LifetimeTracker.AssertLiveCount(1, "FrozenLabeledToken live after init");

        AssertEqual("ticket-2", TestLibFunctions.ConsumeFrozenLabeledToken(token),
            "consuming call returns the label");

        // 0, not -1 and not 2: deinit ran exactly once, inside the call.
        LifetimeTracker.AssertLiveCount(0, "consuming call ran deinit exactly once");

        token.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after consume does not double-free");
        token.Dispose();
        LifetimeTracker.AssertLiveCount(0, "second Dispose after consume is still a no-op");

        TestLogger.Info("FrozenLabeledToken consume: deinit ran exactly once, Dispose was a no-op");
    }

    public void TestUseAfterConsumeThrows()
    {
        // The consumed-preflight: after ownership left C#, every path that would touch the
        // moved-out buffer must fail fast instead. This is the whole reason the reference-bearing
        // flavor is projectable at all — there is a handle to mark, and a guard to read it.
        LifetimeTracker.Reset();

        var token = new FrozenLabeledToken("ticket-3");
        AssertEqual("ticket-3", TestLibFunctions.ConsumeFrozenLabeledToken(token),
            "consuming call returns the label");
        LifetimeTracker.AssertLiveCount(0, "consuming call ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.BorrowFrozenLabeledToken(token),
            "borrowing free function after consume must throw ObjectDisposedException");
        AssertThrows<ObjectDisposedException>(() => TestLibFunctions.ConsumeFrozenLabeledToken(token),
            "a second consume must throw ObjectDisposedException");
        AssertThrows<ObjectDisposedException>(() => token.GetPeek(),
            "borrowing method after consume must throw ObjectDisposedException");
        AssertThrows<ObjectDisposedException>(() => { _ = token.Label; },
            "property getter after consume must throw ObjectDisposedException");

        // Every guard fired before its P/Invoke, so no further deinit ran.
        LifetimeTracker.AssertLiveCount(0, "guarded reuse did not run a second deinit");

        TestLogger.Info("Use-after-consume fails fast on every read path and runs no second deinit");
    }

    public void TestConsumingSelfRedeemRunsDeinitExactlyOnce()
    {
        // The consuming-SELF half: `consuming func redeem()` moves self out, so the generated
        // wrapper marks the SELF handle consumed. Same guarantee, receiver side.
        LifetimeTracker.Reset();

        var token = new FrozenLabeledToken("ticket-4");
        LifetimeTracker.AssertLiveCount(1, "FrozenLabeledToken live after init");

        AssertEqual("ticket-4", token.GetRedeem(), "consuming-self call returns the label");
        LifetimeTracker.AssertLiveCount(0, "consuming-self call ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(() => token.GetRedeem(),
            "a second consuming-self call must throw ObjectDisposedException");
        AssertThrows<ObjectDisposedException>(() => token.GetPeek(),
            "borrowing read after consuming-self must throw ObjectDisposedException");

        token.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after consuming-self does not double-free");

        TestLogger.Info("FrozenLabeledToken redeem: consuming-self ran deinit once, reuse fails fast");
    }

    public void TestCrossTypeMemberBorrowsWithoutConsuming()
    {
        // The mirror of the pruned FrozenTokenDesk.inspect: a member on an unrelated class whose
        // signature mentions the ADMITTED flavor keeps binding, carries the same consumed
        // preflight, and borrows rather than consumes.
        LifetimeTracker.Reset();

        using var desk = new FrozenLabeledTokenDesk();
        var token = new FrozenLabeledToken("ticket-5");
        LifetimeTracker.AssertLiveCount(1, "FrozenLabeledToken live after init");

        AssertEqual("ticket-5", desk.Inspect(token), "cross-type member reads the borrowed label");
        LifetimeTracker.AssertLiveCount(1, "cross-type borrow did not consume the value");

        AssertEqual("ticket-5", TestLibFunctions.ConsumeFrozenLabeledToken(token),
            "the value is still consumable after a cross-type borrow");
        LifetimeTracker.AssertLiveCount(0, "consume after cross-type borrow ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(() => desk.Inspect(token),
            "cross-type member after consume must throw ObjectDisposedException");
        LifetimeTracker.AssertLiveCount(0, "guarded cross-type reuse did not run a second deinit");

        TestLogger.Info("FrozenLabeledTokenDesk.Inspect borrows, and refuses a consumed value");
    }

    public void TestFactoryRoundTripsTheValue()
    {
        // The returning free function: a `~Copyable` return travels back through the indirect
        // result buffer and is rehydrated into a fresh C# handle. That handoff cannot be a VWT
        // copy — a `~Copyable` type's value-witness `initializeWithCopy` slot holds
        // `__swift_cannot_copy_noncopyable_type`, an unconditional trap — so the payload is taken
        // (`InitializeWithTake`) out of the wire buffer and the buffer's value-witness destroy is
        // suppressed. The allocation ledger is what proves the move was a move: exactly one Swift
        // value exists across the handoff, and exactly one deinit ever runs.
        LifetimeTracker.Reset();

        var token = TestLibFunctions.CreateFrozenLabeledToken("ticket-6");

        AssertNotNull(token, "factory returns a token");
        // 1, not 0: the wire buffer was moved from, not destroyed — a destroy of the moved-from
        // buffer would have run deinit here and driven the count to 0 (a use-after-free below).
        LifetimeTracker.AssertLiveCount(1, "factory-returned value is live exactly once after the move");

        AssertEqual("ticket-6", token.GetPeek(), "factory-returned value reads back its label");
        AssertEqual("ticket-6", TestLibFunctions.BorrowFrozenLabeledToken(token),
            "factory-returned value can be borrowed");
        LifetimeTracker.AssertLiveCount(1, "reads through the moved-in payload consumed nothing");

        AssertEqual("ticket-6", TestLibFunctions.ConsumeFrozenLabeledToken(token),
            "factory-returned value can be consumed");
        // 0, not -1: only ONE deinit ran in total. A double-destroy (wire buffer + moved-in
        // payload) would show as a negative live count here.
        LifetimeTracker.AssertLiveCount(0, "consume of a moved-in value ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(() => token.GetPeek(),
            "factory-returned value is guarded after consume like any other");

        token.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after consume of a moved-in value is a no-op");

        TestLogger.Info("createFrozenLabeledToken moves the value across, runs one deinit, stays guarded");
    }

    public void TestFactoryValueSurvivesWithoutConsume()
    {
        // The other half of the move: if the caller never consumes, the moved-in payload is the
        // sole owner and Dispose must run the single deinit. Proves the wire-buffer teardown did
        // not already destroy it (which would make this Dispose a double-destroy).
        LifetimeTracker.Reset();

        var token = TestLibFunctions.CreateFrozenLabeledToken("ticket-7");
        LifetimeTracker.AssertLiveCount(1, "factory-returned value is live exactly once");

        AssertEqual("ticket-7", token.Label, "property reads through the moved-in payload");
        LifetimeTracker.AssertLiveCount(1, "property read consumed nothing");

        token.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose of an unconsumed moved-in value runs deinit once");
        token.Dispose();
        LifetimeTracker.AssertLiveCount(0, "second Dispose is a no-op — no double-destroy");

        TestLogger.Info("An unconsumed factory-returned value is owned by C# and deinits exactly once");
    }

    public void TestInitializerBorrowsWithoutConsuming()
    {
        // A constructor taking a `borrowing` ~Copyable parameter. A constructor is the one member
        // kind that used to be denied the @_cdecl wrapper for a non-copyable parameter, which did
        // not make it safe — it routed the init to the callee-consumes hand-over instead, where the
        // argument is handed over through its value witness. `initializeWithCopy` on a ~Copyable
        // type is `__swift_cannot_copy_noncopyable_type`, an unconditional trap. The constructor
        // now takes the same pointer-passing wrapper path an ordinary borrowing method does, so the
        // caller keeps ownership and the ledger never moves.
        LifetimeTracker.Reset();

        var token = new FrozenLabeledToken("receipt-1");
        LifetimeTracker.AssertLiveCount(1, "FrozenLabeledToken live after init");

        using (var receipt = new FrozenTokenReceipt(token, 42))
        {
            AssertEqual("receipt-1", receipt.Label, "initializer read the borrowed label");
            AssertEqual(42, receipt.Serial, "initializer carried its own scalar argument");
        }
        LifetimeTracker.AssertLiveCount(1, "borrowing initializer did not consume the token");

        // Still fully usable — a borrow is a borrow whether the callee is an init or a method.
        AssertEqual("receipt-1", token.GetPeek(), "token still readable after a borrowing initializer");
        AssertEqual("receipt-1", TestLibFunctions.ConsumeFrozenLabeledToken(token),
            "token is still consumable after a borrowing initializer");
        LifetimeTracker.AssertLiveCount(0, "the later consume ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(() => new FrozenTokenReceipt(token, 1),
            "a borrowing initializer must refuse a consumed token");
        LifetimeTracker.AssertLiveCount(0, "the guarded initializer ran no second deinit");

        TestLogger.Info("FrozenTokenReceipt init borrows the token and refuses a consumed one");
    }

    public void TestInitializerConsumesExactlyOnce()
    {
        // The `consuming` half. Swift takes ownership inside the initializer and runs deinit there,
        // exactly once; C# marks the handle consumed so Dispose is a no-op rather than a second
        // value-witness destroy. A live count of -1 here would be the double-destroy.
        LifetimeTracker.Reset();

        var token = new FrozenLabeledToken("vault-1");
        LifetimeTracker.AssertLiveCount(1, "FrozenLabeledToken live after init");

        using (var vault = new FrozenTokenVault(token))
        {
            AssertEqual("vault-1", vault.Label, "consuming initializer read the label it took");
            LifetimeTracker.AssertLiveCount(0, "consuming initializer ran deinit exactly once");
        }
        LifetimeTracker.AssertLiveCount(0, "disposing the vault ran no further token deinit");

        AssertThrows<ObjectDisposedException>(() => token.GetPeek(),
            "the token is guarded after a consuming initializer");
        AssertThrows<ObjectDisposedException>(() => new FrozenTokenVault(token),
            "a second consuming initializer must throw ObjectDisposedException");

        token.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after a consuming initializer does not double-free");

        TestLogger.Info("FrozenTokenVault init consumed the token once and left it guarded");
    }

    public void TestGenericSlotMovesTheValueWithoutTrapping()
    {
        // `<T: ~Copyable>` is the ONLY erased slot Swift lets a non-copyable value occupy (an Array
        // element, a tuple element, an `any P` existential and a plain `<T>` all carry an implicit
        // `T: Copyable`), so it is the only public shape that can drive a ~Copyable through the
        // generic marshalling path. That path builds the argument buffer with
        // `SwiftMarshal.MarshalToSwift`, which for these projections used to reach the type's
        // `initializeWithCopy` witness — `__swift_cannot_copy_noncopyable_type`, an unconditional
        // trap. It now MOVES (`initializeWithTake`) and marks the source handle consumed.
        //
        // The observable consequence is on the ledger, and it is the point of the assertions below:
        // the value leaves C# even though the Swift callee only borrows, because a move is the only
        // sound way to put a non-copyable value in a caller-owned buffer. Exactly one deinit runs
        // (the caller-side destroy of the moved-to buffer), never two, and never a trap.
        LifetimeTracker.Reset();

        var token = new FrozenLabeledToken("generic-1");
        LifetimeTracker.AssertLiveCount(1, "FrozenLabeledToken live after init");

#pragma warning disable SB0001 // method-generic direct P/Invoke; that is what this test exercises
        AssertEqual(7, TestLibFunctions.InspectNoncopyableGenerically(token),
            "the ~Copyable value reached the generic callee instead of trapping");
#pragma warning restore SB0001

        // 0, not -1 and not 1: the value moved into the argument buffer, the source handle was
        // marked consumed, and the buffer's destroy ran the single deinit.
        LifetimeTracker.AssertLiveCount(0, "the generic hand-over ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(() => token.GetPeek(),
            "the moved-out token is guarded afterwards");

        token.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after the generic hand-over is a no-op");

        TestLogger.Info("A ~Copyable through <T: ~Copyable> moves rather than copies, one deinit, no trap");
    }

    public void TestGenericSlotSecondUseFailsBeforeTheBufferExists()
    {
        // The generic argument buffer is a `stackalloc` span, and marshalling is what puts a value
        // in it. A ~Copyable value used a second time never gets that far: its own consumed-state
        // guard throws while the span still holds whatever the frame's stack happened to contain.
        // So the buffer's teardown has to know whether there is anything to tear down — destroying
        // undefined stack bytes through a value witness is undefined behavior, and for this type it
        // is a deinit over storage whose value already ran one.
        LifetimeTracker.Reset();

        var token = new FrozenLabeledToken("generic-2");
        LifetimeTracker.AssertLiveCount(1, "FrozenLabeledToken live after init");

#pragma warning disable SB0001 // method-generic direct P/Invoke; that is what this test exercises
        AssertEqual(7, TestLibFunctions.InspectNoncopyableGenerically(token),
            "the first hand-over reaches the generic callee");
        LifetimeTracker.AssertLiveCount(0, "the first hand-over ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(
            () => TestLibFunctions.InspectNoncopyableGenerically(token),
            "a second generic hand-over of a consumed token is refused");
#pragma warning restore SB0001

        // The refused call must be inert on the ledger. A -1 here is the destroy of a buffer that
        // was never initialized.
        LifetimeTracker.AssertLiveCount(0, "the refused second call ran no further deinit");

        token.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after the refused call is still a no-op");

        TestLogger.Info("A refused generic hand-over destroys nothing — the buffer was never live");
    }

    public void TestGenericSlotConsumingLeavesTheBufferToTheCallee()
    {
        // The consuming half of the same slot. C# moves the value into the argument buffer either
        // way, but a `consuming` parameter is passed `@in`: the callee owns that buffer and runs
        // the deinit, so the caller must not destroy it as well. One deinit, from the callee this
        // time rather than from the caller's teardown — a ledger of -1 would be both of them.
        LifetimeTracker.Reset();

        var token = new FrozenLabeledToken("generic-3");
        LifetimeTracker.AssertLiveCount(1, "FrozenLabeledToken live after init");

#pragma warning disable SB0001 // method-generic direct P/Invoke; that is what this test exercises
        TestLibFunctions.DiscardNoncopyableGenerically(token);
        LifetimeTracker.AssertLiveCount(0, "the consuming generic hand-over ran deinit exactly once");

        AssertThrows<ObjectDisposedException>(() => token.GetPeek(),
            "the handed-over token is guarded afterwards");
        AssertThrows<ObjectDisposedException>(
            () => TestLibFunctions.DiscardNoncopyableGenerically(token),
            "a second consuming hand-over is refused");
#pragma warning restore SB0001

        LifetimeTracker.AssertLiveCount(0, "the refused second call ran no further deinit");

        token.Dispose();
        LifetimeTracker.AssertLiveCount(0, "Dispose after a consuming hand-over is a no-op");

        TestLogger.Info("A consuming ~Copyable through <T: ~Copyable> is destroyed once, by the callee");
    }

    public void TestNestedFrozenHostBorrowsWithoutConsuming()
    {
        // The surviving half of the route refusal. Its sibling initializer takes the same token
        // `consuming` beside the same nested frozen struct, and the nested parameter is what sends
        // both of them to Swift's own symbol instead of a @_cdecl wrapper — the only place a move
        // out of the caller's buffer could happen. The consuming one is therefore refused at
        // generation and is not callable from here at all; this one asks for no hand-over, so it
        // must keep binding and must leave the caller's token intact and still consumable.
        LifetimeTracker.Reset();

        var token = new FrozenLabeledToken("nested-1");
        LifetimeTracker.AssertLiveCount(1, "FrozenLabeledToken live after init");

        var inner = new NestedOuter.InnerInfo(11);
        using (var host = new NestedFrozenTokenHost(inner, token, 31))
        {
            AssertEqual("nested-1", host.Label, "the borrowing initializer read the label");
            AssertEqual(42, host.Value, "the borrowing initializer carried its scalar arguments");
        }
        LifetimeTracker.AssertLiveCount(1, "the borrowing initializer consumed nothing");

        AssertEqual("nested-1", token.GetPeek(), "the token is still readable afterwards");
        AssertEqual("nested-1", TestLibFunctions.ConsumeFrozenLabeledToken(token),
            "the token is still consumable afterwards");
        LifetimeTracker.AssertLiveCount(0, "the later consume ran deinit exactly once");

        TestLogger.Info("The borrowing sibling of the refused initializer still binds and still borrows");
    }
}
