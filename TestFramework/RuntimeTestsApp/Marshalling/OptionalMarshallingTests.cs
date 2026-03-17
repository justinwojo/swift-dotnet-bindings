// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for optional type marshalling: Some/None for blittable/class, struct properties.
/// </summary>
public class OptionalMarshallingTests : TestBase
{
    public OptionalMarshallingTests(TestResults results) : base(results) { }

    #region Tier 1 — Smoke Tests

    public void TestOptionalBlittableReturnSome()
    {
        var index = TestLibFunctions.FindIndex(new[] { 10, 20, 30 }, 20);
        AssertTrue(index.HasValue, "FindIndex found value");
        AssertEqual(1, index!.Value, "FindIndex returns correct index");
        TestLogger.Info($"FindIndex([10,20,30], 20) = {index}");
    }

    [MonoJitCrash] // Mono: Optional<Int32> None marshalling returns Some incorrectly
    public void TestOptionalBlittableReturnNone()
    {
        var index = TestLibFunctions.FindIndex(new[] { 10, 20, 30 }, 99);
        AssertFalse(index.HasValue, "FindIndex returns null for missing value");
        TestLogger.Info("FindIndex returns null for missing value");
    }

    #endregion

    #region Tier 2 — Functional Tests

    public void TestOptionalClassReturnSome()
    {
        var cat = TestLibFunctions.CreateAnimal("Cat", "Meow");
        var dog = TestLibFunctions.CreateAnimal("Dog", "Woof");

        var found = TestLibFunctions.FindAnimalByName(new[] { cat, dog }, "Cat");
        AssertNotNull(found, "FindAnimalByName found Cat");
        TestLogger.Info("FindAnimalByName returned a non-null result");
    }

    public void TestOptionalClassReturnNone()
    {
        var cat = TestLibFunctions.CreateAnimal("Cat", "Meow");

        var found = TestLibFunctions.FindAnimalByName(new[] { cat }, "Parrot");
        AssertNull(found, "FindAnimalByName returns null for missing name");
        TestLogger.Info("FindAnimalByName returns null for missing name");
    }

    public void TestOptionalParameterSome()
    {
        var result = TestLibFunctions.DescribeOptionalInt(42);
        AssertEqual("Value: 42", result, "DescribeOptionalInt with value");
        TestLogger.Info($"DescribeOptionalInt(42) = \"{result}\"");
    }

    public void TestOptionalParameterNone()
    {
        var result = TestLibFunctions.DescribeOptionalInt(null);
        AssertEqual("nil", result, "DescribeOptionalInt with null");
        TestLogger.Info($"DescribeOptionalInt(null) = \"{result}\"");
    }

    [MonoJitCrash] // SwiftOptional<SwiftString>.GetTypeMetadata() hits Mono JIT assertion
    public void TestOptionalConfigConstructorWithLabel()
    {
        // Exercises NewSome(SwiftString) through frozen struct constructor
        var config = new OptionalConfig(new SwiftString("Primary"), 10, "Fallback");
        AssertEqual("Primary", config.Label, "Constructor sets String? label");
        AssertEqual(10, config.Count, "Constructor sets Int32? count");
        AssertEqual("Fallback", config.FallbackLabel, "Constructor sets fallbackLabel");
        TestLogger.Info("OptionalConfig constructor with label passed");
    }

    [MonoJitCrash] // SwiftOptional<SwiftString>.GetTypeMetadata() hits Mono JIT assertion
    public void TestOptionalConfigConstructorWithoutLabel()
    {
        var config = new OptionalConfig(null, null, "Default");
        AssertNull(config.Label, "Constructor with null label");
        AssertFalse(config.Count.HasValue, "Constructor with null count");
        AssertEqual("Default", config.FallbackLabel, "Constructor sets fallbackLabel");
        TestLogger.Info("OptionalConfig constructor without label passed");
    }

    [MonoJitCrash] // SwiftOptional<SwiftString>.GetTypeMetadata() hits Mono JIT assertion
    public void TestOptionalConfigEffectiveLabel()
    {
        var config = new OptionalConfig(new SwiftString("Primary"), 10, "Fallback");
        var label = config.GetEffectiveLabel();
        AssertEqual("Primary", label, "EffectiveLabel with label");

        var configNoLabel = new OptionalConfig(null, null, "Fallback");
        var fallback = configNoLabel.GetEffectiveLabel();
        AssertEqual("Fallback", fallback, "EffectiveLabel without label");
        TestLogger.Info("OptionalConfig.EffectiveLabel tests passed");
    }

    public void TestFindIndexFirstElement()
    {
        var index = TestLibFunctions.FindIndex(new[] { 5, 10, 15 }, 5);
        AssertTrue(index.HasValue, "FindIndex first element");
        AssertEqual(0, index!.Value, "First element index is 0");
        TestLogger.Info("FindIndex first element passed");
    }

    [MonoJitCrash] // Mono: Optional<Int32> None marshalling returns Some incorrectly
    public void TestFindIndexEmptyArray()
    {
        var index = TestLibFunctions.FindIndex(Array.Empty<int>(), 1);
        AssertFalse(index.HasValue, "FindIndex empty array returns null");
        TestLogger.Info("FindIndex empty array passed");
    }

    [MonoJitCrash] // SwiftOptional<SwiftString>.GetTypeMetadata() hits Mono JIT assertion
    public void TestOptionalStringPropertySetter()
    {
        var config = new OptionalConfig(null, null, "Fallback");
        config.Label = "Updated";
        AssertEqual("Updated", config.Label, "Label setter with String? Some");

        config.Label = null;
        AssertNull(config.Label, "Label setter with String? None");
        TestLogger.Info("OptionalStringPropertySetter tests passed");
    }

    // Fixed: Optional pointer wrapper passes full 16-byte Optional<String> via UnsafeRawPointer
    public void TestOptionalStringParameterSome()
    {
        var result = TestLibFunctions.DescribeOptionalString("hello");
        AssertEqual("Value: hello", result, "DescribeOptionalString with value");
        TestLogger.Info($"DescribeOptionalString(\"hello\") = \"{result}\"");
    }

    // Fixed: Optional pointer wrapper passes full 16-byte Optional<String> via UnsafeRawPointer
    public void TestOptionalStringParameterNone()
    {
        var result = TestLibFunctions.DescribeOptionalString(null);
        AssertEqual("nil", result, "DescribeOptionalString with null");
        TestLogger.Info($"DescribeOptionalString(null) = \"{result}\"");
    }

    #endregion
}
