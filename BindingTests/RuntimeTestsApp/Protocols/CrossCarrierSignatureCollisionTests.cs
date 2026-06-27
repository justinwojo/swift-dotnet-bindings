// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// End-to-end gate for the cross-carrier same-signature partitioning fix.
///
/// A plain Swift protocol (<c>GreetingProviderPlain</c>) and an
/// @objc/NSObjectProtocol protocol (<c>GreetingProviderObjC</c>) declare the
/// SAME <c>makeGreeting(for:)</c> signature. The plain protocol's umbrella
/// conformance routes through the <c>EveryProtocol</c> carrier class; the @objc
/// one routes through <c>EveryObjCProtocol</c>. These are distinct concrete Swift
/// types, so the witness body emitted on one carrier cannot satisfy the
/// requirement on the other.
///
/// Pre-fix the emitter deduplicated the two requirements by signature alone, emitted
/// the witness body on a single owner carrier, and left the sibling carrier's
/// conformance an empty extension — which failed to type-check
/// (<c>type 'EveryObjCProtocol' does not conform to protocol '...'</c>), breaking
/// wrapper compilation for any library carrying this shape. The fix partitions the
/// emission plans by carrier so each carrier owns and emits its own witness.
///
/// Reaching these call sites already proves the wrapper compiled (the bug was a
/// compile failure); the assertions additionally prove each carrier dispatches
/// into its OWN per-carrier vtable rather than cross-wiring to the sibling's.
/// </summary>
public class CrossCarrierSignatureCollisionTests : TestBase
{
    public CrossCarrierSignatureCollisionTests(TestResults results) : base(results) { }

    /// <summary>
    /// A C# class implementing ONLY the plain interface dispatches through the
    /// <c>EveryProtocol</c> carrier's witness.
    /// </summary>
    public void TestPlainCarrierRoundTrips()
    {
        var impl = new PlainOnlyGreeter();
        var result = Functions.CallGreetingPlain(impl, "Ada");
        AssertEqual("plain-only:Ada", result,
            "EveryProtocol carrier witness dispatched into the plain managed implementation");
    }

    /// <summary>
    /// A C# class implementing ONLY the @objc interface dispatches through the
    /// <c>EveryObjCProtocol</c> carrier's witness.
    /// </summary>
    public void TestObjCCarrierRoundTrips()
    {
        var impl = new ObjCOnlyGreeter();
        var result = Functions.CallGreetingObjC(impl, "Bob");
        AssertEqual("objc-only:Bob", result,
            "EveryObjCProtocol carrier witness dispatched into the @objc managed implementation");
    }

    /// <summary>
    /// A single C# object implementing BOTH interfaces (explicit implementations
    /// returning distinguishable values) must route each free function to the
    /// method of the matching carrier — proving the two same-signature carriers
    /// dispatch through independent vtables and never cross-wire.
    /// </summary>
    public void TestBothCarriersDispatchIndependently()
    {
        var impl = new DualCarrierGreeter();

        var plain = Functions.CallGreetingPlain(impl, "X");
        AssertEqual("plain:X", plain,
            "Plain free function routed to the plain interface method");

        var objc = Functions.CallGreetingObjC(impl, "X");
        AssertEqual("objc:X", objc,
            "@objc free function routed to the @objc interface method (no cross-carrier leak)");
    }
}

/// <summary>Plain-carrier-only managed implementation.</summary>
internal class PlainOnlyGreeter : IGreetingProviderPlain
{
    public string MakeGreeting(string name) => $"plain-only:{name}";
}

/// <summary>@objc-carrier-only managed implementation.</summary>
internal class ObjCOnlyGreeter : IGreetingProviderObjC
{
    public string MakeGreeting(string name) => $"objc-only:{name}";
}

/// <summary>
/// Implements both same-signature interfaces with explicit implementations that
/// return carrier-distinguishable values, so a cross-carrier vtable mix-up would
/// surface as the wrong prefix.
/// </summary>
internal class DualCarrierGreeter : IGreetingProviderPlain, IGreetingProviderObjC
{
    string IGreetingProviderPlain.MakeGreeting(string name) => $"plain:{name}";
    string IGreetingProviderObjC.MakeGreeting(string name) => $"objc:{name}";
}
