// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end ABI gate for a class-superclass-constrained Swift protocol's <em>read-only</em>
/// proxy path (RC-PROXY Failure B). The Swift fixture roots <c>EntityRootedProbe</c> on a
/// pure-Swift <c>Entity</c> stand-in (NOT <c>RealityFoundation.Entity</c>), so the generator
/// classifies it as an ordinary class-superclass requirement and routes it through the read-only
/// proxy path rather than the <c>EveryEntityProtocol</c> carrier (which the generator unit tests
/// cover for the real-<c>Entity</c> case). The three proxy directions:
/// <list type="bullet">
///   <item>RETURN   — Swift vends <c>any EntityRootedProbe</c>; C# materialises a proxy. (supported)</item>
///   <item>ACCEPT   — C# passes a Swift-vended proxy back into Swift. (supported)</item>
///   <item>CALLBACK — C# <em>implements</em> the protocol and passes it back to Swift. UNSUPPORTED:
///                    the wrapper exports no <c>Get_EveryProtocol_{P}_WitnessTable</c> getter for a
///                    skipped class-superclass conformance, so the generated proxy fails clean with
///                    <see cref="System.NotSupportedException"/> at the C#→Swift boundary rather than
///                    surfacing an <c>EntryPointNotFoundException</c> for the missing symbol.</item>
/// </list>
/// </summary>
public class EntityRootedExistentialTests : TestBase
{
    public EntityRootedExistentialTests(TestResults results) : base(results) { }

    #region RETURN — Swift vends `any EntityRootedProbe`; C# materialises a carrier-backed proxy

    public void TestMakeProbeReturnReadsMarkerAndPing()
    {
        var vendor = new EntityRootedProbeVendor();
        var probe = vendor.MakeProbe("alpha");
        AssertEqual("alpha", probe.Marker, "returned probe marker");
        AssertEqual("swift:alpha:call", probe.Ping("call"), "returned probe ping");
        TestLogger.Info("Entity-rooted existential RETURN materialised a carrier-backed proxy");
    }

    public void TestMakeProbeIfPresentReturnsProxy()
    {
        var vendor = new EntityRootedProbeVendor();
        var probe = vendor.MakeProbeIf(true, "beta");
        AssertNotNull(probe, "optional probe (present)");
        AssertEqual("beta", probe!.Marker, "optional probe marker");
        AssertEqual("swift:beta:ping", probe.Ping("ping"), "optional probe ping");
    }

    public void TestMakeProbeIfAbsentReturnsNull()
    {
        var vendor = new EntityRootedProbeVendor();
        var probe = vendor.MakeProbeIf(false, "gamma");
        AssertNull(probe, "optional probe (absent)");
    }

    #endregion

    #region ACCEPT — C# passes a Swift-vended proxy back into Swift

    public void TestDescribeAcceptsSwiftVendedProxy()
    {
        var vendor = new EntityRootedProbeVendor();
        var probe = vendor.MakeProbe("alpha");
        var described = vendor.Describe(probe).ToString();
        AssertEqual("alpha#swift:alpha:call", described, "describe(swift-vended)");
    }

    public void TestEchoRoundTripsSwiftVendedProxy()
    {
        var vendor = new EntityRootedProbeVendor();
        var probe = vendor.MakeProbe("delta");
        var echoed = vendor.Echo(probe);
        AssertEqual("delta", echoed.Marker, "echo(swift-vended) marker");
        AssertEqual("swift:delta:x", echoed.Ping("x"), "echo(swift-vended) ping");
    }

    #endregion

    #region CALLBACK — C#-implemented Entity-rooted protocol back into Swift is UNSUPPORTED (fail-clean boundary)

    // Implementing a class-superclass-constrained protocol in C# and passing it back to Swift is an
    // unsupported direction here: the synthesized EveryProtocol helper cannot subclass the required
    // class, so its EveryProtocol conformance (and the Get_EveryProtocol_{P}_WitnessTable getter) is
    // never emitted. The generated proxy fails clean with NotSupportedException at the C#→Swift
    // boundary (the vendor.Describe(...) call) instead of surfacing a raw EntryPointNotFoundException
    // for the missing wrapper symbol.
    public void TestDescribeCSharpImplementationThrowsNotSupported()
    {
        var vendor = new EntityRootedProbeVendor();
        var impl = new CSharpProbe("cs-marker");
        AssertThrows<NotSupportedException>(
            () => { _ = vendor.Describe(impl); },
            "Describing a C#-implemented Entity-rooted protocol must throw NotSupportedException at the C#→Swift boundary");
        TestLogger.Info("C#-implemented Entity-rooted CALLBACK failed clean with NotSupportedException (Describe)");
    }

    // Same unsupported boundary through `Echo`: passing the C# implementation back into Swift must
    // throw NotSupportedException rather than EntryPointNotFoundException (no witness-table getter
    // exists for the skipped class-superclass conformance).
    public void TestEchoCSharpImplementationThrowsNotSupported()
    {
        var vendor = new EntityRootedProbeVendor();
        var impl = new CSharpProbe("cs-roundtrip");
        AssertThrows<NotSupportedException>(
            () => { _ = vendor.Echo(impl); },
            "Echoing a C#-implemented Entity-rooted protocol must throw NotSupportedException at the C#→Swift boundary");
        TestLogger.Info("C#-implemented Entity-rooted CALLBACK failed clean with NotSupportedException (Echo)");
    }

    #endregion

    /// <summary>Pure C# conformer driving the C#→Swift callback direction.</summary>
    private sealed class CSharpProbe : IEntityRootedProbe
    {
        private readonly string _marker;

        public CSharpProbe(string marker) => _marker = marker;

        public string Marker => _marker;

        public string Ping(string value) => $"cs:{_marker}:{value}";
    }
}
