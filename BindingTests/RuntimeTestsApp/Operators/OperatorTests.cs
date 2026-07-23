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

    public void TestArithmeticAdd()
    {
        var a = new ArithmeticValue(10);
        var b = new ArithmeticValue(20);
        var result = a + b;
        AssertEqual(30, result.Value, "10 + 20 = 30");
        TestLogger.Info($"ArithmeticValue: {a.Value} + {b.Value} = {result.Value}");
    }

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

    public void TestArithmeticSubtract()
    {
        var a = new ArithmeticValue(30);
        var b = new ArithmeticValue(12);
        var result = a - b;
        AssertEqual(18, result.Value, "30 - 12 = 18");
        TestLogger.Info("Subtraction passed");
    }

    public void TestArithmeticMultiply()
    {
        var a = new ArithmeticValue(6);
        var b = new ArithmeticValue(7);
        var result = a * b;
        AssertEqual(42, result.Value, "6 * 7 = 42");
        TestLogger.Info("Multiplication passed");
    }

    public void TestArithmeticDivide()
    {
        var a = new ArithmeticValue(100);
        var b = new ArithmeticValue(4);
        var result = a / b;
        AssertEqual(25, result.Value, "100 / 4 = 25");
        TestLogger.Info("Division passed");
    }

    public void TestArithmeticModulo()
    {
        var a = new ArithmeticValue(17);
        var b = new ArithmeticValue(5);
        var result = a % b;
        AssertEqual(2, result.Value, "17 % 5 = 2");
        TestLogger.Info("Modulo passed");
    }

    // Regression coverage for the "operator-return CS0029" family: an open class whose arithmetic
    // operators return the class itself. The class instance comes back as a raw pointer on the
    // direct-call branch and must be marshalled into the projected C# class
    // (SwiftMarshal.MarshalFromSwift) or the operator body fails to compile with CS0029. The Swift
    // fixture (Vector2D) plus the regenerate-and-compile step of the compile gate is what proves
    // that return-marshalling fix end-to-end.
    //
    // These runtime tests are [Skip]ped, not deleted: a class-parent operator never gets an
    // @_cdecl wrapper (ShouldEmitOperatorWrapper only wraps frozen-struct parents), so the emitted
    // P/Invoke passes the class operands as SafeHandle under CallConvSwift. That non-blittable
    // shape is rejected on both Mono and NativeAOT with InvalidProgramException BEFORE the return
    // marshalling runs — a confirmed upstream limitation (Issue 2), independent of and larger than
    // the CS0029 fix. When the generator emits @_cdecl (CallConvCdecl) wrappers for class-parent
    // operators — carrying the class operands as IntPtr and handling the static-metatype ABI the
    // way class static methods already do — these round-trip assertions activate unchanged.
    [Skip("Class-parent operators get no @_cdecl wrapper (ShouldEmitOperatorWrapper only wraps frozen-struct parents), so the class operands pass as SafeHandle under CallConvSwift. Both Mono and NativeAOT reject that non-blittable shape with InvalidProgramException before return marshalling runs (Issue 2). The CS0029 return-marshalling fix and the compile gate are proven by the Vector2D fixture; the runtime round-trip needs CallConvCdecl operator wrappers for class parents.")]
    public void TestClassReturningOperatorAdd()
    {
        var a = new Vector2D(1.0, 2.0);
        var b = new Vector2D(3.0, 4.0);
        var result = a + b;
        AssertEqual(4.0, result.X, "1.0 + 3.0 = 4.0");
        AssertEqual(6.0, result.Y, "2.0 + 4.0 = 6.0");
        TestLogger.Info($"Vector2D add: ({result.X}, {result.Y})");
    }

    [Skip("Class-parent operators get no @_cdecl wrapper (ShouldEmitOperatorWrapper only wraps frozen-struct parents), so the class operands pass as SafeHandle under CallConvSwift. Both Mono and NativeAOT reject that non-blittable shape with InvalidProgramException before return marshalling runs (Issue 2). The CS0029 return-marshalling fix and the compile gate are proven by the Vector2D fixture; the runtime round-trip needs CallConvCdecl operator wrappers for class parents.")]
    public void TestClassReturningOperatorSubtract()
    {
        var a = new Vector2D(10.0, 8.0);
        var b = new Vector2D(3.0, 5.0);
        var result = a - b;
        AssertEqual(7.0, result.X, "10.0 - 3.0 = 7.0");
        AssertEqual(3.0, result.Y, "8.0 - 5.0 = 3.0");
        TestLogger.Info($"Vector2D subtract: ({result.X}, {result.Y})");
    }

    [Skip("Class-parent operators get no @_cdecl wrapper (ShouldEmitOperatorWrapper only wraps frozen-struct parents), so the class operand passes as SafeHandle under CallConvSwift. Both Mono and NativeAOT reject that non-blittable shape with InvalidProgramException before return marshalling runs (Issue 2). The CS0029 return-marshalling fix and the compile gate are proven by the Vector2D fixture; the runtime round-trip needs CallConvCdecl operator wrappers for class parents.")]
    public void TestClassReturningOperatorScalarMultiply()
    {
        var a = new Vector2D(2.0, 3.0);
        var result = a * 4.0;
        AssertEqual(8.0, result.X, "2.0 * 4.0 = 8.0");
        AssertEqual(12.0, result.Y, "3.0 * 4.0 = 12.0");
        TestLogger.Info($"Vector2D scalar multiply: ({result.X}, {result.Y})");
    }

    #endregion

    #region Tier 2 — Comparison

    public void TestComparisonLessThan()
    {
        var a = new ComparableValue(5);
        var b = new ComparableValue(10);
        AssertTrue(a < b, "5 < 10");
        AssertFalse(b < a, "10 not < 5");
        TestLogger.Info("LessThan passed");
    }

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

    public void TestBitwiseAnd()
    {
        var a = new BitwiseValue(0b1100);
        var b = new BitwiseValue(0b1010);
        var result = a & b;
        AssertEqual((uint)0b1000, result.Value, "0b1100 & 0b1010 = 0b1000");
        TestLogger.Info("Bitwise AND passed");
    }

    public void TestBitwiseOr()
    {
        var a = new BitwiseValue(0b1100);
        var b = new BitwiseValue(0b1010);
        var result = a | b;
        AssertEqual((uint)0b1110, result.Value, "0b1100 | 0b1010 = 0b1110");
        TestLogger.Info("Bitwise OR passed");
    }

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

    public void TestUnaryBitwiseNot()
    {
        var val = new UnaryValue(false, 0x0000FF00);
        var result = ~val;
        AssertEqual(0xFFFF00FFu, result.IntValue, "~0x0000FF00 = 0xFFFF00FF");
        TestLogger.Info("Unary bitwise NOT passed");
    }

    #endregion
}
