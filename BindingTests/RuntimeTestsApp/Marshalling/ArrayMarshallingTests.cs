// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for SwiftArray marshalling: create, index, iterate, empty, round-trip, class arrays.
/// Array parameters accept IEnumerable&lt;T&gt;, returns are IReadOnlyList&lt;T&gt;.
/// </summary>
public class ArrayMarshallingTests : TestBase
{
    public ArrayMarshallingTests(TestResults results) : base(results) { }

    #region Tier 1 — Smoke Tests

    public void TestArrayParameterCount()
    {
        var count = TestLibFunctions.ArrayCount(new[] { 10, 20, 30 });
        AssertEqual(3, count, "Array count");
        TestLogger.Info($"ArrayCount([10,20,30]) = {count}");
    }

    public void TestArrayReturn()
    {
        var result = TestLibFunctions.CreateIntArray(3, 42);
        AssertEqual(3, result.Count, "Created array count");
        TestLogger.Info($"CreateIntArray(3, 42) returned {result.Count} elements");
    }

    public void TestEmptyArray()
    {
        AssertTrue(TestLibFunctions.IsEmptyArray(Array.Empty<int>()), "Empty array is empty");
        AssertEqual(0, TestLibFunctions.ArrayCount(Array.Empty<int>()), "Empty array count is 0");
        TestLogger.Info("Empty array tests passed");
    }

    #endregion

    #region Tier 2 — Functional Tests

    public void TestSumArray()
    {
        var sum = TestLibFunctions.SumArray(new[] { 1, 2, 3, 4, 5 });
        AssertEqual(15, sum, "Sum of [1..5]");
        TestLogger.Info($"SumArray([1,2,3,4,5]) = {sum}");
    }

    public void TestReverseIntArray()
    {
        var reversed = TestLibFunctions.ReverseIntArray(new[] { 1, 2, 3 });
        AssertEqual(3, reversed.Count, "Reversed count");
        AssertEqual(3, reversed[0], "Reversed[0]");
        AssertEqual(2, reversed[1], "Reversed[1]");
        AssertEqual(1, reversed[2], "Reversed[2]");
        TestLogger.Info("ReverseIntArray passed");
    }

    public void TestFilterPositive()
    {
        var filtered = TestLibFunctions.FilterPositive(new[] { -2, -1, 0, 1, 2, 3 });
        AssertEqual(3, filtered.Count, "Filtered count");
        AssertEqual(1, filtered[0], "Filtered[0]");
        AssertEqual(2, filtered[1], "Filtered[1]");
        AssertEqual(3, filtered[2], "Filtered[2]");
        TestLogger.Info("FilterPositive passed");
    }

    public void TestCreateStringArray()
    {
        var result = TestLibFunctions.CreateStringArray("hello", "world");
        AssertEqual(2, result.Count, "String array count");
        AssertEqual("hello", result[0].ToString(), "String array[0]");
        AssertEqual("world", result[1].ToString(), "String array[1]");
        TestLogger.Info("CreateStringArray passed");
    }

    public void TestArrayOfClasses()
    {
        var cat = TestLibFunctions.CreateAnimal("Cat", "Meow");
        var dog = TestLibFunctions.CreateAnimal("Dog", "Woof");
        var descriptions = TestLibFunctions.DescribeAnimals(new[] { cat, dog });
        AssertEqual(2, descriptions.Count, "Descriptions count");
        AssertTrue(descriptions[0].ToString().Contains("Cat"), "First is Cat");
        AssertTrue(descriptions[1].ToString().Contains("Dog"), "Second is Dog");
        TestLogger.Info("DescribeAnimals passed");
    }

    public void TestCreateIntArrayValues()
    {
        var result = TestLibFunctions.CreateIntArray(4, 7);
        AssertEqual(4, result.Count, "Created array count");
        for (int i = 0; i < result.Count; i++)
        {
            AssertEqual(7, result[i], $"Element[{i}]");
        }
        TestLogger.Info("CreateIntArray values verified");
    }

    public void TestSingleElementArray()
    {
        AssertEqual(1, TestLibFunctions.ArrayCount(new[] { 99 }), "Single element count");
        AssertEqual(99, TestLibFunctions.SumArray(new[] { 99 }), "Single element sum");
        AssertFalse(TestLibFunctions.IsEmptyArray(new[] { 99 }), "Single element not empty");
        TestLogger.Info("Single element array tests passed");
    }

    public void TestFilterPositiveAllNegative()
    {
        var filtered = TestLibFunctions.FilterPositive(new[] { -3, -2, -1 });
        AssertEqual(0, filtered.Count, "All negative filtered to empty");
        TestLogger.Info("FilterPositive all negative passed");
    }

    #endregion

    #region IEnumerable<NonFrozenStruct> sync round-trip
    // Regression coverage for bug-0.10.0-ienumerable-iswiftstruct-raw-intptr-… Defect A.
    // Pre-fix the generator emitted SwiftArray<IntPtr>.FromEnumerable(seq.Select(e =>
    // e.Payload.DangerousGetHandle())), which packed 1-word handles where Swift expected
    // contiguous Array<NonFrozenPoint> payload bytes — guaranteed wrong-bytes reads on
    // every call. Post-fix the projection emits SwiftArray<NonFrozenPoint>.FromEnumerable(seq)
    // and ISwiftObject.MarshalToSwift copies struct payloads by value via VWT.
    // The same flip applies to dictionary/set/optional containers of NonFrozenStruct
    // elements; the array shape is the canonical regression carrier.

    public void TestSumPointMagnitudesEmpty()
    {
        var sum = TestLibFunctions.SumPointMagnitudes(Array.Empty<NonFrozenPoint>());
        AssertEqual(0.0, sum, "Empty NonFrozenPoint array sum is 0");
        TestLogger.Info("SumPointMagnitudes([]) = 0");
    }

    public void TestSumPointMagnitudesPayloadByValue()
    {
        // Each point's distance-from-origin must equal sqrt(x² + y²) — pre-fix this
        // produced garbage because Swift dereferenced uninitialized payload bytes.
        var points = new[]
        {
            new NonFrozenPoint(3.0, 4.0),    // |p| = 5
            new NonFrozenPoint(0.0, 0.0),    // |p| = 0
            new NonFrozenPoint(6.0, 8.0),    // |p| = 10
        };
        var sum = TestLibFunctions.SumPointMagnitudes(points);
        AssertEqual(15.0, sum, "Sum of point magnitudes = 5 + 0 + 10");
        TestLogger.Info($"SumPointMagnitudes([(3,4),(0,0),(6,8)]) = {sum}");
    }

    public void TestScalePointsRoundTrip()
    {
        // Round-trip: SwiftArray<NonFrozenPoint> as parameter AND return.
        var points = new[]
        {
            new NonFrozenPoint(1.0, 2.0),
            new NonFrozenPoint(-3.0, 4.5),
        };
        var scaled = TestLibFunctions.ScalePoints(points, 2.0);
        AssertEqual(2, scaled.Count, "Scaled count = 2");
        AssertEqual(2.0, scaled[0].X, "scaled[0].X = 2.0");
        AssertEqual(4.0, scaled[0].Y, "scaled[0].Y = 4.0");
        AssertEqual(-6.0, scaled[1].X, "scaled[1].X = -6.0");
        AssertEqual(9.0, scaled[1].Y, "scaled[1].Y = 9.0");
        TestLogger.Info("ScalePoints round-trip preserved per-element values");
    }

    #endregion

    #region Pass 2 — O3: Array of Class Instances Property (TeamRoster)

    public void TestTeamRosterCreation()
    {
        var animals = new List<Animal>
        {
            new Animal("Rex", "Bark"),
            new Animal("Kitty", "Meow"),
        };
        var roster = new TeamRoster(animals);
        AssertNotNull(roster, "TeamRoster created");
        AssertEqual(2, roster.GetSize(), "TeamRoster size = 2");
        TestLogger.Info("TeamRoster creation passed");
    }

    public void TestTeamRosterMembersPropertyGet()
    {
        var animals = new List<Animal>
        {
            new Animal("Rex", "Bark"),
            new Animal("Kitty", "Meow"),
            new Animal("Birdy", "Tweet"),
        };
        var roster = new TeamRoster(animals);
        var members = roster.Members;
        AssertEqual(3, members.Count, "Members.Count = 3");
        AssertEqual("Rex", members[0].Name.ToString(), "Members[0].Name = Rex");
        AssertEqual("Kitty", members[1].Name.ToString(), "Members[1].Name = Kitty");
        AssertEqual("Birdy", members[2].Name.ToString(), "Members[2].Name = Birdy");
        TestLogger.Info("TeamRoster.Members getter passed");
    }

    #endregion
}
