// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// Tests for inout, default, and variadic parameters.
/// Inout tests verify the P/Invoke call completes without crashing (CallConvSwift path).
/// Default parameter tests verify correct return values with and without explicit args.
/// Variadic tests verify CallConvSwift dispatch with SwiftArray<T> (T... → Array<T> at ABI level).
/// </summary>
public class ParameterTests : TestBase
{
    public ParameterTests(TestResults results) : base(results) { }

    #region Inout Parameters

    // Note: The public API passes inout params by value (not ref), so mutations
    // aren't observable to the caller. These tests verify the CallConvSwift P/Invoke
    // path completes without crashing — the key validation for SB0001 methods.

#pragma warning disable SB0001 // No @_cdecl wrapper — CallConvSwift direct call
    public void TestIncrementValue()
    {
        // incrementValue(_ value: inout Int32) — CallConvSwift with ref int
        // Public API takes int by value, so the increment isn't visible to us,
        // but the call exercising the P/Invoke path must not crash.
        TestLibFunctions.IncrementValue(42);
        TestLogger.Info("IncrementValue(42) completed without crash");
    }

    public void TestSwapValues()
    {
        // swapValues(_ a: inout Int32, _ b: inout Int32) — two ref params
        TestLibFunctions.SwapValues(10, 20);
        TestLogger.Info("SwapValues(10, 20) completed without crash");
    }

    public void TestIncrementPoint()
    {
        // incrementPoint(_ point: inout FrozenPoint) — ref on frozen struct
        var point = new FrozenPoint(3.0, 4.0);
        TestLibFunctions.IncrementPoint(point);
        TestLogger.Info("IncrementPoint((3.0, 4.0)) completed without crash");
    }

    public void TestDoubleInPlace()
    {
        // doubleInPlace(_ value: inout Int32) -> Int32 — returns old value
        // The Swift function doubles value in-place and returns the original.
        // Public API passes by value, so the doubled result is lost,
        // but the return value (original) is observable.
        int result = TestLibFunctions.DoubleInPlace(7);
        AssertEqual(7, result, "DoubleInPlace should return original value");
        TestLogger.Info($"DoubleInPlace(7) returned {result}");
    }
#pragma warning restore SB0001

    #endregion

    #region Default Parameters

    public void TestGreetDefault()
    {
        // greet(name:greeting:) — greeting defaults to "Hello"
        string withDefault = TestLibFunctions.Greet("Alice");
        AssertEqual("Hello, Alice!", withDefault, "Greet with default greeting");

        string withExplicit = TestLibFunctions.Greet("Bob", "Hi");
        AssertEqual("Hi, Bob!", withExplicit, "Greet with explicit greeting");

        TestLogger.Info($"Greet default=\"{withDefault}\", explicit=\"{withExplicit}\"");
    }

    public void TestSearchDefaults()
    {
        // search(query:limit:offset:) — limit defaults to 10, offset to 0
        string withDefaults = TestLibFunctions.Search("test");
        AssertEqual("Search 'test' limit=10 offset=0", withDefaults, "Search with all defaults");

        string withExplicit = TestLibFunctions.Search("query", 5, 2);
        AssertEqual("Search 'query' limit=5 offset=2", withExplicit, "Search with explicit params");

        // Partial defaults — only override limit
        string partialDefault = TestLibFunctions.Search("partial", 25);
        AssertEqual("Search 'partial' limit=25 offset=0", partialDefault, "Search with partial defaults");

        TestLogger.Info("Search default parameter tests passed");
    }

    public void TestConfigureDefaults()
    {
        // configure(host:port:secure:) — port defaults to 8080, secure to true
        string withDefaults = TestLibFunctions.Configure("localhost");
        AssertEqual("https://localhost:8080", withDefaults, "Configure with all defaults");

        string insecure = TestLibFunctions.Configure("example.com", 80, false);
        AssertEqual("http://example.com:80", insecure, "Configure insecure");

        string customPort = TestLibFunctions.Configure("myhost", 3000);
        AssertEqual("https://myhost:3000", customPort, "Configure custom port, default secure");

        TestLogger.Info("Configure default parameter tests passed");
    }

    #endregion

    #region Variadic Parameters

#pragma warning disable SB0001 // No @_cdecl wrapper — CallConvSwift direct call
    public void TestSumAll()
    {
        // sumAll(_ values: Int32...) — variadic Int32 via CallConvSwift + SwiftArray<int>
        var result = TestLibFunctions.SumAll(new[] { 1, 2, 3, 4, 5 });
        AssertEqual(15, result, "SumAll(1..5) = 15");

        var empty = TestLibFunctions.SumAll(Array.Empty<int>());
        AssertEqual(0, empty, "SumAll(empty) = 0");
    }

    public void TestJoinStrings()
    {
        // joinStrings(_ strings: String...) — variadic String via CallConvSwift
        var result = TestLibFunctions.JoinStrings(new[] { "hello", "world" });
        AssertEqual("hello world", result, "JoinStrings joins with space");

        var single = TestLibFunctions.JoinStrings(new[] { "only" });
        AssertEqual("only", single, "JoinStrings single element");
    }

    public void TestVariadicConsumer()
    {
        // VariadicConsumer.sumWithPrefix — variadic on struct method
        using var consumer = new VariadicConsumer("Total: ");
        var result = consumer.SumWithPrefix(new[] { 10, 20, 30 });
        AssertEqual("Total: 60", result, "SumWithPrefix adds prefix to sum");
    }
#pragma warning restore SB0001

    #endregion
}
