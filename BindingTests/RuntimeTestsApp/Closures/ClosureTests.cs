// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Closures;

/// <summary>
/// Tests for closure marshalling: @convention(c), escaping with primitives/struct,
/// closure returns, and struct closure methods.
/// </summary>
public class ClosureTests : TestBase
{
    public ClosureTests(TestResults results) : base(results) { }

    #region Tier 2 — Escaping closures with Cdecl wrappers (Strategy B)
    // These escaping closure tests use CallConvCdecl callbacks + Swift _cdecl wrapper
    // functions, bypassing the Mono JIT CallConvSwift crash.

    public void TestEscapingWithInt32()
    {
        var result = TestLibFunctions.CallWithInt32(x => x * 2);
        AssertEqual(84, result, "CallWithInt32(x => x * 2) with 42");
        TestLogger.Info($"CallWithInt32(x => x * 2) = {result}");
    }

    public void TestVoidCallback()
    {
        var called = false;
        TestLibFunctions.CallVoidCallback(() => { called = true; });
        AssertTrue(called, "Void callback was called");
        TestLogger.Info("CallVoidCallback passed");
    }

    public void TestMultiArgClosure()
    {
        var result = TestLibFunctions.CallMultiArg((a, b) => a + b);
        AssertEqual(30, result, "CallMultiArg(10 + 20)");
        TestLogger.Info($"CallMultiArg((a,b) => a+b) = {result}");
    }

    public void TestBoolCallback()
    {
        var result = TestLibFunctions.CallBoolCallback(b => !b);
        AssertFalse(result, "CallBoolCallback(!true) = false");
        TestLogger.Info("CallBoolCallback passed");
    }

    public void TestCallMultipleTimes()
    {
        var result = TestLibFunctions.CallMultipleTimes(x => x * x, 3);
        // 1*1 + 2*2 + 3*3 = 1 + 4 + 9 = 14
        AssertEqual(14, result, "CallMultipleTimes(x^2, 3) = 14");
        TestLogger.Info($"CallMultipleTimes(x => x*x, 3) = {result}");
    }

    // Struct instance method with Cdecl closure wrapper + _selfFixed

    public void TestClosureConsumer()
    {
        var consumer = new ClosureConsumer(3);
        var result = consumer.ApplyToValue(5, x => x + 1);
        // multiplier=3, value=5, so 5*3=15, then transform: 15+1=16
        AssertEqual(16, result, "ClosureConsumer.ApplyToValue(5, x+1) with multiplier 3");
        TestLogger.Info($"ClosureConsumer.ApplyToValue = {result}");
    }

    #endregion

    #region Tier 3 — @convention(c) closures + closure returns (still CallConvSwift)
    // @convention(c) closures: C# callbacks still use CallConvSwift (Strategy B excludes them)
    // Closure returns: invoking returned closures uses delegate* unmanaged[Swift]

    public void TestConventionCFunction()
    {
        var result = TestLibFunctions.CallCFunction(x => x + 8);
        AssertEqual(50, result, "CallCFunction(x => x + 8) with 42");
        TestLogger.Info($"CallCFunction(x => x + 8) = {result}");
    }

    public void TestCBinaryFunction()
    {
        var result = TestLibFunctions.CallCBinaryFunction((a, b) => a * b);
        AssertEqual(200, result, "CallCBinaryFunction(10 * 20)");
        TestLogger.Info($"CallCBinaryFunction((a,b) => a*b) = {result}");
    }

    public void TestCPredicate()
    {
        var result = TestLibFunctions.CallCPredicate(x => x > 5, 10);
        AssertTrue(result, "CPredicate(10 > 5)");

        result = TestLibFunctions.CallCPredicate(x => x > 5, 3);
        AssertFalse(result, "CPredicate(3 not > 5)");
        TestLogger.Info("CallCPredicate passed");
    }

    public void TestMakeAdder()
    {
        var adder = TestLibFunctions.MakeAdder(10);
        AssertNotNull(adder, "MakeAdder returned delegate");
        var result = adder!(5);
        AssertEqual(15, result, "Adder(5) = 15");
        TestLogger.Info($"MakeAdder(10)(5) = {result}");
    }

    public void TestMakeMultiplier()
    {
        var multiplier = TestLibFunctions.MakeMultiplier(3);
        AssertNotNull(multiplier, "MakeMultiplier returned delegate");
        var result = multiplier!(7);
        AssertEqual(21, result, "Multiplier(7) = 21");
        TestLogger.Info($"MakeMultiplier(3)(7) = {result}");
    }

    public void TestMakeGreaterThan()
    {
        var greaterThan5 = TestLibFunctions.MakeGreaterThan(5);
        AssertNotNull(greaterThan5, "MakeGreaterThan returned delegate");
        AssertTrue(greaterThan5!(10), "10 > 5");
        AssertFalse(greaterThan5!(3), "3 not > 5");
        TestLogger.Info("MakeGreaterThan passed");
    }

    public void TestClosureFactory()
    {
        var factory = new ClosureFactory(100);
        var transform = factory.MakeTransform();
        AssertNotNull(transform, "MakeTransform returned delegate");
        var result = transform!(50);
        // base=100, x=50 -> base + x = 150
        AssertEqual(150, result, "ClosureFactory.MakeTransform()(50) = 150");

        var scaler = ClosureFactory.MakeScaler(3);
        AssertNotNull(scaler, "MakeScaler returned delegate");
        var scaled = scaler!(10);
        AssertEqual(30, scaled, "ClosureFactory.MakeScaler(3)(10) = 30");
        TestLogger.Info("ClosureFactory tests passed");
    }

    // B7 closure return tests — Optional<String> and [String] closure returns
    // These use the normal ClosureEmitter pipeline with the B7 gate lifted for String.
    // Generated code is correct (CallConvCdecl + indirect return), but Mono JIT
    // assertion !ji->async triggers during SwiftString callback marshalling (upstream Issue 1).

    [SkipOnSimulator("Mono JIT assertion !ji->async during SwiftString callback marshalling (upstream Issue 1)")]
    public void TestClosureWithOptionalStringReturn()
    {
        var result = TestLibFunctions.CallWithOptionalStringReturn(n => n > 0 ? $"value_{n}" : null);
        AssertNotNull(result, "Optional<String> closure returned non-null");
        AssertEqual("value_42", result, "CallWithOptionalStringReturn returned correct value");
        TestLogger.Info($"CallWithOptionalStringReturn = {result}");
    }

    [SkipOnSimulator("Mono JIT assertion !ji->async during SwiftString callback marshalling (upstream Issue 1)")]
    public void TestClosureWithStringArrayReturn()
    {
        var result = TestLibFunctions.CallWithStringArrayReturn(n =>
        {
            var list = new string[n];
            for (int i = 0; i < n; i++)
                list[i] = $"item_{i}";
            return list;
        });
        AssertNotNull(result, "String array closure returned non-null");
        AssertEqual(3, result!.Count, "Array has 3 elements");
        AssertEqual("item_0", result[0]?.ToString(), "First element is item_0");
        TestLogger.Info($"CallWithStringArrayReturn count = {result.Count}");
    }

    #endregion

    #region Pass 2 — P3: Optional Closure Parameter

    public void TestExecuteIfPresentWithAction()
    {
        var called = false;
        var result = TestLibFunctions.ExecuteIfPresent(() => { called = true; }, 99);
        AssertEqual(1, result, "Returns 1 when action executed");
        AssertTrue(called, "Action was called");
        TestLogger.Info("ExecuteIfPresent with action passed");
    }

    public void TestExecuteIfPresentWithNull()
    {
        var result = TestLibFunctions.ExecuteIfPresent(null, 42);
        AssertEqual(42, result, "Returns fallback when null");
        TestLogger.Info("ExecuteIfPresent with null passed");
    }

    #endregion

    #region Frozen Struct Closure Parameters

    public void TestClosureWithFrozenStructParam()
    {
        // Tests closure with frozen struct parameter via @_cdecl heap allocation path.
        // Swift adapter: allocates FrozenPoint on heap via initializeMemory, passes UnsafeMutableRawPointer.
        // C# callback: receives void*, unmarshals via SwiftMarshal.MarshalFromSwift<FrozenPoint>.
        // Swift function creates FrozenPoint(x: 3.0, y: 4.0) and passes to callback.
        var result = TestLibFunctions.CallWithFrozenStruct(point => point.X + point.Y);
        AssertEqual(7.0, result, "CallWithFrozenStruct: 3.0 + 4.0 = 7.0");
        TestLogger.Info($"CallWithFrozenStruct(x+y) = {result}");
    }

    public void TestClosureWithFrozenStructParamComplex()
    {
        // Tests that the frozen struct data is correctly marshalled through the closure boundary.
        // Compute distance from origin: sqrt(x^2 + y^2) for FrozenPoint(3.0, 4.0) = 5.0.
        var result = TestLibFunctions.CallWithFrozenStruct(point =>
            Math.Sqrt(point.X * point.X + point.Y * point.Y));
        AssertEqual(5.0, result, "CallWithFrozenStruct: distance(3,4) = 5.0");
        TestLogger.Info($"CallWithFrozenStruct(distance) = {result}");
    }

    #endregion

    #region Pass 2 — X2: Multiple Closure Parameters

    public void TestExecuteWithCallbacks()
    {
        var started = false;
        var completedValue = 0;
        TestLibFunctions.ExecuteWithCallbacks(
            () => { started = true; },
            v => { completedValue = v; });
        AssertTrue(started, "onStart was called");
        AssertEqual(42, completedValue, "onComplete received 42");
        TestLogger.Info("ExecuteWithCallbacks passed");
    }

    #endregion

    #region Optional<Primitive/Enum> Closure Parameters

    public void TestClosureWithOptionalIntSome()
    {
        // Swift calls callback(42), C# receives int? = 42
        var result = TestLibFunctions.CallWithOptionalInt(x => x.HasValue ? x.Value * 2 : -1);
        AssertEqual(84, result, "CallWithOptionalInt(42) → x*2 = 84");
        TestLogger.Info($"CallWithOptionalInt(some) = {result}");
    }

    public void TestClosureWithOptionalIntNone()
    {
        // Swift calls callback(nil), C# receives int? = null
        var result = TestLibFunctions.CallWithNilInt(x => x.HasValue ? x.Value : -1);
        AssertEqual(-1, result, "CallWithNilInt(nil) → -1");
        TestLogger.Info($"CallWithNilInt(none) = {result}");
    }

    public void TestClosureWithOptionalBoolSome()
    {
        // Swift calls callback(true), C# receives bool? = true
        var result = TestLibFunctions.CallWithOptionalBool(x => x.HasValue && x.Value);
        AssertTrue(result, "CallWithOptionalBool(true) → true");
        TestLogger.Info($"CallWithOptionalBool(some) = {result}");
    }

    public void TestClosureWithOptionalBoolNone()
    {
        // Swift calls callback(nil), C# receives bool? = null
        var result = TestLibFunctions.CallWithNilBool(x => x.HasValue ? x.Value : false);
        AssertFalse(result, "CallWithNilBool(nil) → false");
        TestLogger.Info($"CallWithNilBool(none) = {result}");
    }

    public void TestClosureWithOptionalEnumSome()
    {
        // Swift calls callback(.blue), C# receives Color? = Color.Blue
        var result = TestLibFunctions.CallWithOptionalEnum(x => x.HasValue ? (int)x.Value : -1);
        AssertEqual((int)Color.Blue, result, "CallWithOptionalEnum(.blue) → 2");
        TestLogger.Info($"CallWithOptionalEnum(some) = {result}");
    }

    public void TestClosureWithOptionalEnumNone()
    {
        // Swift calls callback(nil), C# receives Color? = null
        var result = TestLibFunctions.CallWithNilEnum(x => x.HasValue ? (int)x.Value : -1);
        AssertEqual(-1, result, "CallWithNilEnum(nil) → -1");
        TestLogger.Info($"CallWithNilEnum(none) = {result}");
    }

    public void TestClosureWithOptionalDoubleSome()
    {
        // Swift calls callback(3.14), C# receives double? = 3.14
        var result = TestLibFunctions.CallWithOptionalDouble(x => x.HasValue ? x.Value * 2.0 : 0.0);
        AssertEqual(6.28, result, "CallWithOptionalDouble(3.14) → 6.28");
        TestLogger.Info($"CallWithOptionalDouble(some) = {result}");
    }

    #endregion

    #region P1: Optional Closure Property Setter (Session 2 regression — ClosureHolder)

    public void TestClosureHolderSetCallback()
    {
        // Session 2 fix (68926ecd): Optional closure property setter emission.
        // The @_cdecl setter accepts optional function pointer — nil clears, non-nil wraps.
        var captured = -1;
        using var holder = new ClosureHolder();
        holder.OnValueChanged = v => { captured = v; };
        holder.TriggerChange(42);
        AssertEqual(42, captured, "Callback captured value from TriggerChange");
        TestLogger.Info("ClosureHolder.OnValueChanged setter + trigger passed");
    }

    public void TestClosureHolderSetCallbackToNull()
    {
        using var holder = new ClosureHolder();
        holder.OnValueChanged = v => { }; // set first
        holder.OnValueChanged = null;      // clear
        // TriggerChange should not crash when callback is nil
        holder.TriggerChange(99);
        TestLogger.Info("ClosureHolder.OnValueChanged set-to-null + trigger passed (no crash)");
    }

    public void TestClosureHolderGetCallbackNull()
    {
        using var holder = new ClosureHolder();
        var cb = holder.OnValueChanged;
        AssertNull(cb, "OnValueChanged initially null");
        TestLogger.Info("ClosureHolder.OnValueChanged getter (null) passed");
    }

    public void TestClosureHolderRoundTrip()
    {
        // Set callback → trigger → verify → change callback → trigger → verify new value
        var first = -1;
        var second = -1;
        using var holder = new ClosureHolder();
        holder.OnValueChanged = v => { first = v; };
        holder.TriggerChange(10);
        AssertEqual(10, first, "First callback captured 10");

        holder.OnValueChanged = v => { second = v; };
        holder.TriggerChange(20);
        AssertEqual(20, second, "Second callback captured 20");
        AssertEqual(10, first, "First callback unchanged");
        TestLogger.Info("ClosureHolder round-trip passed");
    }

    #endregion

    #region P2: Static Optional Closure Property (LogRouter)

    [SkipOnSimulator("Mono JIT !ji->async assertion on native-to-managed string callback (upstream Issue 1)")]
    public void TestLogRouterSetHandler()
    {
        var captured = "";
        LogRouter.LogHandler = msg => { captured = msg; };
        LogRouter.Route("hello");
        AssertEqual("hello", captured, "LogHandler captured message");
        LogRouter.LogHandler = null; // cleanup
        TestLogger.Info("LogRouter.LogHandler setter + route passed");
    }

    [SkipOnSimulator("Mono JIT !ji->async assertion on native-to-managed string callback (upstream Issue 1)")]
    public void TestLogRouterClearHandler()
    {
        LogRouter.LogHandler = msg => { };
        LogRouter.LogHandler = null;
        // Route should not crash when handler is nil
        LogRouter.Route("ignored");
        TestLogger.Info("LogRouter.LogHandler clear + route passed (no crash)");
    }

    public void TestLogRouterGetHandlerNull()
    {
        LogRouter.LogHandler = null;
        var handler = LogRouter.LogHandler;
        AssertNull(handler, "LogHandler is null after clear");
        TestLogger.Info("LogRouter.LogHandler getter (null) passed");
    }

    #endregion

    #region Closure + Existential Array Constructor (Swinject NativeAOT pattern)
    // Regression test for Swinject Container.init(behaviors: [any Behavior], registerClosure: ...).
    // The ClosureWithExistentialArray class takes both an [any ProcessingMode] array and an
    // @escaping (Int32) -> Int32 closure. This triggers SwiftArray<ExistentialContainer1> type
    // init during closure construction — the pattern that caused TypeInitializationException
    // on NativeAOT before the SwiftArray.NativeAotInitialize() try-catch fix.

    public void TestClosureWithExistentialArrayInit()
    {
        var modes = new IProcessingMode[] { new SimpleMode(), new StrictMode() };
        using var obj = new ClosureWithExistentialArray(modes, x => x * 10);
        AssertEqual(2, obj.GetModeCount(), "Should have 2 modes");
        AssertEqual(20, obj.GetTransformResult(), "transform(2 modes) = 2 * 10 = 20");
        TestLogger.Info($"ClosureWithExistentialArray: modes={obj.GetModeCount()}, result={obj.GetTransformResult()}");
    }

    public void TestClosureWithExistentialArrayEmptyModes()
    {
        var modes = Array.Empty<IProcessingMode>();
        using var obj = new ClosureWithExistentialArray(modes, x => x + 100);
        AssertEqual(0, obj.GetModeCount(), "Should have 0 modes");
        AssertEqual(100, obj.GetTransformResult(), "transform(0 modes) = 0 + 100 = 100");
        TestLogger.Info("ClosureWithExistentialArray empty modes passed");
    }

    #endregion
}
