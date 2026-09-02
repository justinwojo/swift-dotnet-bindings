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

    // The members below are typed by a framework enum whose module carries no ObjC-bridging flags.
    // Their existence is most of the claim — a record built only for bridging modules never covers
    // such a type, so every member typed by one is skipped as an unprojected Apple type and there is
    // nothing to call. The round-trips then confirm the record describes the right shape: the enum
    // is unsigned in Swift while the boundary carries a signed word, so a raw value reconstructed by
    // a checked conversion rather than by bit pattern would not survive.

    public void TestRegisteredAppleEnum_NonBridgingModuleEnumRoundTripsThroughProperty()
    {
        using var selection = new ImageOrientationSelectionLike(ImageIO.CGImagePropertyOrientation.Right);

        AssertEqual(
            ImageIO.CGImagePropertyOrientation.Right,
            selection.Orientation,
            "an enum from a module with no ObjC-bridging flags survives the constructor and its getter");
    }

    public void TestRegisteredAppleEnum_NonBridgingModuleEnumRoundTripsThroughMethod()
    {
        using var selection = new ImageOrientationSelectionLike(ImageIO.CGImagePropertyOrientation.Up);

        AssertEqual(
            ImageIO.CGImagePropertyOrientation.LeftMirrored,
            selection.Alternate(ImageIO.CGImagePropertyOrientation.LeftMirrored),
            "the same enum passes in and comes back out of a method unchanged");
        AssertEqual(
            ImageIO.CGImagePropertyOrientation.Up,
            selection.Orientation,
            "the stored value is untouched by the call that returns its argument");
    }

    public void TestRegisteredAppleEnum_NonBridgingModuleEnumRawValueSurvives()
    {
        using var selection = new ImageOrientationSelectionLike(ImageIO.CGImagePropertyOrientation.Up);

        // Read through Swift's own .rawValue: the case that arrived is the case whose number comes
        // back, so a value shifted by a mis-sized carrier reads as a different orientation.
        AssertEqual(8u, selection.RawValue(ImageIO.CGImagePropertyOrientation.Left), "Swift received the Left case, whose CGImagePropertyOrientation raw value is 8");
        AssertEqual(1u, selection.RawValue(ImageIO.CGImagePropertyOrientation.Up), "Swift received the Up case, whose CGImagePropertyOrientation raw value is 1");
    }
}
