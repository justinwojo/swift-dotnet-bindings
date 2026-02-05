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

    #region Tier 1 — Smoke Tests

    [TestTier(TestTier.Tier1)]
    public void TestEscapingWithInt32()
    {
        var result = SwiftBindingsTestLib.CallWithInt32(x => x * 2);
        AssertEqual(84, result, "CallWithInt32(x => x * 2) with 42");
        TestLogger.Info($"CallWithInt32(x => x * 2) = {result}");
    }

    [TestTier(TestTier.Tier1)]
    public void TestConventionCFunction()
    {
        var result = SwiftBindingsTestLib.CallCFunction(x => x + 8);
        AssertEqual(50, result, "CallCFunction(x => x + 8) with 42");
        TestLogger.Info($"CallCFunction(x => x + 8) = {result}");
    }

    #endregion

    #region Tier 2 — Escaping Closures

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

    #endregion

    #region Tier 2 — Closure Returns

    [TestTier(TestTier.Tier2)]
    public void TestMakeAdder()
    {
        var adder = SwiftBindingsTestLib.MakeAdder(10);
        AssertNotNull(adder, "MakeAdder returned delegate");
        var result = adder!(5);
        AssertEqual(15, result, "Adder(5) = 15");
        TestLogger.Info($"MakeAdder(10)(5) = {result}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMakeMultiplier()
    {
        var multiplier = SwiftBindingsTestLib.MakeMultiplier(3);
        AssertNotNull(multiplier, "MakeMultiplier returned delegate");
        var result = multiplier!(7);
        AssertEqual(21, result, "Multiplier(7) = 21");
        TestLogger.Info($"MakeMultiplier(3)(7) = {result}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestMakeGreaterThan()
    {
        var greaterThan5 = SwiftBindingsTestLib.MakeGreaterThan(5);
        AssertNotNull(greaterThan5, "MakeGreaterThan returned delegate");
        AssertTrue(greaterThan5!(10), "10 > 5");
        AssertFalse(greaterThan5!(3), "3 not > 5");
        TestLogger.Info("MakeGreaterThan passed");
    }

    #endregion

    #region Tier 2 — @convention(c) Variations

    [TestTier(TestTier.Tier2)]
    public void TestCBinaryFunction()
    {
        var result = SwiftBindingsTestLib.CallCBinaryFunction((a, b) => a * b);
        AssertEqual(200, result, "CallCBinaryFunction(10 * 20)");
        TestLogger.Info($"CallCBinaryFunction((a,b) => a*b) = {result}");
    }

    [TestTier(TestTier.Tier2)]
    public void TestCPredicate()
    {
        var result = SwiftBindingsTestLib.CallCPredicate(x => x > 5, 10);
        AssertTrue(result, "CPredicate(10 > 5)");

        result = SwiftBindingsTestLib.CallCPredicate(x => x > 5, 3);
        AssertFalse(result, "CPredicate(3 not > 5)");
        TestLogger.Info("CallCPredicate passed");
    }

    #endregion

    #region Tier 2 — Struct Closure Methods

    [TestTier(TestTier.Tier2)]
    public void TestClosureConsumer()
    {
        var consumer = new ClosureConsumer(3);
        var result = consumer.ApplyToValue(5, x => x + 1);
        // multiplier=3, value=5, so 5*3=15, then transform: 15+1=16
        AssertEqual(16, result, "ClosureConsumer.ApplyToValue(5, x+1) with multiplier 3");
        TestLogger.Info($"ClosureConsumer.ApplyToValue = {result}");
    }

    [TestTier(TestTier.Tier2)]
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

    #endregion
}
