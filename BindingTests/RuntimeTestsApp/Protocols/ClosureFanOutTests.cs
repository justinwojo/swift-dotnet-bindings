// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
