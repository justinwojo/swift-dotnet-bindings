// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.SwiftBindingsTestLib;

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

    [TestTier(TestTier.Tier2)]
    public void TestEscapingWithInt32()
    {
        var result = SwiftBindingsTestLib.CallWithInt32(x => x * 2);
        AssertEqual(84, result, "CallWithInt32(x => x * 2) with 42");
        TestLogger.Info($"CallWithInt32(x => x * 2) = {result}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestVoidCallback()
    {
        var called = false;
        SwiftBindingsTestLib.CallVoidCallback(() => { called = true; });
        AssertTrue(called, "Void callback was called");
        TestLogger.Info("CallVoidCallback passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMultiArgClosure()
    {
        var result = SwiftBindingsTestLib.CallMultiArg((a, b) => a + b);
        AssertEqual(30, result, "CallMultiArg(10 + 20)");
        TestLogger.Info($"CallMultiArg((a,b) => a+b) = {result}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestBoolCallback()
    {
        var result = SwiftBindingsTestLib.CallBoolCallback(b => !b);
        AssertFalse(result, "CallBoolCallback(!true) = false");
        TestLogger.Info("CallBoolCallback passed");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCallMultipleTimes()
    {
        var result = SwiftBindingsTestLib.CallMultipleTimes(x => x * x, 3);
        // 1*1 + 2*2 + 3*3 = 1 + 4 + 9 = 14
        AssertEqual(14, result, "CallMultipleTimes(x^2, 3) = 14");
        TestLogger.Info($"CallMultipleTimes(x => x*x, 3) = {result}");
    }

    // Struct instance method with Cdecl closure wrapper + _selfFixed
    [TestTier(TestTier.Tier2)]
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

    [TestTier(TestTier.Tier3)]
    public void TestConventionCFunction()
    {
        var result = SwiftBindingsTestLib.CallCFunction(x => x + 8);
        AssertEqual(50, result, "CallCFunction(x => x + 8) with 42");
        TestLogger.Info($"CallCFunction(x => x + 8) = {result}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestCBinaryFunction()
    {
        var result = SwiftBindingsTestLib.CallCBinaryFunction((a, b) => a * b);
        AssertEqual(200, result, "CallCBinaryFunction(10 * 20)");
        TestLogger.Info($"CallCBinaryFunction((a,b) => a*b) = {result}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestCPredicate()
    {
        var result = SwiftBindingsTestLib.CallCPredicate(x => x > 5, 10);
        AssertTrue(result, "CPredicate(10 > 5)");

        result = SwiftBindingsTestLib.CallCPredicate(x => x > 5, 3);
        AssertFalse(result, "CPredicate(3 not > 5)");
        TestLogger.Info("CallCPredicate passed");
    }

    [TestTier(TestTier.Tier3)]
    public void TestMakeAdder()
    {
        var adder = SwiftBindingsTestLib.MakeAdder(10);
        AssertNotNull(adder, "MakeAdder returned delegate");
        var result = adder!(5);
        AssertEqual(15, result, "Adder(5) = 15");
        TestLogger.Info($"MakeAdder(10)(5) = {result}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestMakeMultiplier()
    {
        var multiplier = SwiftBindingsTestLib.MakeMultiplier(3);
        AssertNotNull(multiplier, "MakeMultiplier returned delegate");
        var result = multiplier!(7);
        AssertEqual(21, result, "Multiplier(7) = 21");
        TestLogger.Info($"MakeMultiplier(3)(7) = {result}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestMakeGreaterThan()
    {
        var greaterThan5 = SwiftBindingsTestLib.MakeGreaterThan(5);
        AssertNotNull(greaterThan5, "MakeGreaterThan returned delegate");
        AssertTrue(greaterThan5!(10), "10 > 5");
        AssertFalse(greaterThan5!(3), "3 not > 5");
        TestLogger.Info("MakeGreaterThan passed");
    }

    [TestTier(TestTier.Tier3)]
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
    // Tier 3: SwiftString through CallConvSwift triggers Mono JIT crash on simulator.

    [TestTier(TestTier.Tier3)]
    public void TestClosureWithOptionalStringReturn()
    {
        var result = SwiftBindingsTestLib.CallWithOptionalStringReturn(n => n > 0 ? $"value_{n}" : null);
        AssertNotNull(result, "Optional<String> closure returned non-null");
        AssertEqual("value_42", result, "CallWithOptionalStringReturn returned correct value");
        TestLogger.Info($"CallWithOptionalStringReturn = {result}");
    }

    [TestTier(TestTier.Tier3)]
    public void TestClosureWithStringArrayReturn()
    {
        var result = SwiftBindingsTestLib.CallWithStringArrayReturn(n =>
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
}
