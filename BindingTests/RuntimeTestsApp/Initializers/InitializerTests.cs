// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Initializers;

/// <summary>
/// Tests for BasicInit, ConvenienceInit, SafeDiv (failable), NonEmptyString (failable),
/// RangedInt (failable), and ValidatedConfig (throwing) initializer patterns.
/// </summary>
public class InitializerTests : TestBase
{
    public InitializerTests(TestResults results) : base(results) { }

    #region BasicInit (Frozen Struct — Multiple Overloads)

    public void TestBasicInitDefault()
    {
        var b = new BasicInit();
        AssertEqual(0, b.X, "Default init sets X=0");
        AssertEqual(0, b.Y, "Default init sets Y=0");
    }

    public void TestBasicInitSingleValue()
    {
        var b = new BasicInit(42);
        AssertEqual(42, b.X, "Single-value init sets X");
        AssertEqual(42, b.Y, "Single-value init sets Y equal to X");
    }

    public void TestBasicInitTwoValues()
    {
        var b = new BasicInit(10, 20);
        AssertEqual(10, b.X, "Two-value init sets X");
        AssertEqual(20, b.Y, "Two-value init sets Y");
    }

    #endregion

    #region ConvenienceInit (Class)

    public void TestConvenienceInitDesignated()
    {
        using var c = new ConvenienceInit("test", 42);
        AssertEqual("test", c.Name.ToString(), "Designated init sets name");
        AssertEqual(42, c.Value, "Designated init sets value");
    }

    public void TestConvenienceInitNameOnly()
    {
        using var c = new ConvenienceInit("nameonly");
        AssertEqual("nameonly", c.Name.ToString(), "Name-only convenience init sets name");
        AssertEqual(0, c.Value, "Name-only convenience init defaults value to 0");
    }

    public void TestConvenienceInitValueOnly()
    {
        using var c = new ConvenienceInit(99);
        AssertEqual("unnamed", c.Name.ToString(), "Value-only convenience init defaults name");
        AssertEqual(99, c.Value, "Value-only convenience init sets value");
    }

    public void TestConvenienceInitDefault()
    {
        using var c = new ConvenienceInit();
        AssertEqual("default", c.Name.ToString(), "Default convenience init sets name");
        AssertEqual(-1, c.Value, "Default convenience init sets value to -1");
    }

    #endregion

    #region SafeDiv (Failable Frozen Struct — TryCreate)

    public void TestSafeDivSuccess()
    {
        var ok = SafeDiv.TryCreate(10, 3, out var div);
        AssertTrue(ok, "TryCreate succeeds for valid denominator");
        AssertEqual(10, div.Numerator, "Numerator preserved");
        AssertEqual(3, div.Denominator, "Denominator preserved");
        AssertApproxEqual(3.333, div.Result, 0.01, "Result computed correctly");
    }

    public void TestSafeDivFailure()
    {
        var ok = SafeDiv.TryCreate(10, 0, out _);
        AssertFalse(ok, "TryCreate fails for zero denominator");
    }

    #endregion

    #region NonEmptyString (Failable Class-Projected Struct — TryCreate)

    public void TestNonEmptyStringSuccess()
    {
        // Failable initializer on a non-frozen (class-projected) struct: init?(_:) succeeds for a
        // non-empty string and the stored value + Swift-computed length round-trip.
        var ok = NonEmptyString.TryCreate("café", out var s);
        AssertTrue(ok, "NonEmptyString.TryCreate succeeds for a non-empty string");
        AssertNotNull(s, "successful TryCreate yields a non-null result");
        AssertEqual("café", s.Value, "Value round-trips the constructor string");
        AssertEqual(4, s.Length, "Length is the Swift String.count (grapheme count), not the UTF-8 byte count");
        s.Dispose();
        TestLogger.Info("NonEmptyString.TryCreate (initializer domain) success passed");
    }

    public void TestNonEmptyStringFailure()
    {
        var ok = NonEmptyString.TryCreate("", out var s);
        AssertFalse(ok, "NonEmptyString.TryCreate fails for an empty string (init? returns nil)");
        AssertNull(s, "failed TryCreate yields a null result");
        TestLogger.Info("NonEmptyString.TryCreate (initializer domain) failure passed");
    }

    #endregion

    #region RangedInt (Failable Frozen Struct — TryCreate)

    public void TestRangedIntSuccess()
    {
        var ok = RangedInt.TryCreate(5, 1, 10, out var ri);
        AssertTrue(ok, "TryCreate succeeds for value in range");
        AssertEqual(5, ri.Value, "Value preserved");
        AssertEqual(1, ri.Min, "Min preserved");
        AssertEqual(10, ri.Max, "Max preserved");
    }

    public void TestRangedIntTooLow()
    {
        var ok = RangedInt.TryCreate(0, 1, 10, out _);
        AssertFalse(ok, "TryCreate fails for value below min");
    }

    public void TestRangedIntTooHigh()
    {
        var ok = RangedInt.TryCreate(11, 1, 10, out _);
        AssertFalse(ok, "TryCreate fails for value above max");
    }

    public void TestRangedIntBoundary()
    {
        var okMin = RangedInt.TryCreate(1, 1, 10, out var riMin);
        AssertTrue(okMin, "TryCreate succeeds at min boundary");
        AssertEqual(1, riMin.Value, "Min boundary value preserved");

        var okMax = RangedInt.TryCreate(10, 1, 10, out var riMax);
        AssertTrue(okMax, "TryCreate succeeds at max boundary");
        AssertEqual(10, riMax.Value, "Max boundary value preserved");
    }

    #endregion

    #region ValidatedConfig (Throwing Init)

    public void TestValidatedConfigSuccess()
    {
        using var config = new ValidatedConfig("myapp", 30);
        AssertEqual("myapp", config.Name.ToString(), "Name preserved on valid config");
        AssertEqual(30, config.Timeout, "Timeout preserved on valid config");
    }

    public void TestValidatedConfigEmptyNameThrows()
    {
        AssertThrows<Exception>(() =>
        {
            using var config = new ValidatedConfig("", 30);
        }, "Empty name should throw");
    }

    public void TestValidatedConfigNegativeTimeoutThrows()
    {
        AssertThrows<Exception>(() =>
        {
            using var config = new ValidatedConfig("myapp", -1);
        }, "Negative timeout should throw");
    }

    #endregion

    #region ConstLiteralBox (_const-parameter init filter)

    // The wrapper-emit filter must drop @_cdecl wrappers for `_const` inits
    // (the wrapper passes a runtime variable through and Swift rejects it).
    // The regular (Int32, Int32) init must still get a wrapper and round-trip.
    // Type compilation is the primary gate — without the filter, the Wrapper.swift
    // file would fail to compile with "expect a compile-time constant literal".
    public void TestConstLiteralBoxRegularInitReachable()
    {
        var box = new ConstLiteralBox(3, 11);
        AssertEqual(3, (int)box.Lo, "Regular init sets Lo");
        AssertEqual(11, (int)box.Hi, "Regular init sets Hi");
    }

    #endregion
}
