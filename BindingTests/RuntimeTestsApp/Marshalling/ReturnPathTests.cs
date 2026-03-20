// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests return type marshalling through different generator code paths.
/// Exercises WrapperEmitter.Return and PInvokeEmitter return handling.
///
/// Coverage gaps addressed:
/// - Tuple return (WrapperEmitter.Return:279-291, PInvokeEmitter:100-128)
/// - Closure return (WrapperEmitter.Return:250-269, PInvokeEmitter:73-92)
/// - Optional&lt;closure&gt; return (WrapperEmitter.Return:151-175)
/// - DynamicSelf on class (PInvokeEmitter:179-182)
/// - Self return on struct (MethodWrapperEmitter:145-146)
/// - String return via @_cdecl (WrapperEmitter.Return:102-117)
///
/// Note: All return path methods use CallConvCdecl wrappers with indirect result buffers.
/// The crashes are in the marshalling layer (ValueTuple StructLayout.Auto, closure 16-byte
/// struct return), not in CallConvSwift register assignment.
/// </summary>
public class ReturnPathTests : TestBase
{
    public ReturnPathTests(TestResults results) : base(results) { }

    #region PairMaker — Tuple Return

    public void TestPairMakerConstruction()
    {
        var maker = new PairMaker(label: "test");
        AssertNotNull(maker, "PairMaker constructed");
        TestLogger.Info("PairMaker construction passed");
    }

    public void TestPairMakerTupleReturn()
    {
        var maker = new PairMaker(label: "item");
        var pair = maker.MakePair(value: 42);
        AssertEqual(42, pair.Item1, "Tuple.Item1 (Int32)");
        AssertEqual("item:42", pair.Item2, "Tuple.Item2 (String)");
        TestLogger.Info($"PairMaker.MakePair = ({pair.Item1}, {pair.Item2})");
    }

    #endregion

    #region TransformFactory — Closure Return

    public void TestTransformFactoryConstruction()
    {
        var factory = new TransformFactory(multiplier: 3);
        AssertNotNull(factory, "TransformFactory constructed");
        TestLogger.Info("TransformFactory construction passed");
    }

    [SkipOnSimulator("Mono JIT !ji->async on calli through delegate* unmanaged[Swift] — crash is in indirect call (calli IL), not named P/Invoke; calling convention is correct (Swift CC for closure context in x20); no workaround exists since we only have a runtime function pointer")]
    public void TestTransformFactoryClosureReturn()
    {
        var factory = new TransformFactory(multiplier: 3);
        var transform = factory.MakeTransform();
        AssertNotNull(transform, "MakeTransform returned non-null");
        var result = transform(10);
        AssertEqual(30, result, "transform(10) = 10 * 3");
        TestLogger.Info($"TransformFactory.MakeTransform()(10) = {result}");
    }

    #endregion

    #region OptionalHandlerFactory — Optional<Closure> Return

    public void TestOptionalHandlerFactoryConstruction()
    {
        var factory = new OptionalHandlerFactory(enabled: true);
        AssertNotNull(factory, "OptionalHandlerFactory constructed");
        TestLogger.Info("OptionalHandlerFactory construction passed");
    }

    [SkipOnSimulator("Mono JIT !ji->async on calli through delegate* unmanaged[Swift] — crash is in indirect call (calli IL), not named P/Invoke; calling convention is correct (Swift CC for closure context in x20); no workaround exists since we only have a runtime function pointer")]
    public void TestOptionalHandlerReturnsValue()
    {
        var factory = new OptionalHandlerFactory(enabled: true);
        var handler = factory.MakeHandler();
        AssertNotNull(handler, "MakeHandler should return non-null when enabled");
        var result = handler!(10);
        AssertEqual(20, result, "Handler should double the value");
        TestLogger.Info($"OptionalHandlerFactory.MakeHandler(10) = {result}");
    }

    [SkipOnSimulator("Mono JIT !ji->async on calli through delegate* unmanaged[Swift] — crash is in indirect call (calli IL), not named P/Invoke; calling convention is correct (Swift CC for closure context in x20); no workaround exists since we only have a runtime function pointer")]
    public void TestOptionalHandlerReturnsNil()
    {
        var factory = new OptionalHandlerFactory(enabled: false);
        var handler = factory.MakeHandler();
        AssertNull(handler, "MakeHandler should return null when disabled");
        TestLogger.Info("OptionalHandlerFactory.MakeHandler null passed");
    }

    #endregion

    #region Buildable — DynamicSelf Return on Class

    public void TestBuildableConstruction()
    {
        var b = new Buildable(tag: 1);
        AssertNotNull(b, "Buildable constructed");
        AssertEqual(1, b.Tag, "Buildable.Tag");
        TestLogger.Info("Buildable construction passed");
    }

    public void TestBuildableDynamicSelfReturn()
    {
        var b = new Buildable(tag: 1);
        var b2 = b.WithTag(42);
        AssertNotNull(b2, "WithTag returned non-null");
        AssertEqual(42, b2.Tag, "New tag value");
        TestLogger.Info("Buildable.WithTag DynamicSelf return passed");
    }

    #endregion

    #region CopyableValue — Self Return on Struct

    public void TestCopyableValueConstruction()
    {
        var val = new CopyableValue(value: 10);
        AssertEqual(10, val.Value, "Initial value");
        TestLogger.Info("CopyableValue construction passed");
    }

    public void TestCopyableValueSelfReturn()
    {
        var val = new CopyableValue(value: 10);
        var val2 = val.WithValue(42);
        AssertEqual(42, val2.Value, "New value after WithValue");
        AssertEqual(10, val.Value, "Original unchanged");
        TestLogger.Info("CopyableValue.WithValue passed");
    }

    public void TestCopyableValueDescribe()
    {
        var val = new CopyableValue(value: 99);
        var desc = val.GetDescribe();
        AssertTrue(desc.Contains("99"), "Description contains value");
        TestLogger.Info($"CopyableValue.GetDescribe() = {desc}");
    }

    #endregion

    #region Greeter — String Return via @_cdecl

    public void TestGreeterConstruction()
    {
        var g = new Greeter(name: "World");
        AssertNotNull(g, "Greeter constructed");
        TestLogger.Info("Greeter construction passed");
    }

    public void TestGreeterStringReturn()
    {
        var g = new Greeter(name: "World");
        var greeting = g.Greet(greeting: "Hello");
        AssertEqual("Hello, World!", greeting, "Greeting string");
        TestLogger.Info($"Greeter.Greet = {greeting}");
    }

    #endregion
}
