// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// A payload-case factory whose associated value is <c>Swift.Int</c> surfaces the ABI-accurate
/// <c>nint</c> parameter. The generated factory ALSO emits an additive <c>int</c> convenience
/// forwarder — the enum-case factory bypasses <c>NativeIntOverloadEmitter</c> (which runs only
/// through the method post-processor), so without it the int overload every other native-int API
/// gets would be missing here. These tests confirm the forwarder round-trips the value through the
/// <c>(nint)</c> cast → primary factory delegation.
/// </summary>
public class EnumCaseIntForwarderTests : TestBase
{
    public EnumCaseIntForwarderTests(TestResults results) : base(results) { }

    /// <summary>
    /// A plain <c>int</c> literal binds to the additive int forwarder (not the nint primary),
    /// which casts <c>(nint)42</c> and delegates. <c>RawCount</c> reads the payload back out.
    /// </summary>
    public void TestIntForwarder_RoundTripsPayload()
    {
        using var budget = RetryBudget.Limited(42);
        AssertEqual(42, budget.RawCount, "int-forwarder payload round-trips through (nint) delegation");
    }

    /// <summary>
    /// A negative value survives the signed <c>int → nint</c> widening cast in the forwarder —
    /// guards against an accidental unsigned narrowing.
    /// </summary>
    public void TestIntForwarder_NegativeValue()
    {
        using var budget = RetryBudget.Limited(-7);
        AssertEqual(-7, budget.RawCount, "negative int-forwarder payload preserves sign");
    }
}
