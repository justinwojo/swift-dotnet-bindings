// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.FoundationInterop;

/// <summary>
/// Bug (a-1) round-trip: IndexPath/Calendar/CharacterSet/Locale are Swift VALUE TYPES
/// (structs) that bridge to an ObjC class family (NSIndexPath/NSCalendar/NSCharacterSet/
/// NSLocale). Pre-fix, a `kind="class"` misregistration in `FoundationDatabase.xml` made
/// the generator treat them as reference types, corrupting P/Invoke marshalling
/// (`Unmanaged` used on a value type — CoreStore/JTAppleCalendar's exact symptom). The
/// fix registers them `kind="struct"` + `objcBridgeable="true"`; this class exercises the
/// resulting `.Handle`-based by-value marshalling end-to-end at runtime.
/// </summary>
public class ObjCBridgeableValueTypeBugReprosTests : TestBase
{
    public ObjCBridgeableValueTypeBugReprosTests(TestResults results) : base(results) { }

    public void TestIndexPath_RoundTripsThroughParamAndReturn()
    {
        var path = TestLibFunctions.MakeIndexPath(2, 5);
        var last = TestLibFunctions.LastIndexPathComponent(path);
        AssertEqual(5, last, "IndexPath value-type param/return round-trips via .Handle marshalling");
    }

    public void TestCalendar_RoundTripsThroughParamAndReturn()
    {
        var calendar = TestLibFunctions.GetGregorianCalendar();
        var name = TestLibFunctions.CalendarIdentifierName(calendar).ToString();
        AssertEqual("gregorian", name, "Calendar value-type param/return round-trips via .Handle marshalling");
    }

    public void TestCharacterSet_RoundTripsThroughParamAndReturn()
    {
        var set = TestLibFunctions.GetAlphanumericCharacterSet();
        var containsA = TestLibFunctions.CharacterSetContains(set, (int)'A');
        var containsAt = TestLibFunctions.CharacterSetContains(set, (int)'@');
        AssertTrue(containsA, "Alphanumerics CharacterSet contains 'A'");
        AssertFalse(containsAt, "Alphanumerics CharacterSet does not contain '@'");
    }

    public void TestLocale_RoundTripsThroughParamAndReturn()
    {
        var locale = TestLibFunctions.GetPosixLocale();
        var name = TestLibFunctions.LocaleIdentifierName(locale).ToString();
        AssertEqual("en_US_POSIX", name, "Locale value-type param/return round-trips via .Handle marshalling");
    }
}
