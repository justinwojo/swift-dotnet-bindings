// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Tests for Apple framework type marshalling: optional CoreGraphics types,
/// Foundation.Date patterns, CGFloat scaling, and mixed framework type structs.
/// </summary>
public class AppleFrameworkTypeTests : TestBase
{
    public AppleFrameworkTypeTests(TestResults results) : base(results) { }

    #region Optional CoreGraphics Types

    public void TestProcessOptionalPointSome()
    {
        var point = new Swift.CGPoint { X = 1.5, Y = 2.5 };
        var result = TestLibFunctions.ProcessOptionalPoint(point);
        AssertTrue(result.Contains("1.5"), "Optional point contains X");
        AssertTrue(result.Contains("2.5"), "Optional point contains Y");
        TestLogger.Info($"ProcessOptionalPoint(Some) = \"{result}\"");
    }

    public void TestProcessOptionalPointNone()
    {
        var result = TestLibFunctions.ProcessOptionalPoint(null);
        AssertEqual("nil", result, "Optional point None");
        TestLogger.Info($"ProcessOptionalPoint(None) = \"{result}\"");
    }

    public void TestProcessOptionalRectSome()
    {
        var rect = new Swift.CGRect
        {
            Origin = new Swift.CGPoint { X = 10.0, Y = 20.0 },
            Size = new Swift.CGSize { Width = 100.0, Height = 50.0 }
        };
        var result = TestLibFunctions.ProcessOptionalRect(rect);
        AssertTrue(result.Contains("10.0"), "Optional rect contains origin X");
        AssertTrue(result.Contains("100.0"), "Optional rect contains width");
        TestLogger.Info($"ProcessOptionalRect(Some) = \"{result}\"");
    }

    public void TestProcessOptionalRectNone()
    {
        var result = TestLibFunctions.ProcessOptionalRect(null);
        AssertEqual("nil", result, "Optional rect None");
        TestLogger.Info($"ProcessOptionalRect(None) = \"{result}\"");
    }

    public void TestScaleCGFloat()
    {
        var result = TestLibFunctions.ScaleCGFloat(5.0, 3.0);
        AssertEqual(15.0, result, "ScaleCGFloat(5,3) = 15");
        TestLogger.Info($"ScaleCGFloat(5.0, 3.0) = {result}");
    }

    public void TestScaleCGFloatZero()
    {
        var result = TestLibFunctions.ScaleCGFloat(100.0, 0.0);
        AssertEqual(0.0, result, "ScaleCGFloat(100,0) = 0");
        TestLogger.Info($"ScaleCGFloat(100.0, 0.0) = {result}");
    }

    #endregion

    #region Foundation.Date Patterns

    public void TestDateFromEpoch()
    {
        var date = TestLibFunctions.DateFromEpoch(0);
        // DateTimeOffset is a value type — verify it's a valid epoch-0 date
        AssertTrue(date.Year == 1970, "DateFromEpoch(0) returns 1970");
        TestLogger.Info($"DateFromEpoch(0) = {date}");
    }

    public void TestIsDateInPast()
    {
        // Epoch 0 (1970-01-01) should be in the past
        var date = TestLibFunctions.DateFromEpoch(0);
        var isPast = TestLibFunctions.IsDateInPast(date);
        AssertTrue(isPast, "Epoch 0 is in the past");
        TestLogger.Info($"IsDateInPast(epoch 0) = {isPast}");
    }

    public void TestProcessOptionalDateSome()
    {
        var date = TestLibFunctions.DateFromEpoch(1000000);
        var result = TestLibFunctions.ProcessOptionalDate(date);
        AssertTrue(result.Contains("1000000"), "Optional date contains epoch seconds");
        TestLogger.Info($"ProcessOptionalDate(Some) = \"{result}\"");
    }

    public void TestProcessOptionalDateNone()
    {
        var result = TestLibFunctions.ProcessOptionalDate(null);
        AssertEqual("nil", result, "Optional date None");
        TestLogger.Info($"ProcessOptionalDate(None) = \"{result}\"");
    }

    public void TestDateInTuple()
    {
        // A5: a Foundation.Date element inside a returned tuple must surface as
        // System.DateTimeOffset (epoch-converted), matching the scalar Date path — not a
        // bare double. Epoch 0 → 1970-01-01T00:00:00Z.
        var result = TestLibFunctions.DateEpochPair(0);
        AssertEqual(0, result.epoch, "DateEpochPair epoch element");
        AssertEqual(1970, result.date.Year, "DateEpochPair date element resolves to 1970");
        AssertEqual(0L, result.date.ToUnixTimeSeconds(), "DateEpochPair date round-trips epoch 0");
        TestLogger.Info($"DateEpochPair(0) = (date: {result.date:o}, epoch: {result.epoch})");
    }

    public void TestDateInTupleNonZeroEpoch()
    {
        // Non-zero epoch guards against a coincidental zero/default surfacing as a valid date.
        // 1_000_000_000s after 1970 → 2001-09-09T01:46:40Z.
        var result = TestLibFunctions.DateEpochPair(1_000_000_000);
        AssertEqual(1_000_000_000, result.epoch, "DateEpochPair non-zero epoch element");
        AssertEqual(1_000_000_000L, result.date.ToUnixTimeSeconds(), "DateEpochPair non-zero date round-trips");
        AssertEqual(2001, result.date.Year, "DateEpochPair non-zero date resolves to 2001");
        TestLogger.Info($"DateEpochPair(1e9) = (date: {result.date:o}, epoch: {result.epoch})");
    }

    #endregion

    #region Optional TimeInterval (Double)

    public void TestProcessOptionalTimeIntervalSome()
    {
        var result = TestLibFunctions.ProcessOptionalTimeInterval(42.5);
        AssertEqual("42.5", result, "Optional TimeInterval Some");
        TestLogger.Info($"ProcessOptionalTimeInterval(42.5) = \"{result}\"");
    }

    public void TestProcessOptionalTimeIntervalNone()
    {
        var result = TestLibFunctions.ProcessOptionalTimeInterval(null);
        AssertEqual("nil", result, "Optional TimeInterval None");
        TestLogger.Info($"ProcessOptionalTimeInterval(None) = \"{result}\"");
    }

    #endregion

    #region Mixed Framework Type Struct

    public void TestMakeFrameworkTypeHolder()
    {
        using var holder = TestLibFunctions.MakeFrameworkTypeHolder(10.0, 20.0, "test", 12345.0);
        var desc = holder.GetDescribe();
        AssertTrue(desc.Contains("test"), "FrameworkTypeHolder contains label");
        AssertTrue(desc.Contains("10.0"), "FrameworkTypeHolder contains X");
        TestLogger.Info($"FrameworkTypeHolder.Describe() = \"{desc}\"");
    }

    #endregion
}
