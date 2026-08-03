// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Threading;
using System.Threading.Tasks;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// ABI Coverage Grid — closure corner: the same-signature CLOSURE-PARAM method fan-out Latent.
///
/// Two class-bound protocols (ClosureFanOwner, ClosureFanPeer) declare the same method whose
/// signature contains a dispatchable closure param (<c>applyFactory(_: @escaping () -> Int32)</c>).
/// ClosureFanOwner is the vtable owner (lexically smaller); ClosureFanPeer gets the empty stitched
/// extension. A C# class implementing ONLY the peer is dispatched through the peer existential.
///
/// The sibling-method fan-out for plain + async/sync-effect signatures is already fixed
/// (<see cref="SiblingMethodDispatchTests"/>). The closure-param signature routes through the
/// EveryProtocol closure-method emitter, which (pre-fix) does not receive the owner/sibling fan-out
/// plan — so the owner body force-unwraps its own nil vtable when a peer-only impl is dispatched.
/// This test reaches that leg with a value-round-trip oracle: the Swift driver passes a closure
/// that records <c>value</c> when the C# impl invokes the factory.
/// </summary>
public class ClosureFanOutTests : TestBase
{
    public ClosureFanOutTests(TestResults results) : base(results) { }

    /// <summary>
    /// The unreached leg: dispatch the shared closure-param method through the PEER (non-owner)
    /// existential. Must route to the peer-only C# impl, which invokes the Swift factory closure.
    /// </summary>
    public void TestClosureFan_PeerExistential()
    {
        var impl = new ClosureFanPeerOnlyImpl();
        var result = Functions.CallApplyFactoryViaPeer(impl, 42);
        AssertEqual(42, result,
            "closure-param fan-out via Peer existential must reach the C# impl and invoke the factory");
        AssertEqual(42, impl.LastFactoryValue, "the C# peer impl must have invoked the factory closure");
    }

    /// <summary>
    /// Control: dispatch the shared closure-param method through the OWNER existential. The owner
    /// body reads its own populated vtable, so this works regardless of the fan-out fix.
    /// </summary>
    public void TestClosureFan_OwnerExistential()
    {
        var impl = new ClosureFanOwnerOnlyImpl();
        var result = Functions.CallApplyFactoryViaOwner(impl, 99);
        AssertEqual(99, result,
            "closure-param dispatch via Owner existential round-trips the factory value");
        AssertEqual(99, impl.LastFactoryValue, "the C# owner impl must have invoked the factory closure");
    }

    /// <summary>
    /// The closure-RETURN unreached leg: dispatch the shared closure-RETURNING method through the
    /// PEER (non-owner) existential. Must route to the peer-only C# impl, obtain the returned
    /// closure (an Action), and invoke it. Pre-fix the returning emitter force-unwraps the owner's
    /// nil vtable. The oracle is C#-side: the returned Action records the value when Swift invokes it.
    /// </summary>
    public void TestClosureRetFan_PeerExistential()
    {
        var impl = new ClosureRetFanPeerOnlyImpl(42);
        Functions.CallMakeNotifierViaPeer(impl);
        AssertEqual(42, impl.Recorded,
            "closure-return fan-out via Peer existential must reach the C# impl and invoke the returned closure");
    }

    /// <summary>
    /// Control: dispatch the shared closure-RETURNING method through the OWNER existential. The
    /// owner body reads its own populated vtable, so this works regardless of the fan-out fix.
    /// </summary>
    public void TestClosureRetFan_OwnerExistential()
    {
        var impl = new ClosureRetFanOwnerOnlyImpl(99);
        Functions.CallMakeNotifierViaOwner(impl);
        AssertEqual(99, impl.Recorded,
            "closure-return dispatch via Owner existential round-trips the returned closure's value");
    }

    /// <summary>
    /// ABI Coverage Grid — generics×closure corner: the ASYNC closure-param method fan-out leg,
    /// folded in once the sync closure-param fan-out fix landed. AsyncClosureFanOwner /
    /// AsyncClosureFanPeer both declare <c>applyFactory(_: @escaping () -> Int32) async</c>. Because
    /// the EveryProtocol owner/peer grouping is async-INSENSITIVE (it dedups on the emitted Swift
    /// witness, which drops <c>async</c>), these two async protocols share a fan-out group with the
    /// SYNC ClosureFan{Owner,Peer} pair declaring the same selector. The four-protocol group's
    /// lexically-first owner is AsyncClosureFanOwner, so the non-dispatchable fatalError stub must be
    /// emitted SYNC (a sync candidate satisfies an async requirement) or the sync siblings' empty
    /// extensions fail to conform — the bug this fixture surfaced and the generator fix closes.
    ///
    /// The unreached leg: dispatch the shared async closure-param method through the PEER (non-owner)
    /// existential. Must route to the peer-only C# impl, which invokes the Swift factory closure.
    ///
    /// COMPILE-GATED ONLY (by-design-gray). Owner selection hands the body to the SYNC, non-throwing
    /// sibling (ClosureFanOwner) so the four-protocol group conforms and the sync legs dispatch
    /// correctly. The shared sync witness reads the SYNC per-protocol vtables; an async proxy
    /// registers an async receiver thunk whose result is a Task, so reached through the sync
    /// @convention(c) pointer it returns garbage (the exact hazard the sibling-ordering rationale in
    /// EveryProtocolEmitter.ComputeMethodEmissionPlans documents). Async closure-param REVERSE
    /// dispatch is not runtime-supported; this fixture exists to gate that the binding COMPILES (the
    /// async conformance + @_cdecl wrappers emit) without regressing the sync fan-out. Running it
    /// would force-unwrap a nil sync vtable and crash, blind-skipping the whole class — hence Skip.
    /// </summary>
    [Skip("async closure-param reverse dispatch is compile-gated only — the shared sync witness reads sync vtables; by-design-gray ABI grid cell")]
    public async Task TestAsyncClosureFan_PeerExistential()
    {
        var impl = new AsyncClosureFanPeerOnlyImpl();
        var result = await WithTimeout(
            Functions.CallApplyFactoryAsyncViaPeerAsync(impl, 42), DefaultAsyncTimeout);
        AssertEqual(42, result,
            "async closure-param fan-out via Peer existential must reach the C# impl and invoke the factory");
        AssertEqual(42, impl.LastFactoryValue, "the C# async peer impl must have invoked the factory closure");
    }

    /// <summary>
    /// Control: dispatch the shared async closure-param method through the OWNER existential.
    /// COMPILE-GATED ONLY (by-design-gray) — same rationale as TestAsyncClosureFan_PeerExistential:
    /// the shared sync witness cannot route an async receiver thunk's Task result through the sync
    /// @convention(c) pointer. Async closure-param reverse dispatch is not runtime-supported.
    /// </summary>
    [Skip("async closure-param reverse dispatch is compile-gated only — the shared sync witness reads sync vtables; by-design-gray ABI grid cell")]
    public async Task TestAsyncClosureFan_OwnerExistential()
    {
        var impl = new AsyncClosureFanOwnerOnlyImpl();
        var result = await WithTimeout(
            Functions.CallApplyFactoryAsyncViaOwnerAsync(impl, 99), DefaultAsyncTimeout);
        AssertEqual(99, result,
            "async closure-param dispatch via Owner existential round-trips the factory value");
        AssertEqual(99, impl.LastFactoryValue, "the C# async owner impl must have invoked the factory closure");
    }
}

internal class ClosureFanPeerOnlyImpl : IClosureFanPeer
{
    public int LastFactoryValue = -1;
    public void ApplyFactory(Func<int> factory) => LastFactoryValue = factory();
}

internal class ClosureFanOwnerOnlyImpl : IClosureFanOwner
{
    public int LastFactoryValue = -1;
    public void ApplyFactory(Func<int> factory) => LastFactoryValue = factory();
}

internal class ClosureRetFanPeerOnlyImpl : IClosureRetFanPeer
{
    public int Recorded = -1;
    private readonly int _value;
    public ClosureRetFanPeerOnlyImpl(int value) => _value = value;
    public Action MakeNotifier() => () => Recorded = _value;
}

internal class ClosureRetFanOwnerOnlyImpl : IClosureRetFanOwner
{
    public int Recorded = -1;
    private readonly int _value;
    public ClosureRetFanOwnerOnlyImpl(int value) => _value = value;
    public Action MakeNotifier() => () => Recorded = _value;
}

// The two async impls below are the only conformers in this file that the binding marks as never
// reverse-dispatched, and the marker is telling the truth: the tests driving them are [Skip]ped for
// exactly that reason — the shared sync witness cannot route an async receiver thunk's Task through a
// sync @convention(c) pointer, so no vtable slot is ever filled for these protocols. They stay as
// compile-only coverage of the shape, so the warning is acknowledged here rather than heeded.
#pragma warning disable SB0010 // protocol is never reverse-dispatched; compile-only coverage on purpose

internal class AsyncClosureFanPeerOnlyImpl : IAsyncClosureFanPeer
{
    public int LastFactoryValue = -1;
    public Task ApplyFactoryAsync(Func<int> factory, CancellationToken cancellationToken = default)
    {
        LastFactoryValue = factory();
        return Task.CompletedTask;
    }
}

internal class AsyncClosureFanOwnerOnlyImpl : IAsyncClosureFanOwner
{
    public int LastFactoryValue = -1;
    public Task ApplyFactoryAsync(Func<int> factory, CancellationToken cancellationToken = default)
    {
        LastFactoryValue = factory();
        return Task.CompletedTask;
    }
}

#pragma warning restore SB0010
