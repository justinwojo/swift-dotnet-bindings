// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for optional value-type marshalling: frozen struct, non-frozen struct,
/// enum, and bool optional parameters and returns.
/// Covers value-type vs reference-type optional distinction in WrapperValidation.
/// </summary>
public class OptionalValueTypeTests : TestBase
{
    public OptionalValueTypeTests(TestResults results) : base(results) { }

    #region Optional Frozen Struct

    public void TestAcceptOptionalFrozenPointSome()
    {
        var point = new FrozenPoint(3.0, 4.0);
        var result = TestLibFunctions.AcceptOptionalFrozenPoint(point);
        AssertEqual("(3.0, 4.0)", result, "Optional frozen point Some");
        TestLogger.Info($"AcceptOptionalFrozenPoint(Some) = \"{result}\"");
    }

    public void TestAcceptOptionalFrozenPointNone()
    {
        var result = TestLibFunctions.AcceptOptionalFrozenPoint(null);
        AssertEqual("nil", result, "Optional frozen point None");
        TestLogger.Info($"AcceptOptionalFrozenPoint(None) = \"{result}\"");
    }

    public void TestMakeOptionalFrozenPointSome()
    {
        var point = TestLibFunctions.MakeOptionalFrozenPoint(5.0, 6.0, false);
        AssertTrue(point.HasValue, "MakeOptionalFrozenPoint Some should have value");
        TestLogger.Info("MakeOptionalFrozenPoint(Some) returned value");
    }

    public void TestMakeOptionalFrozenPointNone()
    {
        var point = TestLibFunctions.MakeOptionalFrozenPoint(0, 0, true);
        AssertFalse(point.HasValue, "MakeOptionalFrozenPoint None should not have value");
        TestLogger.Info("MakeOptionalFrozenPoint(None) has no value");
    }

    #endregion

    #region Optional Non-Frozen Struct

    public void TestAcceptOptionalNonFrozenPointSome()
    {
        using var point = new NonFrozenPoint(7.0, 8.0);
        var result = TestLibFunctions.AcceptOptionalNonFrozenPoint(point);
        AssertEqual("(7.0, 8.0)", result, "Optional non-frozen point Some");
        TestLogger.Info($"AcceptOptionalNonFrozenPoint(Some) = \"{result}\"");
    }

    public void TestAcceptOptionalNonFrozenPointNone()
    {
        var result = TestLibFunctions.AcceptOptionalNonFrozenPoint(null);
        AssertEqual("nil", result, "Optional non-frozen point None");
        TestLogger.Info($"AcceptOptionalNonFrozenPoint(None) = \"{result}\"");
    }

    #endregion

    #region Optional Enum

    public void TestAcceptOptionalColorSome()
    {
        var result = TestLibFunctions.AcceptOptionalColor(Color.Red);
        AssertEqual("red", result, "Optional Color.Red");
        TestLogger.Info($"AcceptOptionalColor(Red) = \"{result}\"");
    }

    public void TestAcceptOptionalColorGreen()
    {
        var result = TestLibFunctions.AcceptOptionalColor(Color.Green);
        AssertEqual("green", result, "Optional Color.Green");
        TestLogger.Info($"AcceptOptionalColor(Green) = \"{result}\"");
    }

    public void TestAcceptOptionalColorNone()
    {
        var result = TestLibFunctions.AcceptOptionalColor(null);
        AssertEqual("nil", result, "Optional Color None");
        TestLogger.Info($"AcceptOptionalColor(None) = \"{result}\"");
    }

    public void TestMakeOptionalColorSome()
    {
        var color = TestLibFunctions.MakeOptionalColor(0, false);
        AssertTrue(color.HasValue, "MakeOptionalColor Some should have value");
        TestLogger.Info("MakeOptionalColor(Some) returned value");
    }

    public void TestMakeOptionalColorNone()
    {
        var color = TestLibFunctions.MakeOptionalColor(0, true);
        AssertFalse(color.HasValue, "MakeOptionalColor None should not have value");
        TestLogger.Info("MakeOptionalColor(None) has no value");
    }

    #endregion

    #region Optional Bool

    public void TestAcceptOptionalBoolTrue()
    {
        var result = TestLibFunctions.AcceptOptionalBool(true);
        AssertEqual("true", result, "Optional Bool true");
        TestLogger.Info($"AcceptOptionalBool(true) = \"{result}\"");
    }

    public void TestAcceptOptionalBoolFalse()
    {
        var result = TestLibFunctions.AcceptOptionalBool(false);
        AssertEqual("false", result, "Optional Bool false");
        TestLogger.Info($"AcceptOptionalBool(false) = \"{result}\"");
    }

    public void TestAcceptOptionalBoolNone()
    {
        var result = TestLibFunctions.AcceptOptionalBool(null);
        AssertEqual("nil", result, "Optional Bool None");
        TestLogger.Info($"AcceptOptionalBool(None) = \"{result}\"");
    }

    public void TestMakeOptionalBoolSome()
    {
        var flag = TestLibFunctions.MakeOptionalBool(true, false);
        AssertTrue(flag.HasValue, "MakeOptionalBool Some should have value");
        AssertTrue(flag!.Value, "MakeOptionalBool(true) = true");
        TestLogger.Info("MakeOptionalBool(Some true) returned true");
    }

    public void TestMakeOptionalBoolNone()
    {
        var flag = TestLibFunctions.MakeOptionalBool(false, true);
        AssertFalse(flag.HasValue, "MakeOptionalBool None should not have value");
        TestLogger.Info("MakeOptionalBool(None) has no value");
    }

    #endregion
}
