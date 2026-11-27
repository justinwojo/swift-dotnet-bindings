// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Whether Swift moves a <c>@frozen</c> value in registers or through memory follows what it stores
/// inline. A computed property stores nothing, so a struct whose only resilient member is computed
/// stays loadable; a stored resilient payload, a <c>lazy</c> property's hidden storage, or an opaque
/// existential field makes the value address-only; an <c>indirect</c> case's box stays loadable.
/// Each test hands the value across in both directions and has Swift name what it holds, so a
/// crossing in the wrong convention shows as a wrong value or a fault. Fixture:
/// Types/FrozenStoredLayout.swift.
/// </summary>
public class FrozenStoredLayoutTests : TestBase
{
    public FrozenStoredLayoutTests(TestResults results) : base(results) { }

    public void TestAComputedResilientPropertyLeavesOneFieldInARegister()
    {
        var fromSwift = TestLibFunctions.MakeStoredLayoutComputedCell(17);
        AssertEqual(17, (int)fromSwift.Count, "C# reads the returned field");
        AssertEqual(17, (int)TestLibFunctions.StoredLayoutComputedCellCount(fromSwift), "Swift reads the returned struct back");

        var fromCSharp = new StoredLayoutComputedCell(23);
        AssertEqual(23, (int)TestLibFunctions.StoredLayoutComputedCellCount(fromCSharp), "Swift reads a C#-built struct");
        var echoed = TestLibFunctions.StoredLayoutComputedCellEcho(fromCSharp);
        AssertEqual(23, (int)echoed.Count, "a by-value argument returned unchanged");

        using var detail = fromCSharp.Detail;
        AssertEqual(230, (int)detail.Value, "the computed property reads self");
        using var unit = StoredLayoutComputedCell.Unit;
        AssertEqual(1, (int)unit.Value, "the static property is reachable");
    }

    public void TestAComputedResilientPropertyLeavesTwoFieldsInRegisters()
    {
        using var fromSwift = TestLibFunctions.MakeStoredLayoutComputedPair(3, 7);
        AssertEqual(3, (int)fromSwift.Count, "C# reads the first returned field");
        AssertEqual(7, (int)fromSwift.Scale, "C# reads the second returned field");
        AssertEqual("3x7", TestLibFunctions.StoredLayoutComputedPairName(fromSwift), "Swift reads the returned struct back");

        using var echoed = TestLibFunctions.StoredLayoutComputedPairEcho(fromSwift);
        AssertEqual("3x7", echoed.Summary, "a by-value argument returned unchanged");

        using var fromCSharp = new StoredLayoutComputedPair(5, 2);
        AssertEqual("5x2", TestLibFunctions.StoredLayoutComputedPairName(fromCSharp), "Swift reads a C#-built struct");
        using var scaled = fromCSharp.Scaled(4);
        AssertEqual("5x8", scaled.Summary, "a method on self returning the struct");
        using var detail = fromCSharp.Detail;
        AssertEqual(10, (int)detail.Value, "the computed property reads self");
    }

    public void TestAStructStoringTheComputedCellStaysLoadable()
    {
        var fromSwift = TestLibFunctions.MakeStoredLayoutComputedHolder(4, 9);
        AssertEqual(4, (int)fromSwift.Cell.Count, "C# reads the nested field");
        AssertEqual(9, (int)fromSwift.Tag, "C# reads the outer field");
        AssertEqual("4/9", TestLibFunctions.StoredLayoutComputedHolderName(fromSwift), "Swift reads the returned struct back");

        var fromCSharp = new StoredLayoutComputedHolder(new StoredLayoutComputedCell(6), 1);
        var echoed = TestLibFunctions.StoredLayoutComputedHolderEcho(fromCSharp);
        AssertEqual("6/1", TestLibFunctions.StoredLayoutComputedHolderName(echoed), "a C#-built struct returned unchanged");
    }

    public void TestALazyPropertysStorageMakesTheStructAddressOnly()
    {
        using var cell = TestLibFunctions.MakeStoredLayoutLazyCell(12);
        AssertEqual(12, (int)TestLibFunctions.StoredLayoutLazyCellCount(cell), "Swift reads the returned struct back");
    }

    public void TestAFrozenEnumWithAResilientPayloadCrossesByAddress()
    {
        using var detail = TestLibFunctions.MakeStoredLayoutChoice(42);
        AssertEqual("detail 42", TestLibFunctions.StoredLayoutChoiceName(detail), "Swift reads the returned payload case back");
        if (!detail.TryGetDetail(out var payload))
        {
            AssertTrue(false, "C# sees the payload case");
            return;
        }
        using (payload)
            AssertEqual(42, (int)payload.Value, "C# reads the payload");

        using var none = TestLibFunctions.MakeStoredLayoutChoice(0);
        AssertEqual("none", TestLibFunctions.StoredLayoutChoiceName(none), "Swift reads the returned empty case back");

        using var newDetail = new StoredLayoutDetail(8);
        using var fromCSharp = StoredLayoutChoice.Detail(newDetail);
        using var echoed = TestLibFunctions.StoredLayoutChoiceEcho(fromCSharp);
        AssertEqual("detail 8", TestLibFunctions.StoredLayoutChoiceName(echoed), "a C#-built case returned unchanged");
    }

    public void TestAStructStoringThatEnumCrossesByAddress()
    {
        using var fromSwift = TestLibFunctions.MakeStoredLayoutChoiceHolder(5, 3);
        AssertEqual(3, (int)fromSwift.Count, "C# reads the returned Int field");
        AssertEqual("detail 5/3", TestLibFunctions.StoredLayoutChoiceHolderName(fromSwift), "Swift reads the returned struct back");

        using var echoed = TestLibFunctions.StoredLayoutChoiceHolderEcho(fromSwift);
        AssertEqual("detail 5/3", TestLibFunctions.StoredLayoutChoiceHolderName(echoed), "a by-value argument returned unchanged");

        using var none = StoredLayoutChoice.None;
        using var fromCSharp = new StoredLayoutChoiceHolder(none, 11);
        AssertEqual("none/11", TestLibFunctions.StoredLayoutChoiceHolderName(fromCSharp), "Swift reads a C#-built struct");
    }

    public void TestAnIndirectCaseKeepsTheEnumLoadable()
    {
        using var detail = TestLibFunctions.MakeStoredLayoutBoxedChoice(31);
        AssertEqual("boxed 31", TestLibFunctions.StoredLayoutBoxedChoiceName(detail), "Swift reads the returned payload case back");
        using var echoed = TestLibFunctions.StoredLayoutBoxedChoiceEcho(detail);
        AssertEqual("boxed 31", TestLibFunctions.StoredLayoutBoxedChoiceName(echoed), "a by-value argument returned unchanged");

        using var none = TestLibFunctions.MakeStoredLayoutBoxedChoice(0);
        AssertEqual("none", TestLibFunctions.StoredLayoutBoxedChoiceName(none), "Swift reads the returned empty case back");
    }

    public void TestAnAnyFieldCrossesByAddress()
    {
        using var number = TestLibFunctions.MakeStoredLayoutAnyCell(64);
        AssertEqual("int 64", TestLibFunctions.StoredLayoutAnyCellName(number), "Swift reads the returned Int box back");
        using var echoed = TestLibFunctions.StoredLayoutAnyCellEcho(number);
        AssertEqual("int 64", TestLibFunctions.StoredLayoutAnyCellName(echoed), "a by-value argument returned unchanged");

        using var text = TestLibFunctions.MakeStoredLayoutAnyTextCell("frozen");
        AssertEqual("text frozen", TestLibFunctions.StoredLayoutAnyCellName(text), "Swift reads the returned String box back");
    }

    public void TestAnOpaqueExistentialFieldCrossesByAddress()
    {
        using var fromSwift = TestLibFunctions.MakeStoredLayoutShapeCell(6, 2);
        AssertEqual(2, (int)fromSwift.Count, "C# reads the returned Int field");
        AssertEqual(6, (int)fromSwift.Shape.Sides, "C# reads through the returned existential");
        AssertEqual("6 sides/2", TestLibFunctions.StoredLayoutShapeCellName(fromSwift), "Swift reads the returned struct back");

        using var echoed = TestLibFunctions.StoredLayoutShapeCellEcho(fromSwift);
        AssertEqual("6 sides/2", TestLibFunctions.StoredLayoutShapeCellName(echoed), "a by-value argument returned unchanged");
    }

    public void TestAnUnownedGenericFieldSpecializedWithANativeClassCrossesInRegisters()
    {
        using var anchor = new StoredLayoutAnchor(40);
        using var fromSwift = TestLibFunctions.MakeStoredLayoutUnownedHolder(anchor, 2);
        AssertEqual(2, (int)fromSwift.Count, "C# reads the returned Int field");
        AssertEqual(42, (int)TestLibFunctions.StoredLayoutUnownedHolderTotal(fromSwift), "Swift reads the returned struct back");

        using var echoed = TestLibFunctions.StoredLayoutUnownedHolderEcho(fromSwift);
        AssertEqual(42, (int)TestLibFunctions.StoredLayoutUnownedHolderTotal(echoed), "a by-value argument returned unchanged");
        GC.KeepAlive(anchor);
    }

    public void TestAGenericFieldSpecializedWithAnIntCrossesInRegisters()
    {
        using var fromSwift = TestLibFunctions.MakeStoredLayoutWrappedNumber(7, 3);
        AssertEqual(3, (int)fromSwift.Count, "C# reads the returned Int field");
        AssertEqual(703, (int)TestLibFunctions.StoredLayoutWrappedNumberTotal(fromSwift), "Swift reads the returned struct back");

        using var echoed = TestLibFunctions.StoredLayoutWrappedNumberEcho(fromSwift);
        AssertEqual(703, (int)TestLibFunctions.StoredLayoutWrappedNumberTotal(echoed), "a by-value argument returned unchanged");
    }

    public void TestAGenericFieldSpecializedWithAStringCrossesInRegisters()
    {
        using var fromSwift = TestLibFunctions.MakeStoredLayoutWrappedText("wrap", 5);
        AssertEqual(5, (int)fromSwift.Count, "C# reads the Int field after the String");
        AssertEqual("wrap/5", TestLibFunctions.StoredLayoutWrappedTextName(fromSwift), "Swift reads the returned struct back");
        AssertEqual(405, (int)TestLibFunctions.StoredLayoutWrappedTextCount(fromSwift), "a thunk-eligible member reads the struct");

        using var echoed = TestLibFunctions.StoredLayoutWrappedTextEcho(fromSwift);
        AssertEqual("wrap/5", TestLibFunctions.StoredLayoutWrappedTextName(echoed), "a by-value argument returned unchanged");
    }
}
