// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Types declared inside a constrained extension of a generic type, in both spellings
/// (<c>ConstrainedExtensionNestedTypes.swift</c>). Pinned: <c>PinBox&lt;PinTag&gt;.Op</c> and
/// <c>PinBox&lt;PinTag&gt;.Op.Options</c> must be spelled with the pinned argument on the generic
/// parent, in member wrappers and in free-function signatures alike. Unpinned: the raw-value support
/// of <c>PinBox&lt;V&gt;.Unit</c> and <c>OpenBox&lt;V&gt;.Kind</c> must reach the enum through its
/// metadata, since a <c>@_cdecl</c> cannot be generic. Values are built by Swift factories, so the
/// tests run on every engine; the nested types' own initializers refuse on Mono and the CoreCLR
/// interpreter (SWIFTBIND130), which this fixture does not set out to change.
/// </summary>
public class ConstrainedExtensionNestedTypesTests : TestBase
{
    public ConstrainedExtensionNestedTypesTests(TestResults results) : base(results) { }

    public void TestPinnedNestedStruct_AccessorsReadThroughThePinnedParent()
    {
        using var op = Functions.MakePinOp();
        AssertEqual(41, op.GetLabel(), "PinBox<PinTag>.Op.label");
        AssertEqual(42, op.GetTag(), "PinBox<PinTag>.Op.tag reads label + 1");
    }

    public void TestPinnedNestedOptionSet_RoundTripsItsRawValue()
    {
        using var options = Functions.MakePinOptions(3);
        AssertEqual((ushort)3, options.GetRawValue(), "PinBox<PinTag>.Op.Options.rawValue");
        AssertEqual((ushort)3, Functions.PinOptionsRawValue(options),
            "a free function taking PinBox<PinTag>.Op.Options reads the same value back");
    }

    public void TestUnpinnedConstrainedEnum_FromRawValueReachesTheEnum()
    {
        using var minutes = PinBox<PinMeter>.Unit.FromRawValue("minutes");
        AssertNotNull(minutes, "PinBox<PinMeter>.Unit(rawValue: \"minutes\")");
        AssertEqual("minutes", Functions.PinUnitRawValue(minutes!), "Swift reads the case back");

        var unknown = PinBox<PinMeter>.Unit.FromRawValue("hours");
        AssertTrue(unknown is null, "an unknown raw value yields no case");
    }

    public void TestUnpinnedConstrainedEnum_CaseStaticsAreTheirCases()
    {
        AssertEqual("seconds", Functions.PinUnitRawValue(PinBox<PinMeter>.Unit.Seconds), "Unit.seconds");
        AssertEqual("minutes", Functions.PinUnitRawValue(PinBox<PinMeter>.Unit.Minutes), "Unit.minutes");
    }

    public void TestUnconstrainedGenericParentEnum_RawValuesRoundTrip()
    {
        using var second = OpenBox<PinTag>.Kind.FromRawValue(7);
        AssertNotNull(second, "OpenBox<PinTag>.Kind(rawValue: 7)");
        AssertEqual(7, second!.RawValue, "Kind.second.rawValue");
        AssertEqual(7, Functions.OpenKindRawValue(second), "Swift reads the case back");

        AssertEqual(3, OpenBox<PinTag>.Kind.First.RawValue, "Kind.first.rawValue");
        AssertTrue(OpenBox<PinTag>.Kind.FromRawValue(5) is null, "an unknown raw value yields no case");
    }

    public void TestFrozenGenericParentEnum_RawValuesRoundTrip()
    {
        using var late = OpenBox<PinTag>.Stage.FromRawValue(9);
        AssertNotNull(late, "OpenBox<PinTag>.Stage(rawValue: 9)");
        AssertEqual(9, late!.RawValue, "Stage.late.rawValue");
        AssertEqual(9, Functions.OpenStageRawValue(late), "Swift reads the case back");

        using var early = OpenBox<PinTag>.Stage.FromRawValue(3);
        AssertNotNull(early, "OpenBox<PinTag>.Stage(rawValue: 3)");
        AssertEqual(3, Functions.OpenStageRawValue(early!), "Swift reads the first case back");
        AssertTrue(OpenBox<PinTag>.Stage.FromRawValue(4) is null, "an unknown raw value yields no case");
    }
}
