// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
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

    #region CollectionProcessor (Array/Set as natural C# overloads)

    public void TestCollectionProcessorArrayOverload()
    {
        // process(items: [String]) → Process(IEnumerable<string>)
        using var processor = new CollectionProcessor();
        var result = processor.Process(new[] { "a", "b", "c" });
        AssertTrue(result.Contains("array:"), "Array overload returns array-prefixed result");
        AssertTrue(result.Contains("a"), "Array result contains items");
    }

    public void TestCollectionProcessorSetOverload()
    {
        // process(unique: Set<String>) → Process(IReadOnlySet<string>).
        // Set parameters now project as `IReadOnlySet<T>` (was
        // `IEnumerable<T>` pre-fix), so the array and set overloads no longer collide
        // on their C# signature — both are emitted as natural `Process` overloads
        // disambiguated by parameter type.
        using var processor = new CollectionProcessor();
        var result = processor.Process(new HashSet<string> { "x", "y", "z" });
        AssertTrue(result.Contains("set:"), "Set overload returns set-prefixed result");
    }

    // [DynamicDependency] preserves the Set-typed overload through NativeAOT trimming.
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicMethods,
        typeof(CollectionProcessor))]
    public void TestCollectionProcessorBothOverloadsExist()
    {
        // Verify both overloads exist — compile-time proof that the pre-fix
        // collision is gone, and they're emitted as natural C# overloads.
        using var processor = new CollectionProcessor();
        var arrayOverload = typeof(CollectionProcessor).GetMethod(
            "Process", new[] { typeof(IEnumerable<string>) });
        var setOverload = typeof(CollectionProcessor).GetMethod(
            "Process", new[] { typeof(IReadOnlySet<string>) });
        AssertNotNull(arrayOverload, "Process(IEnumerable<string>) exists");
        AssertNotNull(setOverload, "Process(IReadOnlySet<string>) exists");
    }

    #endregion

    #region Free Function Collision (ModuleHandler path)

    public void TestTransformCollectionArrayOverload()
    {
        // Free function: transformCollection(items: [String]) → TransformCollection(IEnumerable<string>)
        var result = TestLibFunctions.TransformCollection(new[] { "a", "b" });
        AssertTrue(result.Contains("array:"), "Free function array overload works");
    }

    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicMethods,
        typeof(TestLibFunctions))]
    public void TestTransformCollectionBothOverloadsExist()
    {
        // Array and set overloads now project to distinct C# types
        // (`IEnumerable<string>` vs `IReadOnlySet<string>`) and emit as natural overloads
        // rather than collide and require a `2` suffix.
        var arrayOverload = typeof(TestLibFunctions).GetMethod(
            "TransformCollection", new[] { typeof(IEnumerable<string>) });
        var setOverload = typeof(TestLibFunctions).GetMethod(
            "TransformCollection", new[] { typeof(IReadOnlySet<string>) });
        AssertNotNull(arrayOverload, "TransformCollection(IEnumerable<string>) exists");
        AssertNotNull(setOverload, "TransformCollection(IReadOnlySet<string>) exists");
    }

    public void TestTransformCollectionSetOverload()
    {
        // Free function: transformCollection(unique: Set<String>) → TransformCollection(IReadOnlySet<string>)
        var result = TestLibFunctions.TransformCollection(new HashSet<string> { "x", "y" });
        AssertTrue(result.Contains("set:"), "Free function set overload works");
    }

    #endregion
}
