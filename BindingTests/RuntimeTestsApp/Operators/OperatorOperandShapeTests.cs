// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Operators;

/// <summary>
/// Operators whose operands are not all the parent type (fixture: OperatorOperandShapes.swift).
/// The operators no route can carry are refused per member, so the rest of the type, and the
/// module, still bind; the positive controls on each type must return the Swift values.
/// </summary>
public class OperatorOperandShapeTests : TestBase
{
    public OperatorOperandShapeTests(TestResults results) : base(results) { }

    static string[] OperatorSignatures([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] System.Type type)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name.StartsWith("op_", System.StringComparison.Ordinal))
            .Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})->{m.ReturnType.Name}")
            .OrderBy(s => s, System.StringComparer.Ordinal)
            .ToArray();

    public void TestValueTypeOperatorWithPlainOperandBinds()
    {
        using var value = new OperandShapeValue(6);
        AssertEqual((nint)42, value * (nint)7, "the Int-operand operator returns the Swift product");
    }

    public void TestValueTypeWideOptionalOperatorsAreRefused()
    {
        var signatures = OperatorSignatures(typeof(OperandShapeValue));
        AssertFalse(signatures.Any(s => s.StartsWith("op_Addition", System.StringComparison.Ordinal)),
            $"`+ (_, Int?)` has no carrier conversion and must not bind; found {string.Join("; ", signatures)}");
        AssertFalse(signatures.Any(s => s.StartsWith("op_Subtraction", System.StringComparison.Ordinal)),
            "`- (_, String?)` must not bind");
        AssertFalse(signatures.Any(s => s.StartsWith("op_Division", System.StringComparison.Ordinal)),
            "`/` returning Int? must not bind");
    }

    public void TestClassOperatorWithPlainOperandBinds()
    {
        var box = new OperandShapeBox(9);
        AssertEqual((nint)27, box * (nint)3, "the class Int-operand operator returns the Swift product");
        AssertFalse(OperatorSignatures(typeof(OperandShapeBox)).Any(s => s.StartsWith("op_Addition", System.StringComparison.Ordinal)),
            "`+ (_, Double?)` on the class must not bind");
    }

    public void TestFrozenHomogeneousOperatorsBind()
    {
        var sum = new OperandShapeFrozen(40) + new OperandShapeFrozen(2);
        AssertEqual((nint)42, sum.BaseNative, "the homogeneous `+` calls the homogeneous Swift overload");
        AssertTrue(new OperandShapeFrozen(5) == new OperandShapeFrozen(5), "`==` sees equal values");
        AssertFalse(new OperandShapeFrozen(5) == new OperandShapeFrozen(6), "`==` sees different values");
    }

    public void TestFrozenHeterogeneousOperatorsAreRefused()
    {
        var signatures = OperatorSignatures(typeof(OperandShapeFrozen));
        var additions = signatures.Where(s => s.StartsWith("op_Addition", System.StringComparison.Ordinal)).ToArray();
        AssertEqual(1, additions.Length,
            $"only the homogeneous `+` binds; the `+ (_, Int)` overload would call it on an Int's bytes. Found {string.Join("; ", signatures)}");
        AssertFalse(signatures.Any(s => s.StartsWith("op_Subtraction", System.StringComparison.Ordinal)),
            "`- (_, Int?)` must not bind");
        AssertFalse(signatures.Any(s => s.StartsWith("op_Division", System.StringComparison.Ordinal)),
            "`/` returning Int? must not bind");
    }

    public void TestSynthesizedPartnersComeFromTheBoundOverload()
    {
        // A refused `< (_, Int?)` is declared before each homogeneous `<`; the `>` C# requires must
        // pair with the homogeneous one.
        using var small = new OperandShapeValue(1);
        using var large = new OperandShapeValue(2);
        AssertTrue(small < large, "`<` calls the homogeneous Swift overload");
        AssertTrue(large > small, "the synthesized `>` swaps the operands of the homogeneous `<`");
        AssertFalse(small > large, "the synthesized `>` is false for ordered operands");
        var valueSignatures = OperatorSignatures(typeof(OperandShapeValue));
        AssertEqual(1, valueSignatures.Count(s => s.StartsWith("op_LessThan(", System.StringComparison.Ordinal)),
            $"only the homogeneous `<` binds; found {string.Join("; ", valueSignatures)}");

        var low = new OperandShapeFrozen(1);
        var high = new OperandShapeFrozen(2);
        AssertTrue(low < high, "the frozen `<` calls the homogeneous Swift overload");
        AssertTrue(high > low, "the frozen synthesized `>` swaps the operands of the homogeneous `<`");
        AssertFalse(low > high, "the frozen synthesized `>` is false for ordered operands");
        AssertTrue(low != high, "`!=` sees different values");
        var frozenSignatures = OperatorSignatures(typeof(OperandShapeFrozen));
        AssertEqual(1, frozenSignatures.Count(s => s.StartsWith("op_LessThan(", System.StringComparison.Ordinal)),
            $"only the homogeneous frozen `<` binds; found {string.Join("; ", frozenSignatures)}");
    }
    public void TestEveryOverloadOfASymbolGetsItsOwnPartner()
    {
        // Two `==` and three `<` overloads over different operands: each needs a partner over its own
        // operands, synthesized from that overload.
        using var three = new OperandShapeOverloads(3);
        using var otherThree = new OperandShapeOverloads(3);
        using var five = new OperandShapeOverloads(5);

        AssertTrue(three == otherThree, "the homogeneous `==` sees equal values");
        AssertFalse(three != otherThree, "the homogeneous synthesized `!=` negates it");
        AssertTrue(three != five, "the homogeneous synthesized `!=` sees different values");
        AssertTrue(three == (nint)3, "the Int-operand `==` compares with the Int");
        AssertFalse(three != (nint)3, "the Int-operand synthesized `!=` negates the Int-operand `==`");
        AssertTrue(three != (nint)4, "the Int-operand synthesized `!=` sees a different Int");
        AssertTrue(three.Equals((object)otherThree), "Equals(object) agrees with the homogeneous `==`");
        AssertFalse(three.Equals((object)five), "Equals(object) agrees with the homogeneous `==` on different values");

        AssertTrue(three < five, "the homogeneous `<`");
        AssertTrue(five > three, "the homogeneous synthesized `>`");
        AssertTrue(three < (nint)4, "the (value, Int) `<`");
        AssertTrue(five > (nint)4, "the (value, Int) synthesized `>` swaps through the (Int, value) `<`");
        AssertFalse(three > (nint)4, "the (value, Int) synthesized `>` is false for ordered operands");
        AssertTrue((nint)4 < five, "the (Int, value) `<`");
        AssertTrue((nint)4 > three, "the (Int, value) synthesized `>` swaps through the (value, Int) `<`");
        AssertFalse((nint)4 > five, "the (Int, value) synthesized `>` is false for ordered operands");

        var signatures = OperatorSignatures(typeof(OperandShapeOverloads));
        AssertEqual(2, signatures.Count(s => s.StartsWith("op_Equality(", System.StringComparison.Ordinal)), $"both `==` bind; found {string.Join("; ", signatures)}");
        AssertEqual(2, signatures.Count(s => s.StartsWith("op_Inequality(", System.StringComparison.Ordinal)), "each `==` has its `!=`");
        AssertEqual(3, signatures.Count(s => s.StartsWith("op_LessThan(", System.StringComparison.Ordinal)), "all three `<` bind");
        AssertEqual(3, signatures.Count(s => s.StartsWith("op_GreaterThan(", System.StringComparison.Ordinal)), "each `<` has its `>`");
    }
}
