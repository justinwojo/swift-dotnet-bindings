// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Parameters;

/// <summary>
/// Tests for inout, default, and variadic parameters.
/// All are currently stubs — the generator emits these but the test bodies
/// need real implementations before they can be considered passing.
/// </summary>
public class ParameterTests : TestBase
{
    public ParameterTests(TestResults results) : base(results) { }

    #region Inout Parameters

    [Skip("Test stub — needs real implementation to verify ref parameter marshalling")]
    public void TestIncrementValue()
    {
        // incrementValue(_ value: inout Int32) — requires ref parameter marshalling
    }

    [Skip("Test stub — needs real implementation to verify ref parameter marshalling")]
    public void TestSwapValues()
    {
        // swapValues — requires two ref parameters
    }

    [Skip("Test stub — needs real implementation to verify ref parameter marshalling")]
    public void TestIncrementPoint()
    {
        // incrementPoint — inout on struct parameter
    }

    [Skip("Test stub — needs real implementation to verify ref parameter marshalling")]
    public void TestDoubleInPlace()
    {
        // doubleInPlace — inout with return value
    }

    #endregion

    #region Default Parameters

    [Skip("Test stub — needs real implementation to verify default parameter emission")]
    public void TestGreetDefault()
    {
        // greet(name:greeting:) has default greeting parameter
    }

    [Skip("Test stub — needs real implementation to verify default parameter emission")]
    public void TestSearchDefaults()
    {
        // search(query:limit:offset:) has multiple defaults
    }

    [Skip("Test stub — needs real implementation to verify default parameter emission")]
    public void TestConfigureDefaults()
    {
        // configure(host:port:secure:) has mixed defaults
    }

    #endregion

    #region Variadic Parameters

    [Skip("Test stub — needs real implementation to verify variadic parameter emission")]
    public void TestSumAll()
    {
        // sumAll(_ values: Int32...) — variadic Int32
    }

    [Skip("Test stub — needs real implementation to verify variadic parameter emission")]
    public void TestJoinStrings()
    {
        // joinStrings(_ strings: String...) — variadic String
    }

    [Skip("Test stub — needs real implementation to verify variadic parameter emission")]
    public void TestVariadicConsumer()
    {
        // VariadicConsumer.sumWithPrefix — variadic on struct method
    }

    #endregion
}
