// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Runtime coverage for members typed by an Apple framework enum that the framework registry
/// lists as a value type and additionally describes as an integer enum.
/// </summary>
/// <remarks>
/// The declarations compiling is only half the claim: the described shape also asserts an ABI —
/// the enum crosses the boundary as its NSInteger raw value, i.e. a 64-bit carrier, in a
/// constructor argument, a property getter, and both directions of a method. A record built with
/// the wrong underlying width would still compile and would still emit a plain C# enum; only
/// actually calling through it can show the value surviving. The named-value round-trips prove
/// the wiring; they cannot prove the width, since every named case fits in 32 bits. The width
/// itself is pinned by the probe test, which pushes a value with bits only above the low 32
/// through the same members — legal because NS_ENUM enums are open (any raw bit pattern is a
/// valid value) and the Swift members store or return the value untouched.
/// </remarks>
public class RegisteredAppleEnumMemberTests : TestBase
{
    public RegisteredAppleEnumMemberTests(TestResults results) : base(results) { }

    public void TestRegisteredAppleEnum_PropertyRoundTripsConstructorArgument()
    {
        using var config = new PaymentButtonConfigurationLike(PassKit.PKPaymentButtonType.InStore, 12);

        AssertEqual(
            PassKit.PKPaymentButtonType.InStore,
            config.ButtonType,
            "registry-described Apple enum survives the constructor and its property getter");
        AssertEqual(12, config.CornerRadius, "the Int sibling on the same type still round-trips");
    }

    public void TestRegisteredAppleEnum_MethodRoundTripsBothDirections()
    {
        using var config = new PaymentButtonConfigurationLike(PassKit.PKPaymentButtonType.SetUp, 0);

        AssertEqual(
            PassKit.PKPaymentButtonType.Donate,
            config.Alternate(PassKit.PKPaymentButtonType.Donate),
            "registry-described Apple enum passes in and comes back out of a method unchanged");
        AssertEqual(
            PassKit.PKPaymentButtonType.SetUp,
            config.ButtonType,
            "the stored value is untouched by the call that returns its argument");
    }

    public void TestRegisteredAppleEnum_CarrierPreservesBitsAboveThirtyTwo()
    {
        AssertEqual(
            typeof(long),
            Enum.GetUnderlyingType(typeof(PassKit.PKPaymentButtonType)),
            "the imported enum is NSInteger-backed, so a 64-bit carrier is the contract under test");

        // A truncating 32-bit carrier round-trips every named case unchanged, so only a value that
        // sets bits above the low 32 can distinguish the widths; the low bits keep the truncated
        // result nonzero so a failure reads as truncation, not zeroing.
        var probe = (PassKit.PKPaymentButtonType)((1L << 40) | 5L);
        using var config = new PaymentButtonConfigurationLike(probe, 0);

        AssertEqual(probe, config.ButtonType, "constructor argument and property getter preserve bits above the low 32");
        AssertEqual(probe, config.Alternate(probe), "method argument and return preserve bits above the low 32");
    }
}
