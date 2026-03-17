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

    [MonoJitCrash]
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
