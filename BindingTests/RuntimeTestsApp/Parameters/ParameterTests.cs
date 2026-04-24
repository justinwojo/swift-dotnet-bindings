// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// Tests for inout, default, and variadic parameters.
/// Inout tests verify the @_cdecl wrapper path completes without crashing (UnsafeMutableRawPointer write-back).
/// Default parameter tests verify correct return values with and without explicit args.
/// Variadic tests verify CallConvSwift dispatch with SwiftArray<T> (T... → Array<T> at ABI level).
/// </summary>
public class ParameterTests : TestBase
{
    public ParameterTests(TestResults results) : base(results) { }

    #region Inout Parameters

    // Inout parameters use @_cdecl wrappers with UnsafeMutableRawPointer + write-back semantics.
    // The public API passes inout params by value (not ref), so mutations aren't observable
    // to the caller. These tests verify the @_cdecl wrapper path completes without crashing.

    public void TestIncrementValue()
    {
        // incrementValue(_ value: inout Int32) — @_cdecl wrapper with UnsafeMutableRawPointer
        TestLibFunctions.IncrementValue(42);
        TestLogger.Info("IncrementValue(42) completed without crash");
    }

    public void TestSwapValues()
    {
        // swapValues(_ a: inout Int32, _ b: inout Int32) — two inout params with write-back
        TestLibFunctions.SwapValues(10, 20);
        TestLogger.Info("SwapValues(10, 20) completed without crash");
    }

    public void TestIncrementPoint()
    {
        // incrementPoint(_ point: inout FrozenPoint) — inout frozen struct
        var point = new FrozenPoint(3.0, 4.0);
        TestLibFunctions.IncrementPoint(point);
        TestLogger.Info("IncrementPoint((3.0, 4.0)) completed without crash");
    }

    public void TestDoubleInPlace()
    {
        // doubleInPlace(_ value: inout Int32) -> Int32 — returns old value
        // The Swift function doubles value in-place and returns the original.
        int result = TestLibFunctions.DoubleInPlace(7);
        AssertEqual(7, result, "DoubleInPlace should return original value");
        TestLogger.Info($"DoubleInPlace(7) returned {result}");
    }

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

    #region Default-argument Optional non-primitive value-type params (Issue #31)

    public void TestOptParamModel_AllDefaults()
    {
        using var model = new OptParamModel("m1");
        AssertEqual("m1", model.Name.ToString(), "Name");
        AssertEqual(-1, model.SmallCode, "SmallCode defaulted");
        AssertEqual("<default>", model.SmallLabel.ToString(), "SmallLabel defaulted");
        AssertEqual(-1L, model.LargeA, "LargeA defaulted");
        AssertEqual("<default>", model.LargeD.ToString(), "LargeD defaulted");
    }

    public void TestOptParamModel_ExplicitNil()
    {
        using var model = new OptParamModel("m2", small: null, large: null);
        AssertEqual("m2", model.Name.ToString(), "Name");
        AssertEqual(-1, model.SmallCode, "SmallCode explicit nil");
        AssertEqual(-1L, model.LargeA, "LargeA explicit nil");
    }

    public void TestOptParamModel_ExplicitSmall()
    {
        using var small = new OptParamSmallConfig(code: 42, label: "small!");
        using var model = new OptParamModel("m3", small: small);
        AssertEqual("m3", model.Name.ToString(), "Name");
        AssertEqual(42, model.SmallCode, "SmallCode explicit");
        AssertEqual("small!", model.SmallLabel.ToString(), "SmallLabel explicit");
        AssertEqual(-1L, model.LargeA, "LargeA defaulted");
    }

    public void TestOptParamModel_ExplicitLarge()
    {
        using var large = new OptParamLargeConfig(a: 7, b: 8, c: 9, d: "dee", e: "ee");
        using var model = new OptParamModel("m4", small: null, large: large);
        AssertEqual("m4", model.Name.ToString(), "Name");
        AssertEqual(-1, model.SmallCode, "SmallCode defaulted");
        AssertEqual(7L, model.LargeA, "LargeA explicit");
        AssertEqual("dee", model.LargeD.ToString(), "LargeD explicit");
    }

    public void TestOptParamModel_BothExplicit()
    {
        using var small = new OptParamSmallConfig(code: 11, label: "both");
        using var large = new OptParamLargeConfig(a: 101, b: 102, c: 103, d: "D", e: "E");
        using var model = new OptParamModel("m5", small: small, large: large);
        AssertEqual(11, model.SmallCode, "SmallCode");
        AssertEqual("both", model.SmallLabel.ToString(), "SmallLabel");
        AssertEqual(101L, model.LargeA, "LargeA");
        AssertEqual("D", model.LargeD.ToString(), "LargeD");
    }

    public void TestBuildOptParamSummary_AllDefaults()
    {
        string s = TestLibFunctions.BuildOptParamSummary("t0");
        AssertEqual("t0|small=nil|large=nil|flag=q", s, "Free function all defaults");
    }

    public void TestBuildOptParamSummary_SmallOnly()
    {
        using var small = new OptParamSmallConfig(code: 1, label: "x");
        string s = TestLibFunctions.BuildOptParamSummary("t1", small: small);
        AssertEqual("t1|small=(1,x)|large=nil|flag=q", s, "Free function small only");
    }

    public void TestBuildOptParamSummary_LargeOnly()
    {
        using var large = new OptParamLargeConfig(a: 9, b: 0, c: 0, d: "dd", e: "");
        string s = TestLibFunctions.BuildOptParamSummary("t2", small: null, large: large);
        AssertEqual("t2|small=nil|large=(9,dd)|flag=q", s, "Free function large only");
    }

    // Full-wrapper path (no default args) — regression gate for a second manifestation
    // of the #31 layout mismatch. The full OptionalPointerWrapper used to emit
    // `assumingMemoryBound(to: Optional<T>.self).pointee` against a C# SwiftOptional<IntPtr>
    // buffer. For non-frozen inner types those layouts disagree. Fix routes the full-wrapper
    // deref through the same opaque-aware helper the DBW path already uses.

    public void TestDescribeSmall_FullWrapper_Some()
    {
        using var holder = new OptParamHolder(code: 11);
        using var small = new OptParamSmallConfig(code: 1, label: "x");
        string s = holder.DescribeSmall(small);
        AssertEqual("holder=11|small=(1,x)", s, "Full-wrapper instance method with Optional<NonFrozenStruct> = Some");
    }

    public void TestDescribeSmall_FullWrapper_Nil()
    {
        using var holder = new OptParamHolder(code: 12);
        string s = holder.DescribeSmall(null);
        AssertEqual("holder=12|small=nil", s, "Full-wrapper instance method with Optional<NonFrozenStruct> = nil");
    }

    public void TestDescribeLarge_FullWrapper_Some()
    {
        using var holder = new OptParamHolder(code: 21);
        using var large = new OptParamLargeConfig(a: 42, b: 0, c: 0, d: "big", e: "");
        string s = holder.DescribeLarge(large);
        AssertEqual("holder=21|large=(42,big)", s, "Full-wrapper instance method with Optional<LargeNonFrozenStruct> = Some");
    }

    public void TestDescribeLarge_FullWrapper_Nil()
    {
        using var holder = new OptParamHolder(code: 22);
        string s = holder.DescribeLarge(null);
        AssertEqual("holder=22|large=nil", s, "Full-wrapper instance method with Optional<LargeNonFrozenStruct> = nil");
    }

    public void TestSummarizeOptHolder_FullWrapper_Some()
    {
        using var small = new OptParamSmallConfig(code: 7, label: "yo");
        string s = TestLibFunctions.SummarizeOptHolder(small);
        AssertEqual("free=(7,yo)", s, "Full-wrapper free function with Optional<NonFrozenStruct> = Some");
    }

    public void TestSummarizeOptHolder_FullWrapper_Nil()
    {
        string s = TestLibFunctions.SummarizeOptHolder(null);
        AssertEqual("free=nil", s, "Full-wrapper free function with Optional<NonFrozenStruct> = nil");
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
