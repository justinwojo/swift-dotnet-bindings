// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Runtime coverage for members typed by an Apple framework NS_STRING_ENUM — a constant group that
/// Swift imports as a String-backed <c>RawRepresentable</c> struct and .NET projects as a C# enum
/// plus a <c>{Enum}Extensions</c> converter over NSString constants.
/// </summary>
/// <remarks>
/// Compiling is a weak claim here, because the two plausible wrong bindings both compile once the
/// enum is merely known to be a value type: shipping the C# enum's ordinal across what is really an
/// NSString ABI, or bridging an opaque pointer straight through. Both would survive a test that only
/// echoes a value back. So the assertions are built on what the SWIFT side observes: the filter
/// members compare the values they receive against a second value that also crossed the boundary,
/// and the raw-value member reads the string out of the constant Swift actually received and
/// compares it to the platform's own constant for the same enum case. A carrier that arrived as the
/// wrong symbology — or as no symbology at all — fails those rather than passing by accident.
/// </remarks>
public class RegisteredAppleTypedEnumMemberTests : TestBase
{
    public RegisteredAppleTypedEnumMemberTests(TestResults results) : base(results) { }

    public void TestAppleTypedEnum_PropertiesRoundTripConstructorArguments()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        AssertEqual(
            Vision.VNBarcodeSymbology.QR,
            selection.Preferred,
            "string-backed Apple enum survives the constructor and a read-only property getter");
        AssertEqual(
            Vision.VNBarcodeSymbology.Aztec,
            selection.Fallback,
            "a second constructor argument of the same type is not confused with the first");
    }

    public void TestAppleTypedEnum_SettablePropertyRoundTrips()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        selection.Fallback = Vision.VNBarcodeSymbology.Pdf417;

        AssertEqual(
            Vision.VNBarcodeSymbology.Pdf417,
            selection.Fallback,
            "the setter's inbound conversion and the getter's outbound conversion agree");
        AssertEqual(
            Vision.VNBarcodeSymbology.QR,
            selection.Preferred,
            "writing one property of this type leaves its sibling untouched");
    }

    public void TestAppleTypedEnum_MethodRoundTripsBothDirections()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        AssertEqual(
            Vision.VNBarcodeSymbology.Ean13,
            selection.Alternate(Vision.VNBarcodeSymbology.Ean13),
            "string-backed Apple enum passes in and comes back out of a method unchanged");
    }

    public void TestAppleTypedEnum_SwiftSeesThePlatformConstant()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        // Ground truth comes from the same platform binding the marshalling uses, so the assertion
        // pins the identity of the constant that crossed rather than a literal that could drift with
        // the OS. Swift reads .rawValue off what it received, so a value that arrived as some other
        // symbology reads back as that other symbology's string.
        var expectedQR = Vision.VNBarcodeSymbologyExtensions.GetConstant(Vision.VNBarcodeSymbology.QR)!.ToString();
        var expectedAztec = Vision.VNBarcodeSymbologyExtensions.GetConstant(Vision.VNBarcodeSymbology.Aztec)!.ToString();

        AssertTrue(expectedQR != expectedAztec, "the two constants under test are distinct strings");
        // `RawValueOf`, not `RawValue`: the optional-parameter sibling added below makes these two
        // Swift `rawValue` overloads collide, so the resolver disambiguates on the argument label.
        AssertEqual(expectedQR, selection.RawValueOf(Vision.VNBarcodeSymbology.QR), "Swift received the QR constant itself");
        AssertEqual(expectedAztec, selection.RawValueOf(Vision.VNBarcodeSymbology.Aztec), "Swift received the Aztec constant itself");
    }

    public void TestAppleTypedEnum_ArrayParameterAndReturnFilter()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        var input = new[]
        {
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec,
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Pdf417,
        };

        // Swift compares each element against a symbology that crossed as a scalar, so agreement
        // here means the per-element conversion inside the array and the scalar conversion produced
        // the same values — the failure mode a pointer that merely round-trips cannot reproduce.
        var kept = selection.FilterSymbologies(input, Vision.VNBarcodeSymbology.QR);

        AssertEqual(2, kept.Count, "Swift matched exactly the two QR elements in the array parameter");
        AssertEqual(Vision.VNBarcodeSymbology.QR, kept[0], "the first element of the returned array projects back to the enum");
        AssertEqual(Vision.VNBarcodeSymbology.QR, kept[1], "the second element of the returned array projects back to the enum");
    }

    public void TestAppleTypedEnum_ArrayParameterObservedIndependentlyOfArrayReturn()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        var input = new[]
        {
            Vision.VNBarcodeSymbology.Aztec,
            Vision.VNBarcodeSymbology.Ean13,
            Vision.VNBarcodeSymbology.Aztec,
        };

        AssertEqual(2, selection.CountSymbologies(input, Vision.VNBarcodeSymbology.Aztec), "the inbound array is counted through a scalar return");
        AssertEqual(0, selection.CountSymbologies(input, Vision.VNBarcodeSymbology.QR), "a symbology absent from the array matches nothing");
        AssertEqual(0, selection.CountSymbologies(System.Array.Empty<Vision.VNBarcodeSymbology>(), Vision.VNBarcodeSymbology.QR), "an empty array crosses as an empty array");
    }

    public void TestAppleTypedEnum_EmptyFilterResultIsAnEmptyArray()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        var kept = selection.FilterSymbologies(
            new[] { Vision.VNBarcodeSymbology.Aztec },
            Vision.VNBarcodeSymbology.QR);

        AssertNotNull(kept, "an empty Swift array still projects to a list rather than null");
        AssertEqual(0, kept.Count, "no element matched, so the returned array is empty");
    }

    public void TestAppleTypedEnum_OptionalReturnCarriesBothCases()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        // The nullable-pointer ABI reads the carrier only after testing the pointer against nil, so
        // the .none case exercises a different branch of the same plan than the .some case does.
        AssertEqual(
            Vision.VNBarcodeSymbology.Ean13,
            selection.FirstSymbology(new[] { Vision.VNBarcodeSymbology.Ean13, Vision.VNBarcodeSymbology.QR }),
            "a present optional return converts the carrier back to the enum");
        // AssertNull is constrained to reference types; this return is a nullable value type.
        AssertTrue(
            selection.FirstSymbology(System.Array.Empty<Vision.VNBarcodeSymbology>()) is null,
            "an absent optional return arrives as null rather than a default enum value");
    }

    public void TestAppleTypedEnum_OptionalParameterCarriesBothCases()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        // Read through Swift's own .rawValue again, so a constant that arrived as the wrong
        // symbology reads as a wrong string instead of merely being non-empty.
        var expectedPdf417 = Vision.VNBarcodeSymbologyExtensions.GetConstant(Vision.VNBarcodeSymbology.Pdf417)!.ToString();

        AssertEqual(
            expectedPdf417,
            selection.RawValueOfOptional(Vision.VNBarcodeSymbology.Pdf417),
            "a present optional parameter reaches Swift as the platform constant");
        AssertEqual(
            string.Empty,
            selection.RawValueOfOptional(null),
            "an absent optional parameter reaches Swift as nil");
    }

    public void TestAppleTypedEnum_DictionaryValuesConvertBothWays()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        var echoed = selection.EchoLabels(new Dictionary<string, Vision.VNBarcodeSymbology>
        {
            ["first"] = Vision.VNBarcodeSymbology.QR,
            ["second"] = Vision.VNBarcodeSymbology.Aztec,
        });

        AssertEqual(2, echoed.Count, "both entries survived the round trip through NSDictionary");
        AssertEqual(Vision.VNBarcodeSymbology.QR, echoed["first"], "the value stored under the first key reads back as its own enum case");
        AssertEqual(Vision.VNBarcodeSymbology.Aztec, echoed["second"], "the two values were not conflated with each other");
    }

    public void TestAppleTypedEnum_ArrayPropertySetterConvertsElements()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        selection.Accepted = new[] { Vision.VNBarcodeSymbology.QR, Vision.VNBarcodeSymbology.Pdf417 };

        // Ground truth from the platform converter, and read back through SWIFT's own .rawValue, so
        // an element that arrived as the wrong constant fails rather than round-tripping in C#.
        var expectedQR = Vision.VNBarcodeSymbologyExtensions.GetConstant(Vision.VNBarcodeSymbology.QR)!.ToString();
        var expectedPdf417 = Vision.VNBarcodeSymbologyExtensions.GetConstant(Vision.VNBarcodeSymbology.Pdf417)!.ToString();

        // `GetAcceptedRawValues`: a no-argument noun-named Swift method takes the emitter's `Get` prefix.
        var raw = selection.GetAcceptedRawValues();

        AssertEqual(2, raw.Count, "both elements survived the collection setter");
        AssertEqual(expectedQR, raw[0], "the first element reached Swift as the QR constant");
        AssertEqual(expectedPdf417, raw[1], "the second element reached Swift as the PDF417 constant, in order");
        AssertEqual(2, selection.Accepted.Count, "the getter projects the stored array back to enum values");
        AssertEqual(Vision.VNBarcodeSymbology.QR, selection.Accepted[0], "the getter round-trips the first element");
    }

    public void TestAppleTypedEnum_SetPropertySetterConvertsElements()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        selection.AcceptedUnique = new HashSet<Vision.VNBarcodeSymbology>
        {
            Vision.VNBarcodeSymbology.Aztec,
            Vision.VNBarcodeSymbology.Ean13,
        };

        // Swift decides membership on the constant's own hash, so a carrier that was never a real
        // symbology matches nothing here instead of merely being present.
        AssertEqual(1, selection.AcceptedUniqueCount(Vision.VNBarcodeSymbology.Aztec), "Swift matched the Aztec element it received");
        AssertEqual(1, selection.AcceptedUniqueCount(Vision.VNBarcodeSymbology.Ean13), "Swift matched the EAN-13 element it received");
        AssertEqual(0, selection.AcceptedUniqueCount(Vision.VNBarcodeSymbology.QR), "a symbology never stored matches nothing");
        AssertEqual(2, selection.AcceptedUnique.Count, "the getter projects the stored set back to enum values");
    }

    public void TestAppleTypedEnum_SetElementsConvertBothWays()
    {
        using var selection = new BarcodeSymbologySelectionLike(
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Aztec);

        // Set membership is decided by the CONSTANT's own hash on the Swift side, so a carrier that
        // arrived as something other than a real symbology collapses or duplicates entries here.
        var echoed = selection.EchoUnique(new HashSet<Vision.VNBarcodeSymbology>
        {
            Vision.VNBarcodeSymbology.QR,
            Vision.VNBarcodeSymbology.Ean13,
        });

        AssertEqual(2, echoed.Count, "two distinct symbologies stayed distinct across NSSet");
        AssertTrue(echoed.Contains(Vision.VNBarcodeSymbology.QR), "the QR element reads back as QR");
        AssertTrue(echoed.Contains(Vision.VNBarcodeSymbology.Ean13), "the EAN-13 element reads back as EAN-13");
    }
}
