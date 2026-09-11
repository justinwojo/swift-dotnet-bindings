// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Runtime coverage for an <c>inout</c> parameter in a closure's OWN signature —
/// <c>(inout [String: Any]) -&gt; Void</c> and friends. The question every test here asks is
/// the one a by-value bridge answers wrongly while still compiling: after the block returns,
/// does Swift see what the block left in the slot? A member that dropped the mutation would
/// pass a "the callback ran" assertion and fail every assertion below.
/// </summary>
public class InOutClosureParamTests : TestBase
{
    public InOutClosureParamTests(TestResults results) : base(results) { }

    /// <summary>Scalar carrier: the block's assignment is what Swift returns.</summary>
    public void TestScalarInOutMutationReachesSwift()
    {
        var carriers = new InOutClosureCarriers();
        var observed = -1;
        var result = carriers.AdjustCount(slot =>
        {
            observed = slot.Value;
            slot.Value = slot.Value + 5;
        });
        AssertEqual(7, observed, "Block observes the value Swift seeded");
        AssertEqual(12, result, "Swift observes the value the block left in the slot");
    }

    /// <summary>A block that never assigns must leave the seeded value untouched.</summary>
    public void TestScalarInOutWithoutAssignmentPreservesSeed()
    {
        var carriers = new InOutClosureCarriers();
        var result = carriers.AdjustCount(_ => { });
        AssertEqual(7, result, "An untouched slot writes the seeded value back unchanged");
    }

    /// <summary>Frozen-struct carrier — the whole value is replaced through the slot.</summary>
    public void TestStructInOutMutationReachesSwift()
    {
        var carriers = new InOutClosureCarriers();
        var observedWidth = -1;
        var result = carriers.AdjustViewport(slot =>
        {
            observedWidth = slot.Value.Width;
            slot.Value = new BuilderViewport(1024, 768);
        });
        AssertEqual(320, observedWidth, "Block observes the seeded struct");
        AssertEqual(1024, result.Width, "Swift observes the replaced struct width");
        AssertEqual(768, result.Height, "Swift observes the replaced struct height");
    }

    /// <summary>
    /// Memory-managed carrier. String is the one projection whose C# type is not the Swift
    /// value's own carrier, so both directions convert explicitly.
    /// </summary>
    public void TestStringInOutMutationReachesSwift()
    {
        var carriers = new InOutClosureCarriers();
        string? observed = null;
        var result = carriers.AdjustTitle(slot =>
        {
            observed = slot.Value;
            slot.Value = slot.Value + "-edited";
        });
        AssertEqual("seed", observed, "Block observes the seeded string");
        AssertEqual("seed-edited", result, "Swift observes the string the block left behind");
    }

    /// <summary>
    /// COW container carrier. The block mutates its own copy of the array; the bridge writes
    /// that copy back, so Swift's element count changes.
    /// </summary>
    public void TestArrayInOutMutationReachesSwift()
    {
        var carriers = new InOutClosureCarriers();
        var observedCount = -1;
        var result = carriers.AdjustTags(slot =>
        {
            observedCount = slot.Value.Count;
            slot.Value.Append(new SwiftString("two"));
        });
        AssertEqual(1, observedCount, "Block observes the seeded array");
        AssertEqual(2, result.Count, "Swift observes the appended element");
        AssertEqual("one", result[0], "Seeded element survives the round trip");
        AssertEqual("two", result[1], "Appended element reaches Swift");
    }

    /// <summary>Two slots plus a by-value sibling in one signature stay distinct.</summary>
    public void TestTwoInOutSlotsAndByValueSiblingAreDistinct()
    {
        var carriers = new InOutClosureCarriers();
        var byValue = -1;
        var result = carriers.AdjustPair((first, second, third) =>
        {
            byValue = second;
            first.Value = second;
            third.Value = 3;
        });
        AssertEqual(10, byValue, "By-value sibling arrives unchanged");
        // Swift returns first * 100 + second, so 10 and 3 must land in their own cells.
        AssertEqual(1003, result, "Each slot writes back to its own Swift storage");
    }

    /// <summary>The block's return value and its slot write-back are independent channels.</summary>
    public void TestInOutSlotAndBlockReturnValueBothReachSwift()
    {
        var carriers = new InOutClosureCarriers();
        var accepted = carriers.AdjustAndReport(slot => { slot.Value = 9; return true; });
        AssertEqual(9, accepted, "Accepted: Swift returns the mutated value");

        var rejected = carriers.AdjustAndReport(slot => { slot.Value = 9; return false; });
        AssertEqual(-9, rejected, "Rejected: Swift negates the same mutated value");
    }

    /// <summary>
    /// Escaping variant: the block is stored at install time and driven later, so the slot is
    /// constructed on a call the installing frame is no longer on.
    /// </summary>
    public void TestEscapingInOutBlockMutatesOnLaterCall()
    {
        var carriers = new InOutClosureCarriers();
        carriers.InstallAdjuster(slot => { slot.Value = slot.Value * 2; });
        AssertEqual(42, carriers.RunInstalledAdjuster(21), "Stored block mutates on a later drive");
        AssertEqual(8, carriers.RunInstalledAdjuster(4), "Stored block is reusable");
    }

    /// <summary>
    /// The slot is valid only while the block runs. Swift's <c>inout</c> storage can be the
    /// caller's stack, so a captured slot must refuse rather than read freed memory.
    /// </summary>
    public void TestCapturedSlotThrowsAfterBlockReturns()
    {
        var carriers = new InOutClosureCarriers();
        SwiftInOut<int>? captured = null;
        carriers.AdjustCount(slot => { captured = slot; slot.Value = 1; });
        AssertNotNull(captured, "Block received a slot");
        AssertThrows<InvalidOperationException>(
            () => { var _ = captured!.Value; },
            "Reading a captured slot after the block returned throws");
        AssertThrows<InvalidOperationException>(
            () => { captured!.Value = 2; },
            "Writing a captured slot after the block returned throws");
    }

    /// <summary>
    /// The reported builder family: the option bag arrives by reference and the returned
    /// dictionary is whatever the block left in it.
    /// </summary>
    public void TestDictionaryInOutBuilderSeedIsObservable()
    {
        var seeded = OptionsBuilderHost.ShowMapOptions();
        AssertEqual(1, seeded.Count, "Default-argument overload returns the seeded bag");
        AssertTrue(seeded.ContainsKey("mode"), "Seeded bag carries its key");
    }

    /// <summary>
    /// Removing through the slot is visible to Swift — the discriminator between a real
    /// write-back and a by-value bridge that returns the seed regardless.
    /// </summary>
    public void TestDictionaryInOutMutationReachesSwift()
    {
        var observedCount = -1;
        var result = OptionsBuilderHost.FocusOptions(slot =>
        {
            observedCount = slot.Value.Count;
            slot.Value.RemoveAll();
        });
        AssertEqual(1, observedCount, "Block observes the bag Swift seeded");
        AssertEqual(0, result.Count, "Swift observes the emptied bag");
    }

    /// <summary>A read-only sibling parameter rides alongside the slot unchanged.</summary>
    public void TestDictionaryInOutWithSiblingParameter()
    {
        var untouched = OptionsBuilderHost.AnimationOptions("fade", _ => { });
        AssertEqual(1, untouched.Count, "Untouched bag keeps what the sibling parameter seeded");

        var emptied = OptionsBuilderHost.AnimationOptions("fade", slot => slot.Value.RemoveAll());
        AssertEqual(0, emptied.Count, "Mutation through the slot still reaches Swift");
    }

    /// <summary>The instance-member and nested-namespace siblings bind on the same bridge.</summary>
    public void TestDictionaryInOutInstanceAndNestedSiblings()
    {
        var host = new OptionsBuilderHost();
        var marker = host.MarkerOptions(slot => slot.Value.RemoveAll());
        AssertEqual(0, marker.Count, "Instance-member builder writes back");

        var directions = OptionsBuilderHost.Builders.DirectionsOptions(_ => { });
        AssertEqual(1, directions.Count, "Nested-namespace builder seeds its bag");

        var emptied = OptionsBuilderHost.Builders.DirectionsOptions(slot => slot.Value.RemoveAll());
        AssertEqual(0, emptied.Count, "Nested-namespace builder writes back");
    }

    /// <summary>
    /// Insertion through a dictionary slot, on a carrier whose value type a consumer can build
    /// unaided. Removal alone could in principle be explained by a bridge that hands back an empty
    /// container; an inserted key that Swift then reads back cannot.
    /// </summary>
    public void TestDictionaryInOutInsertionReachesSwift()
    {
        var host = new DictionaryBuilderHost();
        var observed = -1;
        var result = host.Build(slot =>
        {
            observed = slot.Value["seed"];
            slot.Value["added"] = 41;
        });
        AssertEqual(1, observed, "Block observes the value Swift seeded");
        AssertEqual(2, result.Count, "Swift sees both the seeded and the inserted entry");
        AssertEqual(1, result["seed"], "Seeded entry survives the round trip");
        AssertEqual(41, result["added"], "Inserted entry reaches Swift");
    }

    /// <summary>
    /// Carriers deliberately left outside the admitted set stay tombstoned. A reference cell
    /// and an Optional must keep refusing rather than bind with a lossy read.
    /// </summary>
    public void TestRefusedInOutCarriersStayTombstoned()
    {
        var refused = new InOutClosureRefusedCarriers();
        // Deliberately invoking the SB0005 tombstones: the point is that the surface exists and
        // refuses at runtime. Suppressed only around these calls so an unintended tombstone call
        // elsewhere still fails the build.
#pragma warning disable SB0005
        AssertThrows<NotSupportedException>(
            () => refused.AdjustBox((object?)null),
            "An inout class carrier must stay refused");
        AssertThrows<NotSupportedException>(
            () => refused.AdjustMaybe((object?)null),
            "An inout Optional carrier must stay refused");
#pragma warning restore SB0005
    }
}
