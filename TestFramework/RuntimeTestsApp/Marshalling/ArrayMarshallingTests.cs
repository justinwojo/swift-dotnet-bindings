// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for SwiftArray marshalling: create, index, iterate, empty, round-trip, class arrays.
/// Array parameters accept IEnumerable&lt;T&gt;, returns are IReadOnlyList&lt;T&gt;.
/// </summary>
public class ArrayMarshallingTests : TestBase
{
    public ArrayMarshallingTests(TestResults results) : base(results) { }

    #region Tier 1 — Smoke Tests

    [TestTier(TestTier.Tier1)]
    public void TestArrayParameterCount()
    {
        var count = SwiftBindingsTestLib.GetArrayCount(new[] { 10, 20, 30 });
        AssertEqual(3, count, "Array count");
        TestLogger.Info($"ArrayCount([10,20,30]) = {count}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestArrayReturn()
    {
        var result = SwiftBindingsTestLib.CreateIntArray(3, 42);
        AssertEqual(3, result.Count, "Created array count");
        TestLogger.Info($"CreateIntArray(3, 42) returned {result.Count} elements");
    }

    [TestTier(TestTier.Tier1)]
    public void TestEmptyArray()
    {
        AssertTrue(SwiftBindingsTestLib.IsEmptyArray(Array.Empty<int>()), "Empty array is empty");
        AssertEqual(0, SwiftBindingsTestLib.GetArrayCount(Array.Empty<int>()), "Empty array count is 0");
        TestLogger.Info("Empty array tests passed");
    }

    #endregion

    #region Tier 2 — Functional Tests

    [TestTier(TestTier.Tier2)]
    public void TestSumArray()
    {
        var sum = SwiftBindingsTestLib.SumArray(new[] { 1, 2, 3, 4, 5 });
        AssertEqual(15, sum, "Sum of [1..5]");
        TestLogger.Info($"SumArray([1,2,3,4,5]) = {sum}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestReverseIntArray()
    {
        var reversed = SwiftBindingsTestLib.ReverseIntArray(new[] { 1, 2, 3 });
        AssertEqual(3, reversed.Count, "Reversed count");
        AssertEqual(3, reversed[0], "Reversed[0]");
        AssertEqual(2, reversed[1], "Reversed[1]");
        AssertEqual(1, reversed[2], "Reversed[2]");
        TestLogger.Info("ReverseIntArray passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestFilterPositive()
    {
        var filtered = SwiftBindingsTestLib.FilterPositive(new[] { -2, -1, 0, 1, 2, 3 });
        AssertEqual(3, filtered.Count, "Filtered count");
        AssertEqual(1, filtered[0], "Filtered[0]");
        AssertEqual(2, filtered[1], "Filtered[1]");
        AssertEqual(3, filtered[2], "Filtered[2]");
        TestLogger.Info("FilterPositive passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCreateStringArray()
    {
        var result = SwiftBindingsTestLib.CreateStringArray("hello", "world");
        AssertEqual(2, result.Count, "String array count");
        AssertEqual("hello", result[0].ToString(), "String array[0]");
        AssertEqual("world", result[1].ToString(), "String array[1]");
        TestLogger.Info("CreateStringArray passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestArrayOfClasses()
    {
        var cat = SwiftBindingsTestLib.CreateAnimal("Cat", "Meow");
        var dog = SwiftBindingsTestLib.CreateAnimal("Dog", "Woof");
        var descriptions = SwiftBindingsTestLib.GetDescribeAnimals(new[] { cat, dog });
        AssertEqual(2, descriptions.Count, "Descriptions count");
        AssertTrue(descriptions[0].ToString().Contains("Cat"), "First is Cat");
        AssertTrue(descriptions[1].ToString().Contains("Dog"), "Second is Dog");
        TestLogger.Info("DescribeAnimals passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCreateIntArrayValues()
    {
        var result = SwiftBindingsTestLib.CreateIntArray(4, 7);
        AssertEqual(4, result.Count, "Created array count");
        for (int i = 0; i < result.Count; i++)
        {
            AssertEqual(7, result[i], $"Element[{i}]");
        }
        TestLogger.Info("CreateIntArray values verified");
    }

    [TestTier(TestTier.Tier2)]
    public void TestSingleElementArray()
    {
        AssertEqual(1, SwiftBindingsTestLib.GetArrayCount(new[] { 99 }), "Single element count");
        AssertEqual(99, SwiftBindingsTestLib.SumArray(new[] { 99 }), "Single element sum");
        AssertFalse(SwiftBindingsTestLib.IsEmptyArray(new[] { 99 }), "Single element not empty");
        TestLogger.Info("Single element array tests passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestFilterPositiveAllNegative()
    {
        var filtered = SwiftBindingsTestLib.FilterPositive(new[] { -3, -2, -1 });
        AssertEqual(0, filtered.Count, "All negative filtered to empty");
        TestLogger.Info("FilterPositive all negative passed");
    }

    #endregion
}
