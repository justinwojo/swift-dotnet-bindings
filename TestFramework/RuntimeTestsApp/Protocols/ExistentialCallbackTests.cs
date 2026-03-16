// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Tests that Swift can call back into C# protocol implementations passing
/// existential parameters (any Protocol) through proxy receiver dispatch.
///
/// Session 6: Existential parameter marshalling in protocol proxy receivers.
/// The proxy receiver unmarshals ExistentialContainer1 → HasValueProxy and
/// dispatches to the C# implementation.
///
/// Tier 3: Proxy object passes through CallConvSwift P/Invoke.
/// NativeAOT (device builds) should work.
/// </summary>
// Mono JIT assertion (jit-info.c:918): proxy objects through CallConvSwift P/Invoke
[MonoJitCrash]
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
