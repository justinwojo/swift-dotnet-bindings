// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;
using Swift;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Existential boxing tests — verifies concrete types conforming to a protocol
/// can be passed as existential parameters (any Protocol) to Swift functions
/// and class constructors.
///
/// SimpleMode/StrictMode are Swift structs tested directly for construction +
/// method calls. ModeProcessor, Pipeline, and free functions test existential
/// boxing through C# implementations of IProcessingMode.
///
/// Tier structure:
/// - Tier 1: Swift type construction + blittable methods
/// - Tier 2: Validate methods, ModeProcessor with existential, Pipeline, free functions
/// </summary>
public class ExistentialBoxingTests : TestBase
{
    public ExistentialBoxingTests(TestResults results) : base(results) { }

    #region Construction + Properties (Tier 1)

    public void TestSimpleModeConstruction()
    {
        var mode = new SimpleMode();
        AssertNotNull(mode, "SimpleMode constructed");
        TestLogger.Info("SimpleMode() construction passed");
    }

    public void TestSimpleModeModeName()
    {
        var mode = new SimpleMode();
        var name = mode.ModeName;
        AssertEqual("simple", name, "SimpleMode.ModeName");
        TestLogger.Info($"SimpleMode.ModeName = \"{name}\"");
    }

    public void TestStrictModeConstruction()
    {
        var mode = new StrictMode();
        AssertNotNull(mode, "StrictMode constructed");
        TestLogger.Info("StrictMode() construction passed");
    }

    public void TestStrictModeModeName()
    {
        var mode = new StrictMode();
        var name = mode.ModeName;
        AssertEqual("strict", name, "StrictMode.ModeName");
        TestLogger.Info($"StrictMode.ModeName = \"{name}\"");
    }

    #endregion

    #region Validate Methods — Direct Swift Type (Tier 2)

    public void TestSimpleModeValidatePositive()
    {
        var mode = new SimpleMode();
        AssertTrue(mode.Validate(1), "SimpleMode.Validate(1) should be true");
        AssertTrue(mode.Validate(100), "SimpleMode.Validate(100) should be true");
        TestLogger.Info("SimpleMode.Validate positive values passed");
    }

    public void TestSimpleModeValidateZeroAndNegative()
    {
        var mode = new SimpleMode();
        // SimpleMode: input >= 0, so 0 is true and -1 is false
        AssertTrue(mode.Validate(0), "SimpleMode.Validate(0) should be true (>= 0)");
        AssertFalse(mode.Validate(-1), "SimpleMode.Validate(-1) should be false (< 0)");
        TestLogger.Info("SimpleMode.Validate zero/negative passed");
    }

    public void TestStrictModeValidatePositive()
    {
        var mode = new StrictMode();
        AssertTrue(mode.Validate(1), "StrictMode.Validate(1) should be true");
        AssertTrue(mode.Validate(50), "StrictMode.Validate(50) should be true");
        TestLogger.Info("StrictMode.Validate positive values passed");
    }

    public void TestStrictModeValidateZeroAndNegative()
    {
        var mode = new StrictMode();
        AssertFalse(mode.Validate(0), "StrictMode.Validate(0) should be false");
        AssertFalse(mode.Validate(-1), "StrictMode.Validate(-1) should be false");
        TestLogger.Info("StrictMode.Validate zero/negative passed");
    }

    public void TestStrictModeValidateBoundary()
    {
        var mode = new StrictMode();
        AssertTrue(mode.Validate(999), "StrictMode.Validate(999) should be true");
        AssertFalse(mode.Validate(1000), "StrictMode.Validate(1000) should be false");
        TestLogger.Info("StrictMode boundary validation passed");
    }

    #endregion

    #region ModeProcessor — Existential Boxing (Tier 3 — ProcessingModeProxy triggers Mono JIT crash)

    [MonoJitCrash] // ProcessingModeProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestModeProcessorWithSimpleImpl()
    {
        var impl = new TestSimpleProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var processor = new ModeProcessor(proxy);
        AssertTrue(processor.Process(42), "ModeProcessor.Process(42) with simple impl");
        TestLogger.Info("ModeProcessor with simple impl passed");
    }

    [MonoJitCrash] // ProcessingModeProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestModeProcessorWithStrictImpl()
    {
        var impl = new TestStrictProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var processor = new ModeProcessor(proxy);
        AssertTrue(processor.Process(1), "ModeProcessor.Process(1) with strict impl");
        AssertFalse(processor.Process(-1), "ModeProcessor.Process(-1) with strict impl");
        TestLogger.Info("ModeProcessor with strict impl passed");
    }

    [MonoJitCrash] // ProcessingModeProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestModeProcessorGetModeName()
    {
        var impl = new TestSimpleProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var processor = new ModeProcessor(proxy);
        var name = processor.GetModeName();
        AssertEqual("simple", name, "ModeProcessor.GetModeName() with simple impl");
        TestLogger.Info($"ModeProcessor.GetModeName() = \"{name}\"");
    }

    #endregion

    #region Pipeline — Array + Existential Constructor (Tier 3)

    [MonoJitCrash] // ProcessingModeProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestPipelineConstruction()
    {
        var impl = new TestSimpleProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var pipeline = new Pipeline(new[] { 1, 2, 3 }, proxy);
        AssertNotNull(pipeline, "Pipeline constructed");
        TestLogger.Info("Pipeline construction passed");
    }

    [MonoJitCrash] // ProcessingModeProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestPipelineGetStepCount()
    {
        var impl = new TestSimpleProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var pipeline = new Pipeline(new[] { 10, 20, 30, 40 }, proxy);
        AssertEqual(4, pipeline.GetStepCount(), "Pipeline.GetStepCount()");
        TestLogger.Info($"Pipeline.GetStepCount() = {pipeline.GetStepCount()}");
    }

    [MonoJitCrash] // ProcessingModeProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestPipelineGetModeName()
    {
        var impl = new TestStrictProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var pipeline = new Pipeline(new[] { 1, 2 }, proxy);
        var name = pipeline.GetModeName();
        AssertEqual("strict", name, "Pipeline.GetModeName() with strict impl");
        TestLogger.Info($"Pipeline.GetModeName() = \"{name}\"");
    }

    #endregion

    #region Free Functions — Existential Parameters (Tier 3)

    [MonoJitCrash] // ProcessingModeProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestRunWithModeSimple()
    {
        var impl = new TestSimpleProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var result = TestLibFunctions.RunWithMode(proxy, 42);
        AssertTrue(result, "RunWithMode(simple, 42)");
        TestLogger.Info($"RunWithMode(simple, 42) = {result}");
    }

    [MonoJitCrash] // ProcessingModeProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestRunWithModeStrict()
    {
        var impl = new TestStrictProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        AssertTrue(TestLibFunctions.RunWithMode(proxy, 10), "RunWithMode(strict, 10)");
        AssertFalse(TestLibFunctions.RunWithMode(proxy, -5), "RunWithMode(strict, -5)");
        TestLogger.Info("RunWithMode with strict impl passed");
    }

    [MonoJitCrash] // ProcessingModeProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestCompareResultsSameMode()
    {
        var a = new ProcessingModeProxy(new TestSimpleProcessingMode());
        var b = new ProcessingModeProxy(new TestSimpleProcessingMode());
        var result = TestLibFunctions.CompareResults(a, b, 42);
        AssertTrue(result, "CompareResults(simple, simple, 42) should agree");
        TestLogger.Info($"CompareResults(simple, simple, 42) = {result}");
    }

    [MonoJitCrash] // ProcessingModeProxy(impl) calls EveryProtocol.GetTypeMetadata() → Mono JIT assertion
    public void TestCompareResultsDifferentModes()
    {
        var simple = new ProcessingModeProxy(new TestSimpleProcessingMode());
        var strict = new ProcessingModeProxy(new TestStrictProcessingMode());
        // Simple.Validate(0) = true, Strict.Validate(0) = false → disagree
        var result = TestLibFunctions.CompareResults(simple, strict, 0);
        AssertFalse(result, "CompareResults(simple, strict, 0) should disagree");
        TestLogger.Info($"CompareResults(simple, strict, 0) = {result}");
    }

    #endregion

    #region Pass 2 — N1: Protocol Default Implementation (ConfigurableItem)

    public void TestConfigurableItemUsesDefault()
    {
        var item = new ConfigurableItem("WiFi");
        AssertEqual("WiFi", item.ConfigName, "ConfigName property");
        // Protocol default: ConfigurableItem doesn't emit Configure() — it's on the interface
        // with throw NotSupportedException. Test that the type conforms to IConfigurable.
        IConfigurable iface = item;
        AssertNotNull(iface, "ConfigurableItem implements IConfigurable");
        TestLogger.Info("ConfigurableItem uses protocol default (not directly callable)");
    }

    public void TestCustomConfigItemOverrides()
    {
        var item = new CustomConfigItem("WiFi");
        // CustomConfigItem overrides the protocol default, so Configure() is emitted
        var config = item.Configure();
        AssertEqual("Custom: WiFi", config, "Custom configure override");
        TestLogger.Info($"CustomConfigItem.Configure = {config}");
    }

    #endregion

    #region Pass 2 — N2: Protocol with Existential Parameters (ModeConsumer)

    public void TestRunModeConsumerWithSimpleMode()
    {
        var consumer = new SimpleModeConsumer();
        var mode = new SimpleMode();
        var result = TestLibFunctions.RunModeConsumer(consumer, mode);
        AssertEqual("Consumed: simple", result, "RunModeConsumer with SimpleMode");
        TestLogger.Info($"RunModeConsumer(SimpleModeConsumer, SimpleMode) = {result}");
    }

    public void TestRunModeConsumerWithStrictMode()
    {
        var consumer = new SimpleModeConsumer();
        var mode = new StrictMode();
        var result = TestLibFunctions.RunModeConsumer(consumer, mode);
        AssertEqual("Consumed: strict", result, "RunModeConsumer with StrictMode");
        TestLogger.Info($"RunModeConsumer(SimpleModeConsumer, StrictMode) = {result}");
    }

    #endregion

    #region Pass 2 — N3: Multiple Protocol Conformance (MultiProtocolEntity)

    public void TestMultiProtocolEntityCreation()
    {
        var entity = new MultiProtocolEntity("e1", "TestEntity");
        AssertNotNull(entity, "MultiProtocolEntity created");
        AssertEqual("e1", entity.Id.ToString(), "Entity Id");
        AssertEqual("TestEntity", entity.Name.ToString(), "Entity Name");
        TestLogger.Info("MultiProtocolEntity creation passed");
    }

    public void TestMultiProtocolEntityDescribe()
    {
        var entity = new MultiProtocolEntity("e2", "MyEntity");
        var desc = entity.GetDescribe();
        AssertTrue(desc.Contains("e2"), "Describe contains id");
        AssertTrue(desc.Contains("MyEntity"), "Describe contains name");
        TestLogger.Info($"MultiProtocolEntity.Describe = {desc}");
    }

    #endregion

    #region Pass 2 — N4: Marker Protocol (TaggedItem)

    public void TestTaggedItemCreation()
    {
        var item = new TaggedItem("important");
        AssertNotNull(item, "TaggedItem created");
        AssertEqual("important", item.Tag.ToString(), "TaggedItem.Tag");
        TestLogger.Info("TaggedItem creation passed");
    }

    #endregion

    #region Pass 2 — AB2: 3-Level Protocol Chain (LengthRule)

    public void TestLengthRuleCreation()
    {
        var rule = new LengthRule("maxlen", 2, 10);
        AssertEqual("maxlen", rule.RuleName.ToString(), "LengthRule.RuleName");
        AssertEqual(2, rule.StrictLevel, "LengthRule.StrictLevel");
        TestLogger.Info("LengthRule creation passed");
    }

    public void TestLengthRuleValidation()
    {
        var rule = new LengthRule("maxlen", 1, 5);
        var valid = rule.Validate("hi");
        AssertTrue(valid, "Short string validates");
        var invalid = rule.Validate("toolongstring");
        AssertFalse(invalid, "Long string fails validation");
        TestLogger.Info("LengthRule validation passed");
    }

    #endregion
}

/// <summary>
/// C# implementation of IProcessingMode — simple mode (accepts non-negative).
/// </summary>
internal class TestSimpleProcessingMode : IProcessingMode
{
    public string ModeName => "simple";
    public bool Validate(int input) => input >= 0;
}

/// <summary>
/// C# implementation of IProcessingMode — strict mode (accepts 0 < x < 1000).
/// </summary>
internal class TestStrictProcessingMode : IProcessingMode
{
    public string ModeName => "strict";
    public bool Validate(int input) => input > 0 && input < 1000;
}
