// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Swift enums whose case payload is an Apple framework enum with a declared raw type — an
/// <c>Int32</c>-backed <c>CLAuthorizationStatus</c> and an <c>Int</c>-backed
/// <c>UIBarButtonItem.SystemItem</c>. The payload's width comes from that raw type, so a wrong width
/// shows up as a Swift-side <c>rawValue</c> that disagrees with the value C# put in or read out.
/// </summary>
public class AppleRawValuePayloadEnumTests : TestBase
{
    public AppleRawValuePayloadEnumTests(TestResults results) : base(results) { }

    public void TestInt32BackedPayloadRoundTripsThroughSwift()
    {
        using var constructed = LocationPermissionEvent.Changed(CoreLocation.CLAuthorizationStatus.Denied);
        AssertEqual((int)CoreLocation.CLAuthorizationStatus.Denied, constructed.RawStatus,
            "Swift reads the raw value C# placed in the payload");

        using var made = LocationPermissionEvent.Make((int)CoreLocation.CLAuthorizationStatus.AuthorizedAlways);
        AssertEqual(LocationPermissionEvent.CaseTag.Changed, made.Tag, "a valid raw status builds the payload case");
        AssertTrue(made.TryGetChanged(out var status), "the payload can be read back");
        AssertEqual(CoreLocation.CLAuthorizationStatus.AuthorizedAlways, status,
            "C# reads the payload Swift stored");
    }

    public void TestInt32BackedPayloadEnumKeepsItsPayloadlessCase()
    {
        using var made = LocationPermissionEvent.Make(-1);

        AssertEqual(LocationPermissionEvent.CaseTag.Unknown, made.Tag, "a negative raw status selects the payloadless case");
        AssertEqual(-1, made.RawStatus, "the payloadless case reports no raw value");
    }

    public void TestInt32BackedPayloadCarriesARawValueNoCaseNames()
    {
        using var made = LocationPermissionEvent.Make(99);

        AssertEqual(LocationPermissionEvent.CaseTag.Changed, made.Tag, "an imported C enum accepts any raw value");
        AssertEqual(99, made.RawStatus, "Swift reads back the full 32-bit raw value");
        AssertTrue(made.TryGetChanged(out var status), "the payload can be read back");
        AssertEqual(99, (int)status, "C# reads the raw value Swift stored");
    }

    public void TestIntBackedPayloadRoundTripsThroughSwift()
    {
        using var constructed = BarItemEvent.Tapped(UIKit.UIBarButtonSystemItem.Add);
        AssertEqual((int)UIKit.UIBarButtonSystemItem.Add, constructed.RawItem,
            "Swift reads the raw value C# placed in the payload");

        using var made = BarItemEvent.Tapping((int)UIKit.UIBarButtonSystemItem.Cancel);
        AssertEqual(BarItemEvent.CaseTag.Tapped, made.Tag, "a valid raw item builds the payload case");
        AssertTrue(made.TryGetTapped(out var item), "the payload can be read back");
        AssertEqual(UIKit.UIBarButtonSystemItem.Cancel, item, "C# reads the payload Swift stored");
    }
}
