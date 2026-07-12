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

    // Fixed: EveryProtocol now uses real Swift objects + proper metadata
    public void TestModeProcessorWithSimpleImpl()
    {
        var impl = new TestSimpleProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var processor = new ModeProcessor(proxy);
        AssertTrue(processor.Process(42), "ModeProcessor.Process(42) with simple impl");
        TestLogger.Info("ModeProcessor with simple impl passed");
    }

    // Fixed: EveryProtocol now uses real Swift objects + proper metadata
    public void TestModeProcessorWithStrictImpl()
    {
        var impl = new TestStrictProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var processor = new ModeProcessor(proxy);
        AssertTrue(processor.Process(1), "ModeProcessor.Process(1) with strict impl");
        AssertFalse(processor.Process(-1), "ModeProcessor.Process(-1) with strict impl");
        TestLogger.Info("ModeProcessor with strict impl passed");
    }

    // String return now uses Utf8Slice encoding (avoids ARC issues with SwiftString)
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

    #region Stateful Conformer — Direct Construction Controls (no existential boxing)

    // Controls that isolate a RichMode/OpenMode/ClassMode construction+field-storage bug from
    // an existential-boxing bug: these call the conformer's own methods directly (no `any P`
    // parameter, no owns=true box). If these pass but the boxed variants below fail, the
    // defect is purely in the existential payload marshalling. RichMode/OpenMode/ClassMode all
    // use a parameterless init with defaulted non-trivial state — mirroring CryptoSwift's ECB
    // (init(); options + customBlockSize: Int?) — so they never trip the parameterized-init
    // wrapper path and pin the box marshalling in isolation.

    public void TestRichModeDirectConstruction()
    {
        var mode = new RichMode();
        AssertEqual("rich", mode.ModeName.ToString(), "RichMode.ModeName direct");
        AssertTrue(mode.Validate(50), "RichMode.Validate(50) direct within [10,100]");
        AssertFalse(mode.Validate(5), "RichMode.Validate(5) direct below threshold");
        AssertFalse(mode.Validate(150), "RichMode.Validate(150) direct above ceiling");
        TestLogger.Info("RichMode direct construction + validate passed");
    }

    public void TestOpenModeDirectConstruction()
    {
        var mode = new OpenMode();
        AssertEqual("open", mode.ModeName.ToString(), "OpenMode.ModeName direct");
        AssertTrue(mode.Validate(10), "OpenMode.Validate(10) direct at threshold");
        AssertTrue(mode.Validate(9999), "OpenMode.Validate(9999) direct (nil ceiling)");
        AssertFalse(mode.Validate(9), "OpenMode.Validate(9) direct below threshold");
        TestLogger.Info("OpenMode direct construction + validate passed");
    }

    public void TestClassModeDirectConstruction()
    {
        var mode = new ClassMode();
        AssertEqual("cls", mode.ModeName.ToString(), "ClassMode.ModeName direct");
        AssertTrue(mode.Validate(5), "ClassMode.Validate(5) direct");
        AssertFalse(mode.Validate(4), "ClassMode.Validate(4) direct");
        TestLogger.Info("ClassMode direct construction + validate passed");
    }

    #endregion

    #region ModeProcessor — Boxed Swift Struct/Class Conformer (owns=true existential box)

    // These construct ModeProcessor with a *same-module Swift conformer* passed directly as
    // `any ProcessingMode`, rather than a C#-side ProcessingModeProxy. A Swift value-type
    // conformer marshals through the IExistentialBoxable owns=true path (BoxAsExistential1 →
    // MarshalPayload → owns-gated VWT destroy on cleanup), which the proxy (owns=false borrow)
    // tests never exercise. This is the exact shape that SIGKILLed a protocol-typed ctor
    // argument on NativeAOT (CryptoSwift `new AES(key, new ECB(), ...)`).

    public void TestModeProcessorBoxedSimpleModeCtor()
    {
        // Trivial resilient struct — inline existential payload (<= 24B), owns=true.
        var processor = new ModeProcessor(new SimpleMode());
        AssertTrue(processor.Process(42), "ModeProcessor(boxed SimpleMode).Process(42)");
        AssertFalse(processor.Process(-1), "ModeProcessor(boxed SimpleMode).Process(-1)");
        AssertEqual("simple", processor.GetModeName(), "ModeProcessor(boxed SimpleMode).GetModeName()");
        TestLogger.Info("ModeProcessor with boxed SimpleMode struct passed");
    }

    public void TestModeProcessorBoxedRichModeCtor()
    {
        // Non-trivial resilient struct (String + Optional<Int32>) — forces the existential
        // payload out of line via swift_allocBox, the arm CryptoSwift's ECB hits. This is the
        // primary NativeAOT crash reproduction. RichMode's ceiling is 100.
        var processor = new ModeProcessor(new RichMode());
        AssertTrue(processor.Process(50), "ModeProcessor(boxed RichMode).Process(50) within [10,100]");
        AssertFalse(processor.Process(5), "ModeProcessor(boxed RichMode).Process(5) below threshold");
        AssertFalse(processor.Process(150), "ModeProcessor(boxed RichMode).Process(150) above ceiling");
        AssertEqual("rich", processor.GetModeName(), "ModeProcessor(boxed RichMode).GetModeName()");
        TestLogger.Info("ModeProcessor with boxed RichMode (non-inline allocBox) struct passed");
    }

    public void TestModeProcessorBoxedOpenModeCtor()
    {
        // Same non-inline struct shape, but the Optional<Int32> field is nil — exercises the
        // other Optional-payload branch through the boxed existential.
        var processor = new ModeProcessor(new OpenMode());
        AssertTrue(processor.Process(10), "ModeProcessor(boxed OpenMode).Process(10)");
        AssertTrue(processor.Process(9999), "ModeProcessor(boxed OpenMode).Process(9999) nil ceiling");
        AssertFalse(processor.Process(9), "ModeProcessor(boxed OpenMode).Process(9)");
        AssertEqual("open", processor.GetModeName(), "ModeProcessor(boxed OpenMode).GetModeName()");
        TestLogger.Info("ModeProcessor with boxed OpenMode (nil ceiling) passed");
    }

    public void TestModeProcessorBoxedClassModeCtor()
    {
        // Reference-type conformer control — bridges through ARC, not an inline/allocBox copy.
        var processor = new ModeProcessor(new ClassMode());
        AssertTrue(processor.Process(5), "ModeProcessor(boxed ClassMode).Process(5)");
        AssertFalse(processor.Process(4), "ModeProcessor(boxed ClassMode).Process(4)");
        AssertEqual("cls", processor.GetModeName(), "ModeProcessor(boxed ClassMode).GetModeName()");
        TestLogger.Info("ModeProcessor with boxed ClassMode reference conformer passed");
    }

    public void TestModeProcessorMatchesBoxedStructArg()
    {
        // Sibling *method* (not ctor) that boxes an existential argument — same owns=true
        // packing path at a method call site. The processor itself is built from a boxed
        // struct too, so both the ctor box and the method-arg box are live at once.
        var processor = new ModeProcessor(new RichMode());
        // RichMode[10,100].Validate(50)=true; SimpleMode.Validate(50)=true → agree.
        AssertTrue(processor.Matches(new SimpleMode(), 50), "Matches(boxed SimpleMode, 50) agree");
        // RichMode[10,100].Validate(150)=false; SimpleMode.Validate(150)=true → disagree.
        AssertFalse(processor.Matches(new SimpleMode(), 150), "Matches(boxed SimpleMode, 150) disagree");
        // Method-arg box of the non-inline OpenMode as well (nil-ceiling branch).
        AssertTrue(processor.Matches(new OpenMode(), 50), "Matches(boxed OpenMode, 50) agree");
        TestLogger.Info("ModeProcessor.Matches with boxed struct arguments passed");
    }

    #endregion

    #region Pipeline — Boxed Swift Struct Conformer (owns=true, multi-param ctor)

    public void TestPipelineBoxedRichModeCtor()
    {
        // Collection + boxed non-inline existential in one constructor (owns=true box coexists
        // with an array-marshalling temporary).
        var pipeline = new Pipeline(new[] { 1, 2, 3 }, new RichMode());
        AssertEqual(3, pipeline.GetStepCount(), "Pipeline(boxed RichMode).GetStepCount()");
        AssertEqual("rich", pipeline.GetModeName(), "Pipeline(boxed RichMode).GetModeName()");
        TestLogger.Info("Pipeline with boxed RichMode struct passed");
    }

    #endregion

    #region Free Functions — Boxed Swift Struct Conformer (owns=true)

    public void TestRunWithModeBoxedRichMode()
    {
        // Free-function argument boxing of the non-inline struct.
        AssertTrue(TestLibFunctions.RunWithMode(new RichMode(), 50), "RunWithMode(boxed RichMode, 50)");
        AssertFalse(TestLibFunctions.RunWithMode(new RichMode(), 150), "RunWithMode(boxed RichMode, 150)");
        TestLogger.Info("RunWithMode with boxed RichMode struct passed");
    }

    #endregion

    #region Pipeline — Array + Existential Constructor (Tier 3)

    // Fixed: EveryProtocol now uses real Swift objects + proper metadata
    public void TestPipelineConstruction()
    {
        var impl = new TestSimpleProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var pipeline = new Pipeline(new[] { 1, 2, 3 }, proxy);
        AssertNotNull(pipeline, "Pipeline constructed");
        TestLogger.Info("Pipeline construction passed");
    }

    // Fixed: EveryProtocol now uses real Swift objects + proper metadata
    public void TestPipelineGetStepCount()
    {
        var impl = new TestSimpleProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var pipeline = new Pipeline(new[] { 10, 20, 30, 40 }, proxy);
        AssertEqual(4, pipeline.GetStepCount(), "Pipeline.GetStepCount()");
        TestLogger.Info($"Pipeline.GetStepCount() = {pipeline.GetStepCount()}");
    }

    // String return now uses Utf8Slice encoding (avoids ARC issues with SwiftString)
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

    // Fixed: EveryProtocol now uses real Swift objects + proper metadata
    public void TestRunWithModeSimple()
    {
        var impl = new TestSimpleProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        var result = TestLibFunctions.RunWithMode(proxy, 42);
        AssertTrue(result, "RunWithMode(simple, 42)");
        TestLogger.Info($"RunWithMode(simple, 42) = {result}");
    }

    // Fixed: EveryProtocol now uses real Swift objects + proper metadata
    public void TestRunWithModeStrict()
    {
        var impl = new TestStrictProcessingMode();
        var proxy = new ProcessingModeProxy(impl);
        AssertTrue(TestLibFunctions.RunWithMode(proxy, 10), "RunWithMode(strict, 10)");
        AssertFalse(TestLibFunctions.RunWithMode(proxy, -5), "RunWithMode(strict, -5)");
        TestLogger.Info("RunWithMode with strict impl passed");
    }

    // Fixed: EveryProtocol now uses real Swift objects + proper metadata
    public void TestCompareResultsSameMode()
    {
        var a = new ProcessingModeProxy(new TestSimpleProcessingMode());
        var b = new ProcessingModeProxy(new TestSimpleProcessingMode());
        var result = TestLibFunctions.CompareResults(a, b, 42);
        AssertTrue(result, "CompareResults(simple, simple, 42) should agree");
        TestLogger.Info($"CompareResults(simple, simple, 42) = {result}");
    }

    // Fixed: EveryProtocol now uses real Swift objects + proper metadata
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
