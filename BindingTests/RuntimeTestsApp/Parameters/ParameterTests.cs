// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// Tests for inout parameters. Default and variadic parameters are skipped.
/// </summary>
public class ParameterTests : TestBase
{
    public ParameterTests(TestResults results) : base(results) { }

    #region Inout Parameters

    [Skip("inout parameters: ref semantics not fully supported in P/Invoke")]
    public void TestIncrementValue()
    {
        // incrementValue(_ value: inout Int32) — requires ref parameter marshalling
    }

    [Skip("inout parameters: ref semantics not fully supported in P/Invoke")]
    public void TestSwapValues()
    {
        // swapValues — requires two ref parameters
    }

    [Skip("inout parameters: ref semantics not fully supported in P/Invoke")]
    public void TestIncrementPoint()
    {
        // incrementPoint — inout on struct parameter
    }

    [Skip("inout parameters: ref semantics not fully supported in P/Invoke")]
    public void TestDoubleInPlace()
    {
        // doubleInPlace — inout with return value
    }

    #endregion

    #region Default Parameters (Unsupported)

    [Skip("Default parameters not P/Invoke-expressible")]
    public void TestGreetDefault()
    {
        // greet(name:greeting:) has default greeting parameter
    }

    [Skip("Default parameters not P/Invoke-expressible")]
    public void TestSearchDefaults()
    {
        // search(query:limit:offset:) has multiple defaults
    }

    [Skip("Default parameters not P/Invoke-expressible")]
    public void TestConfigureDefaults()
    {
        // configure(host:port:secure:) has mixed defaults
    }

    #endregion

    #region Variadic Parameters (Unsupported)

    [Skip("Variadic parameters not supported in P/Invoke")]
    public void TestSumAll()
    {
        // sumAll(_ values: Int32...) — variadic Int32
    }

    [Skip("Variadic parameters not supported in P/Invoke")]
    public void TestJoinStrings()
    {
        // joinStrings(_ strings: String...) — variadic String
    }

    [Skip("Variadic parameters not supported in P/Invoke")]
    public void TestVariadicConsumer()
    {
        // VariadicConsumer.sumWithPrefix — variadic on struct method
    }

    #endregion
}
