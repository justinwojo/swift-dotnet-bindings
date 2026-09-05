// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Non-failable initializers that differ only by Swift argument label erase to ONE projected C#
/// constructor signature. Before the recovery, the first claimant emitted as the constructor and every
/// colliding sibling was dropped as DuplicateSignature — half the construction surface disappeared with
/// no diagnostic a consumer could act on.
///
/// Now the colliding members are recovered as <c>CreateWith{Labels}</c> static factories, with the plain
/// constructor slot going to the fully positional member when — and only when — exactly one member of the
/// family is fully positional. Each Swift init writes a distinct <c>dispatchMarker</c>, so these
/// assertions prove not just that the members exist but that each generated entry point reaches the
/// Swift initializer it was named for. A member that was still dropped would make this file fail to
/// compile, so reachability is covered by the compile gate and routing by the assertions below.
/// </summary>
public class NonFailableInitLabelCollisionTests : TestBase
{
    public NonFailableInitLabelCollisionTests(TestResults results) : base(results) { }

    public void TestThreeWayCollisionRecoversEveryInitializer()
    {
        // No member of this family is fully positional, so nobody owns the bare constructor slot and all
        // three initializers emit as factories.
        var payment = LabeledSessionHandle.CreateWithPaymentTokenRetries("pi_1", 3);
        var setup = LabeledSessionHandle.CreateWithSetupTokenRetries("seti_2", 4);
        var customer = LabeledSessionHandle.CreateWithCustomerTokenRetries("cus_3", 5);

        AssertEqual("payment:pi_1:3", payment.DispatchMarker, "paymentToken: factory reaches the paymentToken: init body");
        AssertEqual("setup:seti_2:4", setup.DispatchMarker, "setupToken: factory reaches the setupToken: init body");
        AssertEqual("customer:cus_3:5", customer.DispatchMarker, "customerToken: factory reaches the customerToken: init body");

        payment.Dispose();
        setup.Dispose();
        customer.Dispose();
    }

    public void TestThreeWayCollisionFactoriesAreIndependentInstances()
    {
        var a = LabeledSessionHandle.CreateWithPaymentTokenRetries("a", 1);
        var b = LabeledSessionHandle.CreateWithSetupTokenRetries("a", 1);

        // Identical arguments, different Swift initializers: the markers must still differ, which is only
        // possible if each factory is bound to its own native entry point.
        AssertTrue(a.DispatchMarker != b.DispatchMarker, "same arguments through two factories reach two different Swift inits");

        a.Dispose();
        b.Dispose();
    }

    public void TestPositionalInitializerKeepsThePlainConstructorOnAClass()
    {
        // The labeled init is declared FIRST in Swift; ownership of the constructor slot still goes to the
        // positional one, so a re-ordered interface cannot re-point this call at the other initializer.
        var positional = new LabeledEndpointHandle("wss://example");
        AssertEqual("raw:wss://example", positional.DispatchMarker, "the plain constructor reaches the unlabeled init body");

        var labeled = LabeledEndpointHandle.CreateWithOpaqueToken("wss://example");
        AssertEqual("opaque:wss://example", labeled.DispatchMarker, "the recovered factory reaches the opaqueToken: init body");

        positional.Dispose();
        labeled.Dispose();
    }

    public void TestPositionalInitializerKeepsThePlainConstructorOnAFrozenStruct()
    {
        // Same policy on a value type, whose constructor terminal is a returned value rather than an
        // adopted class handle.
        var positional = new LabeledPortDescriptor(8080);
        AssertEqual(0, positional.DispatchMarker, "the plain constructor reaches the unlabeled init body");
        AssertEqual(8080, positional.Value, "the plain constructor forwards its argument");

        var tcp = LabeledPortDescriptor.CreateWithTcpPort(443);
        AssertEqual(1, tcp.DispatchMarker, "the tcpPort: factory reaches the tcpPort: init body");
        AssertEqual(443, tcp.Value, "the tcpPort: factory forwards its argument");

        var udp = LabeledPortDescriptor.CreateWithUdpPort(53);
        AssertEqual(2, udp.DispatchMarker, "the udpPort: factory reaches the udpPort: init body");
        AssertEqual(53, udp.Value, "the udpPort: factory forwards its argument");
    }

    public void TestRecoveredStructFactoriesRoundTripIndependently()
    {
        // Identical arguments through the two struct factories must still land in different init bodies.
        var tcp = LabeledPortDescriptor.CreateWithTcpPort(9000);
        var udp = LabeledPortDescriptor.CreateWithUdpPort(9000);

        AssertTrue(tcp.DispatchMarker != udp.DispatchMarker, "same argument through two struct factories reaches two different Swift inits");
        AssertEqual(tcp.Value, udp.Value, "both struct factories forward the same argument");
    }
}
