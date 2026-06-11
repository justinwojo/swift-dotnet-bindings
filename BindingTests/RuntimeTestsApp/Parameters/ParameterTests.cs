// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
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
    // The public API passes them by `ref`, so Swift's in-place mutation IS observable to the caller.
    // These tests assert the round-trip: the value Swift wrote back must reach the caller's variable.
    // Primitives propagate the readback straight through the P/Invoke `ref`; blittable frozen structs
    // marshal into a stack buffer, then re-read it (MarshalFromSwift) into the `ref` param after the call.

    public void TestIncrementValue()
    {
        // incrementValue(_ value: inout Int32) — Swift does `value += 1`.
        int value = 42;
        TestLibFunctions.IncrementValue(ref value);
        AssertEqual(43, value, "IncrementValue must write the incremented value back to the caller");
        TestLogger.Info($"IncrementValue(ref 42) → {value}");
    }

    public void TestSwapValues()
    {
        // swapValues(_ a: inout Int32, _ b: inout Int32) — two inout params, both written back.
        int a = 10, b = 20;
        TestLibFunctions.SwapValues(ref a, ref b);
        AssertEqual(20, a, "SwapValues must write the swapped value back to a");
        AssertEqual(10, b, "SwapValues must write the swapped value back to b");
        TestLogger.Info($"SwapValues(ref 10, ref 20) → a={a}, b={b}");
    }

    public void TestIncrementPoint()
    {
        // incrementPoint(_ point: inout FrozenPoint) — blittable frozen struct, Swift adds 1 to x and y.
        var point = new FrozenPoint(3.0, 4.0);
        TestLibFunctions.IncrementPoint(ref point);
        AssertEqual(4.0, point.X, "IncrementPoint must write the incremented x back to the caller");
        AssertEqual(5.0, point.Y, "IncrementPoint must write the incremented y back to the caller");
        TestLogger.Info($"IncrementPoint(ref (3,4)) → ({point.X}, {point.Y})");
    }

    public void TestDoubleInPlace()
    {
        // doubleInPlace(_ value: inout Int32) -> Int32 — doubles in-place, returns the OLD value.
        // Exercises both channels at once: the return value AND the inout writeback.
        int value = 7;
        int result = TestLibFunctions.DoubleInPlace(ref value);
        AssertEqual(7, result, "DoubleInPlace should return the original value");
        AssertEqual(14, value, "DoubleInPlace must write the doubled value back to the caller");
        TestLogger.Info($"DoubleInPlace(ref 7) returned {result}, value now {value}");
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

    #region Underscore (`_:`) argument-label projection

    // Swift `_:` parameters lose external-label information at the
    // swiftinterface boundary. The C# emitter must invent a name; the
    // failure modes we guard against are the literal underscore `_`,
    // positional placeholders (`value0`, `value1`, …), and lowercased
    // typedef-name leakage (e.g. `cGFloat` from a typealias projection).
    // The Swift fixtures live in UnderscoreLabels.swift and exercise the
    // function, method, and enum-case shapes.

    public void TestUnderscoreLabel_FreeFunctionParamHasSaneName()
    {
        // Generator preserves the Swift function's lowerCamelCase name on emit
        // (`UnderscoreLabel_progressValue`), so the reflection lookup is case-sensitive.
        // If a future generator change PascalCases it, fall back to a case-insensitive
        // lookup so this acceptance test fails for the right reason — the parameter
        // name — rather than the method name.
        var method = typeof(TestLibFunctions).GetMethod("UnderscoreLabel_progressValue")
            ?? typeof(TestLibFunctions).GetMethod(
                "UnderscoreLabel_progressValue",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)
            ?? throw new InvalidOperationException(
                "UnderscoreLabel_progressValue method missing — fixture regressed or " +
                "function-emission pipeline dropped the `_:` overload.");
        var parameters = method.GetParameters();
        AssertEqual(1, parameters.Length, "UnderscoreLabel_progressValue must take exactly one parameter");
        AssertParameterNameIsSane(parameters[0].Name, $"{method.Name}.<param 0>");
        // Pin the exact role-derived name: `Double` → `value` via
        // NameProvider.DeriveParameterNameFromType. A bare-shape regression
        // (e.g. `param`, `arg0`) would pass the sanity helper while losing
        // the role-based derived name.
        AssertEqual("value", parameters[0].Name,
            $"{method.Name}.<param 0>: Double `_:` parameter must derive to `value`");
    }

    public void TestUnderscoreLabel_MethodParamHasSaneName()
    {
        var target = typeof(UnderscoreLabelTarget);
        var method = target.GetMethod("ContentsGravity")
            ?? throw new InvalidOperationException(
                "UnderscoreLabelTarget.ContentsGravity missing — method-emission " +
                "pipeline dropped the `_:` second parameter.");
        var parameters = method.GetParameters();
        AssertEqual(1, parameters.Length, "ContentsGravity must take exactly one parameter");
        AssertParameterNameIsSane(parameters[0].Name, "UnderscoreLabelTarget.ContentsGravity.<param 0>");
        // Pin the lifted external label: `func contentsGravity(for _: Int32)`
        // → external label `for` lifted onto the C# parameter. Generator emits
        // `@for` to escape the C# keyword; reflection reports the unescaped name.
        AssertEqual("for", parameters[0].Name,
            "UnderscoreLabelTarget.ContentsGravity.<param 0>: external label `for` must lift onto the parameter name");
    }

    public void TestUnderscoreLabel_EnumCaseFactoryParamHasSaneName()
    {
        // Enum-case-with-`_:`-payload projects as a static factory method on
        // the C# enum mirror class. Locate the factory and check its
        // first parameter name.
        var enumType = typeof(UnderscoreLabelPlaybackMode)
            ?? throw new InvalidOperationException(
                "UnderscoreLabelPlaybackMode type missing — enum emission regressed.");
        var progressFactory =
            enumType.GetMethod("Progress")
            ?? enumType.GetMethod("Progress",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        AssertTrue(progressFactory is not null,
            "UnderscoreLabelPlaybackMode.Progress factory must exist (enum case " +
            "`progress(_: Double)` projects as static factory method).");
        var parameters = progressFactory!.GetParameters();
        AssertTrue(parameters.Length >= 1,
            "UnderscoreLabelPlaybackMode.Progress factory must take at least one parameter.");
        AssertParameterNameIsSane(parameters[0].Name, "UnderscoreLabelPlaybackMode.Progress.<param 0>");
        // Pin the single-payload role-derived name: enum case `progress(_: Double)`
        // → `value` (single payload drops the `{i}` suffix; multi-payload cases
        // like `marker(_: String, playEndMarker: Bool)` retain `value0`).
        AssertEqual("value", parameters[0].Name,
            "UnderscoreLabelPlaybackMode.Progress.<param 0>: single-payload `_:` case must derive to `value`");
    }

    // Helper: rejects the three documented failure modes. Permits any other
    // identifier so the assertion stays robust against future renaming.
    private void AssertParameterNameIsSane(string? name, string context)
    {
        AssertTrue(!string.IsNullOrEmpty(name),
            $"{context}: parameter must have a name (null/empty fails C# naming requirements).");

        // Mode 3: literal underscore. Legal C# but collides with the
        // discard-pattern symbol and trips static analyzers.
        AssertTrue(name != "_",
            $"{context}: parameter name `_` is the literal-underscore failure mode " +
            "(collides with C# discard pattern). Expected a synthesized identifier.");

        // Mode 2: positional placeholder `value0`, `value1`, …
        AssertTrue(!(name!.StartsWith("value") && name.Length > 5 && char.IsDigit(name[5])),
            $"{context}: parameter name `{name}` looks like the positional-placeholder " +
            "failure mode (`value0`, `value1`, …). Expected a meaningful synthesized identifier.");

        // Mode 1: lowercased typedef-name leakage. The original regression shape was
        // `cGFloat` from `AnimationProgressTime = CGFloat`. The
        // analog here is the typealias `UnderscoreLabelAnimationProgress`
        // — the lowercased-leak shape would project as
        // `underscoreLabelAnimationProgress`.
        AssertTrue(name != "underscoreLabelAnimationProgress" && name != "uNderscoreLabelAnimationProgress",
            $"{context}: parameter name `{name}` looks like the lowercased-typealias-leak " +
            "failure mode. Expected a synthesized identifier based on the parameter role.");
    }

    #endregion
}
