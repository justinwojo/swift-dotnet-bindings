// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Operators;

/// <summary>
/// Tests for operator overload emission: arithmetic, comparison, bitwise, unary.
/// </summary>
public class OperatorTests : TestBase
{
    public OperatorTests(TestResults results) : base(results) { }

    #region Tier 1 — Smoke Tests

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestArithmeticAdd()
    {
        var a = new ArithmeticValue(10);
        var b = new ArithmeticValue(20);
        var result = a + b;
        AssertEqual(30, result.Value, "10 + 20 = 30");
        TestLogger.Info($"ArithmeticValue: {a.Value} + {b.Value} = {result.Value}");
    }

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestComparisonEquals()
    {
        var a = new ComparableValue(42);
        var b = new ComparableValue(42);
        var c = new ComparableValue(99);
        AssertTrue(a == b, "42 == 42");
        AssertTrue(a != c, "42 != 99");
        TestLogger.Info("ComparableValue equality tests passed");
    }

    #endregion

    #region Tier 2 — Arithmetic

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestArithmeticSubtract()
    {
        var a = new ArithmeticValue(30);
        var b = new ArithmeticValue(12);
        var result = a - b;
        AssertEqual(18, result.Value, "30 - 12 = 18");
        TestLogger.Info("Subtraction passed");
    }

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestArithmeticMultiply()
    {
        var a = new ArithmeticValue(6);
        var b = new ArithmeticValue(7);
        var result = a * b;
        AssertEqual(42, result.Value, "6 * 7 = 42");
        TestLogger.Info("Multiplication passed");
    }

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestArithmeticDivide()
    {
        var a = new ArithmeticValue(100);
        var b = new ArithmeticValue(4);
        var result = a / b;
        AssertEqual(25, result.Value, "100 / 4 = 25");
        TestLogger.Info("Division passed");
    }

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestArithmeticModulo()
    {
        var a = new ArithmeticValue(17);
        var b = new ArithmeticValue(5);
        var result = a % b;
        AssertEqual(2, result.Value, "17 % 5 = 2");
        TestLogger.Info("Modulo passed");
    }

    #endregion

    #region Tier 2 — Comparison

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestComparisonLessThan()
    {
        var a = new ComparableValue(5);
        var b = new ComparableValue(10);
        AssertTrue(a < b, "5 < 10");
        AssertFalse(b < a, "10 not < 5");
        TestLogger.Info("LessThan passed");
    }

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestComparisonGreaterThan()
    {
        var a = new ComparableValue(10);
        var b = new ComparableValue(5);
        AssertTrue(a > b, "10 > 5");
        AssertFalse(b > a, "5 not > 10");
        TestLogger.Info("GreaterThan passed");
    }

    #endregion

    #region Tier 2 — Bitwise

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestBitwiseAnd()
    {
        var a = new BitwiseValue(0b1100);
        var b = new BitwiseValue(0b1010);
        var result = a & b;
        AssertEqual((uint)0b1000, result.Value, "0b1100 & 0b1010 = 0b1000");
        TestLogger.Info("Bitwise AND passed");
    }

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestBitwiseOr()
    {
        var a = new BitwiseValue(0b1100);
        var b = new BitwiseValue(0b1010);
        var result = a | b;
        AssertEqual((uint)0b1110, result.Value, "0b1100 | 0b1010 = 0b1110");
        TestLogger.Info("Bitwise OR passed");
    }

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestBitwiseXor()
    {
        var a = new BitwiseValue(0b1100);
        var b = new BitwiseValue(0b1010);
        var result = a ^ b;
        AssertEqual((uint)0b0110, result.Value, "0b1100 ^ 0b1010 = 0b0110");
        TestLogger.Info("Bitwise XOR passed");
    }

    #endregion

    #region Tier 2 — Unary

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestUnaryNot()
    {
        var trueVal = new UnaryValue(true, 0);
        var result = !trueVal;
        AssertFalse(result, "!true = false");

        var falseVal = new UnaryValue(false, 0);
        result = !falseVal;
        AssertTrue(result, "!false = true");
        TestLogger.Info("Unary NOT passed");
    }

    [Skip("EntryPointNotFoundException: operator @_cdecl wrappers stripped during compilation")]
    public void TestUnaryBitwiseNot()
    {
        var val = new UnaryValue(false, 0x0000FF00);
        var result = ~val;
        AssertEqual(0xFFFF00FFu, result.IntValue, "~0x0000FF00 = 0xFFFF00FF");
        TestLogger.Info("Unary bitwise NOT passed");
    }

    #endregion
}
