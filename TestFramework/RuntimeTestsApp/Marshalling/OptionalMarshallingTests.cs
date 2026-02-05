// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using Swift.SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for optional type marshalling: Some/None for blittable/class, struct properties.
/// </summary>
public class OptionalMarshallingTests : TestBase
{
    public OptionalMarshallingTests(TestResults results) : base(results) { }

    #region Tier 1 — Smoke Tests

    [TestTier(TestTier.Tier1)]
    public void TestOptionalBlittableReturnSome()
    {
        var index = SwiftBindingsTestLib.FindIndex(new[] { 10, 20, 30 }, 20);
        AssertTrue(index.HasValue, "FindIndex found value");
        AssertEqual(1, index!.Value, "FindIndex returns correct index");
        TestLogger.Info($"FindIndex([10,20,30], 20) = {index}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestOptionalBlittableReturnNone()
    {
        var index = SwiftBindingsTestLib.FindIndex(new[] { 10, 20, 30 }, 99);
        AssertFalse(index.HasValue, "FindIndex returns null for missing value");
        TestLogger.Info("FindIndex returns null for missing value");
    }

    #endregion

    #region Tier 2 — Functional Tests

    [TestTier(TestTier.Tier2)]
    public void TestOptionalClassReturnSome()
    {
        var cat = SwiftBindingsTestLib.CreateAnimal("Cat", "Meow");
        var dog = SwiftBindingsTestLib.CreateAnimal("Dog", "Woof");

        var found = SwiftBindingsTestLib.FindAnimalByName(new[] { cat, dog }, "Cat");
        AssertNotNull(found, "FindAnimalByName found Cat");
        TestLogger.Info("FindAnimalByName returned a non-null result");
    }

    [TestTier(TestTier.Tier2)]
    public void TestOptionalClassReturnNone()
    {
        var cat = SwiftBindingsTestLib.CreateAnimal("Cat", "Meow");

        var found = SwiftBindingsTestLib.FindAnimalByName(new[] { cat }, "Parrot");
        AssertNull(found, "FindAnimalByName returns null for missing name");
        TestLogger.Info("FindAnimalByName returns null for missing name");
    }

    [TestTier(TestTier.Tier2)]
    public void TestOptionalParameterSome()
    {
        var result = SwiftBindingsTestLib.DescribeOptionalInt(42);
        AssertEqual("Value: 42", result, "DescribeOptionalInt with value");
        TestLogger.Info($"DescribeOptionalInt(42) = \"{result}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestOptionalParameterNone()
    {
        var result = SwiftBindingsTestLib.DescribeOptionalInt(null);
        AssertEqual("nil", result, "DescribeOptionalInt with null");
        TestLogger.Info($"DescribeOptionalInt(null) = \"{result}\"");
    }

    [TestTier(TestTier.Tier2)]
    public void TestOptionalConfigEffectiveLabel()
    {
        var config = new OptionalConfig(new SwiftString("Primary"), 10, "Fallback");
        var label = config.EffectiveLabel();
        AssertEqual("Primary", label, "EffectiveLabel with label");

        var configNoLabel = new OptionalConfig(null, null, "Fallback");
        var fallback = configNoLabel.EffectiveLabel();
        AssertEqual("Fallback", fallback, "EffectiveLabel without label");
        TestLogger.Info("OptionalConfig.EffectiveLabel tests passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestFindIndexFirstElement()
    {
        var index = SwiftBindingsTestLib.FindIndex(new[] { 5, 10, 15 }, 5);
        AssertTrue(index.HasValue, "FindIndex first element");
        AssertEqual(0, index!.Value, "First element index is 0");
        TestLogger.Info("FindIndex first element passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestFindIndexEmptyArray()
    {
        var index = SwiftBindingsTestLib.FindIndex(Array.Empty<int>(), 1);
        AssertFalse(index.HasValue, "FindIndex empty array returns null");
        TestLogger.Info("FindIndex empty array passed");
    }

    #endregion
}
