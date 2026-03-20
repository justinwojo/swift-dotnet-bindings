// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;
using SwiftEventHandler = SwiftBindingsTestLib.EventHandler;

namespace RuntimeTestsApp.Patterns;

/// <summary>
/// Tests for real-world composition patterns — types that combine multiple
/// features (optionals + arrays, inheritance + protocols, singleton + optional
/// returns, closures + properties) that are individually tested but never in
/// combination.
///
/// Class name sorts alphabetically BEFORE EnumMarshallingTests (the Mono JIT
/// crash point), ensuring these tests complete before the process is killed.
///
/// Tier structure:
/// - Tier 1: Construction + blittable property access (constructors may pass String
///           internally but the assertions only read Int32 properties, static getters,
///           or check interface conformance — no String return paths exercised)
/// - Tier 2: Transformer.Apply (Cdecl closure wrapper), EventHandler.CreateDefault (no closure in call)
/// - Tier 3: Everything else — frozen struct string returns (JIT crash), SafeHandle
///           through CallConvSwift, missing entry points, closure returns, optional array
///           property access, Registry mutation methods, string-returning methods
///
/// Known Tier 3 blocks found during B5:
/// - BatchConfig.EffectiveName/DescribeConfig: SwiftString return on frozen struct through
///   CallConvSwift triggers Mono JIT assertion (jit-info.c:918)
/// - BatchConfig.TagCount: "Not enough bits to represent the passed value" — optional array
///   property on frozen struct layout mismatch
/// - Registry.Register/Clear/ProcessRegistry: "Passing non-blittable types to P/Invoke with
///   Swift calling convention" — SafeHandle arg through CallConvSwift
/// - ValueAnimal.Value/GetValue/SetValue: EntryPointNotFoundException — property/method
///   symbols not exported from dylib (class inheriting + conforming composition)
/// - EventHandler/Transformer: Closure P/Invoke crashes Mono JIT
/// </summary>
public class BasicCompositionTests : TestBase
{
    public BasicCompositionTests(TestResults results) : base(results) { }

    #region Tier 3 — Construction + Blittable Property Access (Mono JIT crash)

    public void TestBatchConfigMaxRetries()
    {
        var config = new BatchConfig(name: "test", maxRetries: 5, tags: null);
        AssertEqual(5, config.MaxRetries, "MaxRetries should be 5");
        TestLogger.Info($"BatchConfig.MaxRetries = {config.MaxRetries}");
    }

    public void TestRegistrySharedAccess()
    {
        var registry = Registry.Shared;
        AssertTrue(registry is not null, "Registry.Shared should not be null");
        TestLogger.Info("Registry.Shared access OK");
    }

    public void TestValueAnimalHasValueConformance()
    {
        var va = new ValueAnimal(name: "Wolf", sound: "Howl", value: 7);
        AssertTrue(va is IHasValue, "ValueAnimal should implement IHasValue");
        TestLogger.Info("ValueAnimal conforms to IHasValue");
    }

    public void TestTransformerOffset()
    {
        var t = new Transformer(offset: 10);
        AssertEqual(10, t.Offset, "Offset should be 10");
        TestLogger.Info($"Transformer.Offset = {t.Offset}");
    }

    #endregion

    #region Tier 3 — Composition Patterns with Known Runtime Blocks

    // --- ValueAnimal/Registry string methods: also Tier 3 because ---
    // Registry.Clear/Register use SafeHandle through CallConvSwift,
    // and ValueAnimal.Summary returns SwiftString through CallConvSwift

    public void TestValueAnimalSummary()
    {
        var va = new ValueAnimal(name: "Eagle", sound: "Screech", value: 100);
        var summary = va.GetSummary();
        AssertTrue(summary.Contains("Eagle"), "Summary should contain name");
        AssertTrue(summary.Contains("Screech"), "Summary should contain sound");
        AssertTrue(summary.Contains("100"), "Summary should contain value");
        TestLogger.Info($"ValueAnimal.Summary = {summary}");
    }

    public void TestRegistryLookupFound()
    {
        var registry = Registry.Shared;
        registry.Clear();
        var cat = new Animal(name: "Tabby", sound: "Purr");
        var id = registry.Register(cat);
        var found = registry.Lookup(id: id);
        AssertTrue(found is not null, "Lookup should find registered animal");
        var speak = found!.GetSpeak();
        AssertTrue(speak.Contains("Tabby"), "Found animal should speak with name");
        TestLogger.Info($"Registry.Lookup found: {speak}");
    }

    public void TestRegistryLookupNotFound()
    {
        var registry = Registry.Shared;
        registry.Clear();
        var notFound = registry.Lookup(id: 999);
        AssertTrue(notFound is null, "Lookup for non-existent ID should return null");
        TestLogger.Info("Registry.Lookup(999) = null");
    }

    // --- BatchConfig: frozen struct + optional array composition ---

    public void TestBatchConfigTagCountNil()
    {
        var config = new BatchConfig(name: "test", maxRetries: 3, tags: null);
        AssertEqual(0, config.GetTagCount(), "TagCount should be 0 for nil tags");
        TestLogger.Info($"BatchConfig nil tags: TagCount = {config.GetTagCount()}");
    }

    public void TestBatchConfigTagCountWithTags()
    {
        var tags = new SwiftArray<int>();
        tags.Append(10);
        tags.Append(20);
        tags.Append(30);
        var config = new BatchConfig(name: "tagged", maxRetries: 1, tags: tags);
        AssertEqual(3, config.GetTagCount(), "TagCount should be 3");
        TestLogger.Info($"BatchConfig with tags: TagCount = {config.GetTagCount()}");
    }

    // EffectiveName/DescribeConfig crash: SwiftString return on frozen struct
    // through CallConvSwift triggers Mono JIT assertion (jit-info.c:918)
    public void TestBatchConfigEffectiveName()
    {
        var config = new BatchConfig(name: "Upload", maxRetries: 3, tags: null);
        var name = config.GetEffectiveName();
        AssertTrue(name.Contains("Upload"), "EffectiveName should contain 'Upload'");
        AssertTrue(name.Contains("3"), "EffectiveName should contain retry count");
        TestLogger.Info($"BatchConfig.EffectiveName = {name}");
    }

    // Fixed: assumingMemoryBound(to:).pointee for frozen struct with non-BitwiseCopyable fields
    public void TestDescribeConfigFreeFunction()
    {
        var config = new BatchConfig(name: "Sync", maxRetries: 2, tags: null);
        var desc = TestLibFunctions.DescribeConfig(config);
        AssertTrue(desc.Contains("Sync"), "describeConfig should contain name");
        AssertTrue(desc.Contains("no tags"), "describeConfig should say 'no tags' for nil");
        TestLogger.Info($"describeConfig = {desc}");
    }

    // Fixed: assumingMemoryBound(to:).pointee for frozen struct with non-BitwiseCopyable fields
    public void TestDescribeConfigWithTags()
    {
        var tags = new SwiftArray<int>();
        tags.Append(1);
        tags.Append(2);
        var config = new BatchConfig(name: "Build", maxRetries: 1, tags: tags);
        var desc = TestLibFunctions.DescribeConfig(config);
        AssertTrue(desc.Contains("Build"), "describeConfig should contain name");
        AssertTrue(desc.Contains("2 tags"), "describeConfig should say '2 tags'");
        TestLogger.Info($"describeConfig with tags = {desc}");
    }

    // --- ValueAnimal: entry points now in SwiftBindings wrapper ---

    public void TestValueAnimalBlittableProperty()
    {
        var va = new ValueAnimal(name: "Fox", sound: "Ring", value: 42);
        AssertEqual(42, va.Value, "Value should be 42");
        TestLogger.Info($"ValueAnimal.Value = {va.Value}");
    }

    public void TestValueAnimalGetSetValue()
    {
        var va = new ValueAnimal(name: "Bear", sound: "Growl", value: 10);
        AssertEqual(10, va.GetValue(), "GetValue should return 10");
        va.SetValue(99);
        AssertEqual(99, va.GetValue(), "GetValue after SetValue(99) should return 99");
        AssertEqual(99, va.Value, "Value property should also reflect 99");
        TestLogger.Info($"ValueAnimal Get/SetValue: {va.GetValue()}");
    }

    public void TestValueAnimalHasValueInterface()
    {
        var va = new ValueAnimal(name: "Owl", sound: "Hoot", value: 55);
        IHasValue hasVal = va;
        AssertEqual(55, hasVal.Value, "IHasValue.Value should be 55");
        hasVal.Value = 77;
        AssertEqual(77, hasVal.Value, "IHasValue.Value after set should be 77");
        TestLogger.Info($"IHasValue interface dispatch: {hasVal.Value}");
    }

    // --- Registry: SafeHandle through CallConvSwift ---

    // Register/Clear/ProcessRegistry: "Passing non-blittable types to P/Invoke
    // with Swift calling convention" — SafeHandle as arg through CallConvSwift
    public void TestRegistryRegisterAndCount()
    {
        var registry = Registry.Shared;
        registry.Clear();
        var animal = new Animal(name: "Cat", sound: "Meow");
        var id = registry.Register(animal);
        AssertEqual(0, id, "First registered ID should be 0");
        AssertEqual(1, registry.GetCount(), "Count should be 1 after register");
        TestLogger.Info($"Registry: registered id={id}, count={registry.GetCount()}");
    }

    public void TestRegistryClear()
    {
        var registry = Registry.Shared;
        registry.Clear();
        var animal = new Animal(name: "Dog", sound: "Woof");
        registry.Register(animal);
        AssertEqual(1, registry.GetCount(), "Count should be 1");
        registry.Clear();
        AssertEqual(0, registry.GetCount(), "Count should be 0 after clear");
        TestLogger.Info("Registry.Clear works");
    }

    public void TestProcessRegistryFreeFunction()
    {
        var registry = Registry.Shared;
        registry.Clear();
        var a1 = new Animal(name: "A", sound: "a");
        var a2 = new Animal(name: "B", sound: "b");
        registry.Register(a1);
        registry.Register(a2);
        var count = TestLibFunctions.ProcessRegistry(registry);
        AssertEqual(2, count, "processRegistry should return 2");
        TestLogger.Info($"processRegistry = {count}");
    }

    // --- EventHandler: factory + blittable Fire() (no closure in call path) ---

    // CreateDefault is a factory (no closure param); Fire takes Int32, returns Bool
    public void TestEventHandlerCreateDefault()
    {
        var handler = SwiftEventHandler.CreateDefault();
        AssertTrue(handler is not null, "CreateDefault should return non-null");
        var result = handler!.Fire(42);
        AssertFalse(result, "Fire on default handler (nil closure) should return false");
        TestLogger.Info($"EventHandler.CreateDefault + Fire = {result}");
    }

    public void TestEventHandlerWithClosure()
    {
        var handler = new SwiftEventHandler(label: "test", onComplete: v => v > 10);
        var result = handler.Fire(20);
        AssertTrue(result, "Fire(20) with v > 10 closure should return true");
        var result2 = handler.Fire(5);
        AssertFalse(result2, "Fire(5) with v > 10 closure should return false");
        TestLogger.Info("EventHandler with closure works");
    }

    public void TestEventHandlerOnCompleteProperty()
    {
        var handler = SwiftEventHandler.CreateDefault();
        var onComplete = handler.OnComplete;
        AssertTrue(onComplete is null, "Default handler OnComplete should be null");
        TestLogger.Info("EventHandler.OnComplete property access OK");
    }

    // --- Transformer: Cdecl-wrapped closure P/Invoke (Strategy B) ---

    // Strategy B: Apply uses CallConvCdecl callback + _cdecl wrapper
    // NOTE: Transformer constructor calls GetTypeMetadata which crashes Mono JIT,
    // so this test only passes on NativeAOT (device) despite the Apply method being Cdecl-safe.
    public void TestTransformerApply()
    {
        var t = new Transformer(offset: 5);
        var result = t.Apply(10, x => x * 2);
        // apply(10, using: transform) => transform(10 + 5) => transform(15) => 30
        AssertEqual(30, result, "Apply(10, x => x * 2) with offset 5 should be 30");
        TestLogger.Info($"Transformer.Apply = {result}");
    }

    [SkipOnSimulator("Mono JIT !ji->async on calli through delegate* unmanaged[Swift] — crash is in indirect call (calli IL), not named P/Invoke; calling convention is correct (Swift CC for closure context in x20); no workaround exists since we only have a runtime function pointer")]
    public void TestTransformerChain()
    {
        var chained = Transformer.Chain(x => x + 1, x => x * 3);
        // chain(f, g) => x => g(f(x)) => x => (x + 1) * 3
        var result = chained(10);
        AssertEqual(33, result, "Chain(x+1, x*3)(10) should be 33");
        TestLogger.Info($"Transformer.Chain result = {result}");
    }

    #endregion
}
