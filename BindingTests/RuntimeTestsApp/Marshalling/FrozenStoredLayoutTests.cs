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

    // A stored class reference is a strong reference the value owns. Each holder below is built by
    // Swift and by C#, read back by Swift, checked to hold the very object C# gave it, and then
    // disposed, after which no counted object may still be alive: a value that never released its
    // reference, or released it twice, shows as a wrong count or a fault.

    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields, typeof(StoredLayoutObjectHolder.Buffer))]
    public void TestAStoredAnyObjectIsOwnedAndReleased()
    {
        AssertBufferMatchesSwift<StoredLayoutObjectHolder.Buffer>(TestLibFunctions.GetStoredLayoutObjectHolderLayout(), "count_", "AnyObject, Int");
        int baseline = StoredLayoutTrackedRef.LiveCount;

        using (var fromSwift = TestLibFunctions.MakeStoredLayoutObjectHolder(4, 9))
        {
            AssertEqual(baseline + 1, StoredLayoutTrackedRef.LiveCount, "the returned value keeps its object alive");
            AssertEqual("4/9", TestLibFunctions.StoredLayoutObjectHolderName(fromSwift), "Swift reads the returned value back");
            AssertEqual(9, (int)fromSwift.Count, "C# reads the field after the reference");
            using (var echoed = TestLibFunctions.StoredLayoutObjectHolderEcho(fromSwift))
                AssertEqual("4/9", TestLibFunctions.StoredLayoutObjectHolderName(echoed), "a by-value argument returned unchanged");
            AssertEqual(baseline + 1, StoredLayoutTrackedRef.LiveCount, "disposing the echoed copy releases only its own reference");
        }
        AssertEqual(baseline, StoredLayoutTrackedRef.LiveCount, "disposing the value releases the object it held");

        var item = new StoredLayoutTrackedRef(6);
        var replacement = new StoredLayoutTrackedRef(7);
        using (var fromCSharp = new StoredLayoutObjectHolder(item, 2))
        {
            AssertTrue(TestLibFunctions.StoredLayoutObjectHolderHolds(fromCSharp, item), "Swift sees the very object C# stored");
            AssertEqual("6/2", TestLibFunctions.StoredLayoutObjectHolderName(fromCSharp), "Swift reads a C#-built value");
            fromCSharp.Count = 3;
            AssertEqual("6/3", TestLibFunctions.StoredLayoutObjectHolderName(fromCSharp), "a C# write lands in the field Swift reads");

            // The reference field through its own accessors: the getter returns the stored object,
            // and a write replaces it, releasing the old reference and retaining the new one.
            object read = fromCSharp.Object;
            AssertTrue(TestLibFunctions.AnyObjectLaneSame(read, item), "the field getter returns the very object stored");
            (read as IDisposable)?.Dispose();
            fromCSharp.Object = replacement;
            AssertTrue(TestLibFunctions.StoredLayoutObjectHolderHolds(fromCSharp, replacement), "a C# write of the reference lands in the field Swift reads");
            AssertEqual("7/3", TestLibFunctions.StoredLayoutObjectHolderName(fromCSharp), "Swift reads the replaced reference");
            item.Dispose();
            AssertEqual(baseline + 1, StoredLayoutTrackedRef.LiveCount, "the write released the reference it replaced");
        }
        replacement.Dispose();
        AssertEqual(baseline, StoredLayoutTrackedRef.LiveCount, "the C#-built value released the object it retained");
    }

    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields, typeof(StoredLayoutTaggedHolder.Buffer))]
    public void TestAStoredClassBoundExistentialIsOwnedAndReleased()
    {
        AssertBufferMatchesSwift<StoredLayoutTaggedHolder.Buffer>(TestLibFunctions.GetStoredLayoutTaggedHolderLayout(), "count_", "any ClassBound, Int");
        int baseline = StoredLayoutTrackedRef.LiveCount;

        using (var fromSwift = TestLibFunctions.MakeStoredLayoutTaggedHolder(5, 7))
        {
            AssertEqual(baseline + 1, StoredLayoutTrackedRef.LiveCount, "the returned value keeps its object alive");
            AssertEqual("50/7", TestLibFunctions.StoredLayoutTaggedHolderName(fromSwift), "Swift calls through the stored witness table");
            using (var echoed = TestLibFunctions.StoredLayoutTaggedHolderEcho(fromSwift))
                AssertEqual("50/7", TestLibFunctions.StoredLayoutTaggedHolderName(echoed), "a by-value argument returned unchanged");
        }
        AssertEqual(baseline, StoredLayoutTrackedRef.LiveCount, "disposing the value releases the object it held");

        var item = new StoredLayoutTrackedRef(8);
        using (var fromCSharp = new StoredLayoutTaggedHolder(item, 1))
        {
            AssertTrue(TestLibFunctions.StoredLayoutTaggedHolderHolds(fromCSharp, item), "Swift sees the very object C# stored");
            AssertEqual("80/1", TestLibFunctions.StoredLayoutTaggedHolderName(fromCSharp), "Swift reads a C#-built value");
        }
        item.Dispose();
        AssertEqual(baseline, StoredLayoutTrackedRef.LiveCount, "the C#-built value released the object it retained");
    }

    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields, typeof(StoredLayoutLabelledHolder.Buffer))]
    public void TestAStoredClassBoundCompositionIsOwnedAndReleased()
    {
        AssertBufferMatchesSwift<StoredLayoutLabelledHolder.Buffer>(TestLibFunctions.GetStoredLayoutLabelledHolderLayout(), "count_", "any ClassBound & Labelled, Int");
        int baseline = StoredLayoutTrackedRef.LiveCount;

        using (var fromSwift = TestLibFunctions.MakeStoredLayoutLabelledHolder(3, 4))
        {
            AssertEqual(baseline + 1, StoredLayoutTrackedRef.LiveCount, "the returned value keeps its object alive");
            AssertEqual("ref3/30/4", TestLibFunctions.StoredLayoutLabelledHolderName(fromSwift), "Swift calls through both stored witness tables");
            using (var echoed = TestLibFunctions.StoredLayoutLabelledHolderEcho(fromSwift))
                AssertEqual("ref3/30/4", TestLibFunctions.StoredLayoutLabelledHolderName(echoed), "a by-value argument returned unchanged");
        }
        AssertEqual(baseline, StoredLayoutTrackedRef.LiveCount, "disposing the value releases the object it held");
    }

    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields, typeof(StoredLayoutErrorHolder.Buffer))]
    public void TestAStoredErrorIsOwnedAndReleased()
    {
        AssertBufferMatchesSwift<StoredLayoutErrorHolder.Buffer>(TestLibFunctions.GetStoredLayoutErrorHolderLayout(), "count_", "any Error, Int");
        int baseline = StoredLayoutTrackedRef.LiveCount;

        using (var fromSwift = TestLibFunctions.MakeStoredLayoutErrorHolder(12, 5))
        {
            AssertEqual(baseline + 1, StoredLayoutTrackedRef.LiveCount, "the boxed error keeps what it carries alive");
            AssertEqual("12/5", TestLibFunctions.StoredLayoutErrorHolderName(fromSwift), "Swift casts the stored error back");
            using (var echoed = TestLibFunctions.StoredLayoutErrorHolderEcho(fromSwift))
                AssertEqual("12/5", TestLibFunctions.StoredLayoutErrorHolderName(echoed), "a by-value argument returned unchanged");
        }
        AssertEqual(baseline, StoredLayoutTrackedRef.LiveCount, "disposing the value releases the error box");
    }

    public void TestATupleHoldingAClassIsOwnedAndReleased()
    {
        var parts = TestLibFunctions.GetStoredLayoutRefPairHolderLayout().Split('/');
        int size = int.Parse(parts[0]), stride = int.Parse(parts[1]);
        int managed = System.Runtime.CompilerServices.Unsafe.SizeOf<StoredLayoutRefPairHolder.Buffer>();
        AssertTrue(managed >= size && managed <= stride, $"(Ref, Int): the Buffer ({managed} bytes) spans Swift's size {size} and fits its stride {stride}");
        int baseline = StoredLayoutTrackedRef.LiveCount;

        using (var fromSwift = TestLibFunctions.MakeStoredLayoutRefPairHolder(2, 6))
        {
            AssertEqual(baseline + 1, StoredLayoutTrackedRef.LiveCount, "the returned value keeps its object alive");
            AssertEqual("2/6", TestLibFunctions.StoredLayoutRefPairHolderName(fromSwift), "Swift reads the tuple back");
            using (var echoed = TestLibFunctions.StoredLayoutRefPairHolderEcho(fromSwift))
                AssertEqual("2/6", echoed.Summary, "a by-value argument returned unchanged");
        }
        AssertEqual(baseline, StoredLayoutTrackedRef.LiveCount, "disposing the value releases the object it held");

        var item = new StoredLayoutTrackedRef(9);
        using (var fromCSharp = new StoredLayoutRefPairHolder(item, 4))
        {
            AssertTrue(fromCSharp.Holds(item), "Swift sees the very object C# stored");
            AssertEqual("9/4", fromCSharp.Summary, "Swift reads a C#-built value");
        }
        item.Dispose();
        AssertEqual(baseline, StoredLayoutTrackedRef.LiveCount, "the C#-built value released the object it retained");
    }

    /// <summary>
    /// The managed Buffer against Swift's own <c>MemoryLayout</c> (size/stride/offset): at least the
    /// value's size and no wider than its stride, and the named member at Swift's offset of that field.
    /// </summary>
    private void AssertBufferMatchesSwift<TBuffer>(string swiftLayout, string member, string shape) where TBuffer : struct
    {
        var parts = swiftLayout.Split('/');
        int size = int.Parse(parts[0]), stride = int.Parse(parts[1]), offset = int.Parse(parts[2]);
        int managed = System.Runtime.CompilerServices.Unsafe.SizeOf<TBuffer>();
        AssertTrue(managed >= size && managed <= stride, $"{shape}: the Buffer ({managed} bytes) spans Swift's size {size} and fits its stride {stride}");
        AssertEqual(offset, (int)System.Runtime.InteropServices.Marshal.OffsetOf<TBuffer>(member), $"{shape}: '{member}' sits at Swift's offset");
    }

    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields, typeof(StoredLayoutWrappedFlags.Buffer))]
    public void TestASpecializationFollowedBySubWordFieldsKeepsSwiftsOffsets()
    {
        AssertBufferMatchesSwift<StoredLayoutWrappedFlags.Buffer>(TestLibFunctions.GetStoredLayoutWrappedFlagsLayout(), "tail_", "Wrap<Int>, UInt8?, UInt8");

        using var fromSwift = TestLibFunctions.MakeStoredLayoutWrappedFlags(7, 9, 200);
        AssertEqual("7/9/200", TestLibFunctions.StoredLayoutWrappedFlagsName(fromSwift), "Swift reads every field of the returned struct");
        AssertEqual((byte)200, fromSwift.Tail, "C# reads the tail after the one-byte Optional");
        AssertEqual((byte)9, fromSwift.Flag, "C# reads the one-byte Optional");

        using var none = TestLibFunctions.MakeStoredLayoutWrappedFlags(-3, null, 1);
        AssertEqual("-3/nil/1", TestLibFunctions.StoredLayoutWrappedFlagsName(none), "an empty Optional between two fields");

        using var echoed = TestLibFunctions.StoredLayoutWrappedFlagsEcho(fromSwift);
        AssertEqual("7/9/200", TestLibFunctions.StoredLayoutWrappedFlagsName(echoed), "a by-value argument returned unchanged");

        var bumped = TestLibFunctions.MakeStoredLayoutWrappedFlags(7, 9, 200);
        TestLibFunctions.StoredLayoutWrappedFlagsBump(ref bumped);
        AssertEqual("7/9/201", TestLibFunctions.StoredLayoutWrappedFlagsName(bumped), "an inout argument written back in place");
        bumped.BumpTail();
        AssertEqual((byte)202, bumped.Tail, "a mutating method writes the tail Swift reads");
        bumped.Dispose();
    }

    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields, typeof(StoredLayoutTextFlags.Buffer))]
    public void TestAStringFollowedBySubWordFieldsKeepsSwiftsOffsets()
    {
        AssertBufferMatchesSwift<StoredLayoutTextFlags.Buffer>(TestLibFunctions.GetStoredLayoutTextFlagsLayout(), "tail_", "String, Int16?, UInt8");

        using var fromSwift = TestLibFunctions.MakeStoredLayoutTextFlags("flags", -12, 77);
        AssertEqual("flags/-12/77", TestLibFunctions.StoredLayoutTextFlagsName(fromSwift), "Swift reads every field of the returned struct");
        AssertEqual((short)-12, fromSwift.Flag, "C# reads the Optional after the String");
        AssertEqual((byte)77, fromSwift.Tail, "C# reads the tail after the Optional's tag byte");

        using var fromCSharp = new StoredLayoutTextFlags("built", null, 5);
        AssertEqual("built/nil/5", TestLibFunctions.StoredLayoutTextFlagsName(fromCSharp), "Swift reads a C#-built struct");
        using var echoed = TestLibFunctions.StoredLayoutTextFlagsEcho(fromCSharp);
        AssertEqual("built/nil/5", TestLibFunctions.StoredLayoutTextFlagsName(echoed), "a by-value argument returned unchanged");
    }

    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicFields, typeof(StoredLayoutPaddedText.Buffer))]
    public void TestPaddingAheadOfAnAlignedInnerStructKeepsSwiftsOffsets()
    {
        AssertBufferMatchesSwift<StoredLayoutPaddedText.Buffer>(TestLibFunctions.GetStoredLayoutPaddedTextLayout(), "text_0_", "UInt8, { UInt8, Int }, String");

        using var fromSwift = TestLibFunctions.MakeStoredLayoutPaddedText(1, 2, 300, "padded");
        AssertEqual("1/2/300/padded", TestLibFunctions.StoredLayoutPaddedTextName(fromSwift), "Swift reads every field of the returned struct");
        AssertEqual("padded", fromSwift.Text, "C# reads the String after the inner struct");

        using var echoed = TestLibFunctions.StoredLayoutPaddedTextEcho(fromSwift);
        AssertEqual("1/2/300/padded", TestLibFunctions.StoredLayoutPaddedTextName(echoed), "a by-value argument returned unchanged");
    }
}
