// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Operators;

/// <summary>
/// Runtime gate for <c>OptionalValueOperators.swift</c>: class and struct operators that return a
/// one-word Optional value, and types that declare both the binary and the prefix form of one
/// operator symbol. The Optional result once came back as Swift's payload-and-tag word assigned to
/// the projected <c>int?</c> (a compile error that failed the module), and the binary and prefix forms
/// once shared one entry-point name the call reader could not tell apart (which also failed the
/// module). Each operator is called with an input for which Swift returns nil and one for which it
/// returns a value, and the binary and prefix forms are each checked to reach their own Swift body.
/// </summary>
public class OptionalValueOperatorTests : TestBase
{
    public OptionalValueOperatorTests(TestResults results) : base(results) { }

    private const SwiftTransitionFacts ClassOperatorShapes = SwiftTransitionFacts.SelfRegister | SwiftTransitionFacts.LoweredSignature;

    /// <summary>
    /// Runs <paramref name="call"/>, which must work wherever a bridge carries the class operator or
    /// the engine runs its direct transition (the metatype in the self register), and must refuse
    /// before Swift runs everywhere else.
    /// </summary>
    private void RunClassOperator(string member, Action call)
    {
        if (ImportRoutes.BridgesRouted || SwiftTransitionGuard.IsSupported(ClassOperatorShapes))
        {
            call();
            return;
        }
        SwiftTransitionUnsupportedException? refusal = null;
        try
        {
            call();
        }
        catch (SwiftTransitionUnsupportedException ex)
        {
            refusal = ex;
        }
        AssertNotNull(refusal, $"{member} must refuse on an engine that mishandles its direct transition");
        AssertTrue(refusal!.Member.Contains(member, StringComparison.Ordinal), $"refusal names the member: {refusal.Member}");
    }

    #region One-word Optional value result

    public void TestClassOperatorReturnsOptionalInt32()
    {
        using var two = new OptionalValueOperatorClass(2);
        using var five = new OptionalValueOperatorClass(5);
        using var minusTwo = new OptionalValueOperatorClass(-2);
        RunClassOperator("OptionalValueOperatorClass.operator +", () =>
        {
            for (int i = 0; i < 3; i++)
            {
                int? sum = two + five;
                AssertTrue(sum.HasValue, "a non-zero sum comes back as a value");
                AssertEqual(7, sum!.Value, "the value is the sum Swift computed");
            }
            AssertFalse((two + minusTwo).HasValue, "a zero sum comes back as nil");
        });
    }

    public void TestClassOperatorReturnsOptionalBool()
    {
        using var three = new OptionalValueOperatorClass(3);
        using var minusFour = new OptionalValueOperatorClass(-4);
        using var zero = new OptionalValueOperatorClass(0);
        RunClassOperator("OptionalValueOperatorClass.operator ^", () =>
        {
            bool? mixed = three ^ minusFour;
            AssertTrue(mixed.HasValue && mixed.Value, "one negative operand comes back as true");
            bool? same = three ^ three;
            AssertTrue(same.HasValue && !same.Value, "no negative operand comes back as false, not nil");
            AssertFalse((zero ^ zero).HasValue, "two zero operands come back as nil");
        });
    }

    public void TestStructOperatorReturnsOptionalInt32()
    {
        using var two = new OptionalValueOperatorValue(2);
        using var five = new OptionalValueOperatorValue(5);
        using var minusTwo = new OptionalValueOperatorValue(-2);
        for (int i = 0; i < 3; i++)
        {
            int? sum = two + five;
            AssertTrue(sum.HasValue, "a non-zero sum comes back as a value");
            AssertEqual(7, sum!.Value, "the value is the sum Swift computed");
        }
        AssertFalse((two + minusTwo).HasValue, "a zero sum comes back as nil");
    }

    #endregion

    #region Binary and prefix forms of one symbol

    public void TestClassBinaryAndPrefixMinusReachTheirOwnBodies()
    {
        using var nine = new OptionalValueOperatorClass(9);
        using var four = new OptionalValueOperatorClass(4);
        RunClassOperator("OptionalValueOperatorClass.operator -", () =>
        {
            AssertEqual(5, nine - four, "binary minus subtracts");
            AssertEqual(-9, -nine, "prefix minus negates");
        });
    }

    public void TestClassPrefixMinusSurfacesTheSwiftError()
    {
        using var zero = new OptionalValueOperatorClass(0);
        RunClassOperator("OptionalValueOperatorClass.operator -", () =>
        {
            SwiftException? caught = null;
            try
            {
                _ = -zero;
            }
            catch (SwiftException ex)
            {
                caught = ex;
            }
            AssertNotNull(caught, "negating zero throws the Swift error");
        });
    }

    public void TestClassBinaryAndPrefixPlusReachTheirOwnBodies()
    {
        using var six = new OptionalValueOperatorClass(6);
        RunClassOperator("OptionalValueOperatorClass.operator +", () =>
        {
            AssertEqual(12, +six, "prefix plus doubles");
            AssertEqual(12, (six + six)!.Value, "binary plus adds");
        });
    }

    public void TestStructBinaryAndPrefixMinusReachTheirOwnBodies()
    {
        using var nine = new OptionalValueOperatorValue(9);
        using var four = new OptionalValueOperatorValue(4);
        AssertEqual(5, nine - four, "binary minus subtracts");
        AssertEqual(-9, -nine, "prefix minus negates");
    }

    #endregion
}
