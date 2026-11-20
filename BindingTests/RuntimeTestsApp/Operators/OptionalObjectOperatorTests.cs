// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Foundation;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Operators;

/// <summary>
/// Runtime gate for <c>OptionalObjectOperators.swift</c>: class and struct operators that return an
/// Optional object (an ObjC class, <c>NSObject</c> itself, a Swift class) or take an Optional ObjC
/// operand. An operator body writes its own call, and these shapes once failed the module's compile:
/// the raw handle of an Optional ObjC result was returned as the projected type, and an Optional
/// operand's converted local was never declared. Each operator is called with an input for which
/// Swift returns nil and one for which it returns an object, and the object is checked by value or
/// by identity.
/// </summary>
public class OptionalObjectOperatorTests : TestBase
{
    public OptionalObjectOperatorTests(TestResults results) : base(results) { }

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

    #region Optional ObjC result

    public void TestClassOperatorReturnsOptionalObjCObject()
    {
        using var two = new OptionalObjectOperatorClass(2, null);
        using var five = new OptionalObjectOperatorClass(5, null);
        using var minusTwo = new OptionalObjectOperatorClass(-2, null);
        RunClassOperator("OptionalObjectOperatorClass.operator +", () =>
        {
            for (int i = 0; i < 3; i++)
            {
                using var sum = two + five;
                AssertNotNull(sum, "a non-zero sum comes back as an NSNumber");
                AssertEqual(7, sum!.Int32Value, "the NSNumber holds the sum Swift built");
            }
            AssertNull(two + minusTwo, "a zero sum comes back as nil");
        });
    }

    public void TestClassPrefixOperatorReturnsOptionalObjCObject()
    {
        using var ten = new OptionalObjectOperatorClass(10, null);
        using var zero = new OptionalObjectOperatorClass(0, null);
        RunClassOperator("OptionalObjectOperatorClass.operator ~", () =>
        {
            using var negated = ~ten;
            AssertNotNull(negated, "a non-zero value comes back as an NSNumber");
            AssertEqual(-10, negated!.Int32Value, "the NSNumber holds the negated value");
            AssertNull(~zero, "a zero value comes back as nil");
        });
    }

    public void TestClassOperatorReturnsTheSameNSObject()
    {
        // The operator hands back the object the caller attached, at +1. It has to arrive as the
        // caller's own managed peer, with that +1 released once, so repeated calls leave the
        // object's retain count where it was.
        using var attachment = new NSObject();
        using var holder = new OptionalObjectOperatorClass(1, attachment);
        using var positive = new OptionalObjectOperatorClass(3, null);
        using var negative = new OptionalObjectOperatorClass(-3, null);
        RunClassOperator("OptionalObjectOperatorClass.operator &", () =>
        {
            var retainCount = attachment.RetainCount;
            for (int i = 0; i < 3; i++)
            {
                var returned = holder & positive;
                AssertNotNull(returned, "the attachment comes back for a non-negative right operand");
                AssertTrue(ReferenceEquals(attachment, returned), "the attachment comes back as the caller's own object");
                AssertEqual(attachment.Handle, returned!.Handle, "the attachment comes back as the same native object");
            }
            AssertEqual(retainCount, attachment.RetainCount, "each returned +1 is released exactly once");
            AssertNull(holder & negative, "a negative right operand returns nil");
        });
    }

    public void TestStructOperatorReturnsOptionalObjCObject()
    {
        using var two = new OptionalObjectOperatorValue(2);
        using var five = new OptionalObjectOperatorValue(5);
        using var minusTwo = new OptionalObjectOperatorValue(-2);
        for (int i = 0; i < 3; i++)
        {
            using var sum = two + five;
            AssertNotNull(sum, "a non-zero sum comes back as an NSNumber");
            AssertEqual(7, sum!.Int32Value, "the NSNumber holds the sum Swift built");
        }
        AssertNull(two + minusTwo, "a zero sum comes back as nil");
    }

    #endregion

    #region Optional Swift class result

    public void TestClassOperatorReturnsOptionalSwiftClass()
    {
        using var five = new OptionalObjectOperatorClass(5, null);
        using var two = new OptionalObjectOperatorClass(2, null);
        using (var difference = five - two)
        {
            AssertNotNull(difference, "a non-zero difference comes back as an instance");
            AssertEqual(3, difference!.Value, "the instance holds the difference");
            AssertNull(difference.Attachment, "the new instance has no attachment");
        }
        AssertNull(five - five, "a zero difference comes back as nil");
    }

    public void TestStructOperatorReturnsOptionalSwiftClass()
    {
        using var five = new OptionalObjectOperatorValue(5);
        using var two = new OptionalObjectOperatorValue(2);
        using (var difference = five - two)
        {
            AssertNotNull(difference, "a non-zero difference comes back as an instance");
            AssertEqual(3, difference!.Value, "the instance holds the difference");
        }
        AssertNull(five - five, "a zero difference comes back as nil");
    }

    #endregion

    #region Optional ObjC operand

    public void TestClassOperatorTakesOptionalObjCOperand()
    {
        using var two = new OptionalObjectOperatorClass(2, null);
        using var factor = NSNumber.FromInt32(21);
        RunClassOperator("OptionalObjectOperatorClass.operator *", () =>
        {
            AssertEqual(42, two * factor, "Swift reads the operand's value");
            AssertEqual(-1, two * (NSNumber?)null, "a nil operand reaches Swift as nil");
            AssertEqual(21, factor.Int32Value, "the operand is still alive after the call");
        });
    }

    public void TestStructOperatorTakesOptionalObjCOperand()
    {
        using var two = new OptionalObjectOperatorValue(2);
        using var factor = NSNumber.FromInt32(21);
        AssertEqual(42, two * factor, "Swift reads the operand's value");
        AssertEqual(-1, two * (NSNumber?)null, "a nil operand reaches Swift as nil");
        AssertEqual(21, factor.Int32Value, "the operand is still alive after the call");
    }

    #endregion
}
