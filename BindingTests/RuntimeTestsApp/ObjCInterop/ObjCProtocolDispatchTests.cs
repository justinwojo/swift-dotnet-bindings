// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.ObjCInterop;

/// <summary>
/// End-to-end gate for the EveryObjCProtocol routing fix.
///
/// Previously the generator skipped <c>@objc protocol X: NSObjectProtocol</c>
/// conformances (the plain Swift <c>EveryProtocol</c> class cannot satisfy
/// NSObjectProtocol's NSObject identity surface), which broke Stripe's
/// STPAuthenticationContext / STPCustomerEphemeralKeyProvider /
/// STPIssuingCardEphemeralKeyProvider in the generated bindings. The fix introduces
/// a parallel <c>EveryObjCProtocol: NSObject</c> helper class in the emitted
/// wrapper module and routes NSObjectProtocol-only conformances through it.
///
/// This test exercises the full round-trip: a plain C# class implements the
/// generated <c>INumberProvider</c> interface and is passed to a Swift function
/// that invokes the witness method through the existential. The Swift wrapper
/// only compiles if the synthesized <c>extension EveryObjCProtocol:
/// NumberProvider</c> type-checks, so reaching the call already proves the
/// emitter routed correctly; the return-value assertion proves the vtable
/// callback dispatches into the managed method.
/// </summary>
public class ObjCProtocolDispatchTests : TestBase
{
    public ObjCProtocolDispatchTests(TestResults results) : base(results) { }

    public void TestNSObjectProtocolInheritorRoundTrips()
    {
        var impl = new NumberProviderImpl(value: 42);

        // Auto-wrap constructs an EveryObjCProtocol-backed proxy and hands it
        // to the Swift function. If the routing regresses to skip-the-conformance,
        // the generated INumberProvider interface and/or wrapper Swift module won't
        // compile and this call site won't exist.
        var result = TestLibFunctions.CallNumberProvider(impl);

        AssertEqual(42, result, "EveryObjCProtocol witness dispatched into the managed implementation");
        AssertTrue(impl.WasCalled, "Managed provideNumber() actually fired");
    }
}

/// <summary>
/// Plain managed implementation of the generated <c>INumberProvider</c>
/// interface — no proxy class subclassing, no manual existential wrapping. The
/// auto-wrap fallback in the generator is what makes this work.
/// </summary>
internal class NumberProviderImpl : INumberProvider
{
    private readonly int _value;

    public NumberProviderImpl(int value)
    {
        _value = value;
    }

    public bool WasCalled { get; private set; }

    public int ProvideNumber()
    {
        WasCalled = true;
        return _value;
    }
}
