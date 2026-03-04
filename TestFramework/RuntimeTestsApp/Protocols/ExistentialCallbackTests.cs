// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Tests that Swift can call back into C# protocol implementations passing
/// existential parameters (any Protocol) through proxy receiver dispatch.
///
/// Session 6: Existential parameter marshalling in protocol proxy receivers.
/// The proxy receiver unmarshals ExistentialContainer1 → HasValueProxy and
/// dispatches to the C# implementation.
///
/// Tier 3: Proxy object passes through CallConvSwift P/Invoke which hits
/// Mono JIT SIGSEGV in swift_getObjectType → objc_msgSend_uncached.
/// Same root cause as SafeHandle non-blittable through CallConvSwift.
/// NativeAOT (device builds) should work.
/// </summary>
[TestTier(TestTier.Tier3)]
[CrashRisk("Mono JIT SIGSEGV: proxy object through CallConvSwift")]
public class ExistentialCallbackTests : TestBase
{
    public ExistentialCallbackTests(TestResults results) : base(results) { }

    /// <summary>
    /// Tests that Swift can call a C# protocol implementation passing
    /// an existential parameter (any HasValue) through proxy receiver dispatch.
    /// </summary>
    public void TestExistentialParamCallbackDelivery()
    {
        var impl = new TestExistentialDelegate();
        var proxy = new ExistentialParamDelegateProxy(impl);

        // Swift creates a MutableItem(value: 42), passes it as `any HasValue`
        // to delegate.didReceive(value:). The proxy receiver unmarshals
        // ExistentialContainer1 → HasValueProxy and dispatches to impl.
        TestLibFunctions.FireExistentialDelegate(proxy, intValue: 42);

        AssertTrue(impl.WasCalled, "Delegate was called");
        AssertEqual(42, impl.ReceivedValue, "Received correct existential value");
    }
}

/// <summary>
/// C# implementation of IExistentialParamDelegate for testing.
/// Records whether the callback was received and what value was passed.
/// </summary>
internal class TestExistentialDelegate : IExistentialParamDelegate
{
    public bool WasCalled { get; private set; }
    public int ReceivedValue { get; private set; }

    public void DidReceive(IHasValue value)
    {
        WasCalled = true;
        ReceivedValue = value.Value;
    }
}
