// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Tests for method overload collision disambiguation.
/// Verifies that Swift overloads projecting to the same C# signature
/// are disambiguated with numeric suffixes instead of being skipped.
/// </summary>
public class ClosureOverloadCollisionTests : TestBase
{
    public ClosureOverloadCollisionTests(TestResults results) : base(results) { }

    #region CollectionProcessor (Array/Set → IEnumerable collision)

    public void TestCollectionProcessorArrayOverload()
    {
        // First overload: process(items: [String]) → Process(IEnumerable<string>)
        // This is the first occurrence — no suffix.
        using var processor = new CollectionProcessor();
        var result = processor.Process(new[] { "a", "b", "c" });
        AssertTrue(result.Contains("array:"), "Array overload returns array-prefixed result");
        AssertTrue(result.Contains("a"), "Array result contains items");
    }

    [Skip("SwiftSet<SwiftString>.FromEnumerable crash — pre-existing Set marshalling issue")]
    public void TestCollectionProcessorSetOverload()
    {
        // Second overload: process(unique: Set<String>) → Process2(IEnumerable<string>)
        // Disambiguated with suffix "2".
        using var processor = new CollectionProcessor();
        var result = processor.Process2(new[] { "x", "y", "z" });
        AssertTrue(result.Contains("set:"), "Set overload (Process2) returns set-prefixed result");
    }

    public void TestCollectionProcessorDisambiguatedMethodExists()
    {
        // Verify Process2 exists as a method — compile-time proof that
        // disambiguation emitted the previously-skipped overload.
        using var processor = new CollectionProcessor();
        var method = processor.GetType().GetMethod("Process2");
        AssertNotNull(method, "Process2 method exists on CollectionProcessor (disambiguation worked)");
    }

    #endregion

    #region Free Function Collision (ModuleHandler path)

    public void TestTransformCollectionArrayOverload()
    {
        // Free function: transformCollection(items: [String]) → TransformCollection(IEnumerable<string>)
        var result = TestLibFunctions.TransformCollection(new[] { "a", "b" });
        AssertTrue(result.Contains("array:"), "Free function array overload works");
    }

    public void TestTransformCollectionDisambiguatedMethodExists()
    {
        // Verify TransformCollection2 exists as a method on the Functions class
        var method = typeof(TestLibFunctions).GetMethod("TransformCollection2");
        AssertNotNull(method, "TransformCollection2 method exists (free function disambiguation worked)");
    }

    [Skip("SwiftSet<SwiftString>.FromEnumerable crash — pre-existing Set marshalling issue")]
    public void TestTransformCollectionSetOverload()
    {
        // Free function: transformCollection(unique: Set<String>) → TransformCollection2(IEnumerable<string>)
        var result = TestLibFunctions.TransformCollection2(new[] { "x", "y" });
        AssertTrue(result.Contains("set:"), "Free function set overload (TransformCollection2) works");
    }

    #endregion
}
