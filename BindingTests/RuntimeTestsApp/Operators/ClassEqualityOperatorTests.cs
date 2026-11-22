// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Operators;

/// <summary>
/// Classes that declare <c>==</c> without conforming to Equatable themselves. C# needs
/// <c>Equals</c>/<c>GetHashCode</c> beside the operator, and the pair must agree with each other
/// and with Swift's own <c>Hashable</c> where the class has one. Fixture:
/// Operators/ClassEqualityOperators.swift.
/// </summary>
public class ClassEqualityOperatorTests : TestBase
{
    public ClassEqualityOperatorTests(TestResults results) : base(results) { }

    public void TestRootClassEqualsFollowsOperator()
    {
        using var a = new OperatorEqualityRoot(1);
        using var b = new OperatorEqualityRoot(1);
        using var c = new OperatorEqualityRoot(2);
        AssertTrue(a == b, "== compares keys");
        AssertFalse(a == c, "== distinguishes keys");
        AssertTrue(a.Equals(b), "Equals agrees with ==");
        AssertFalse(a.Equals(c), "Equals distinguishes keys");
        AssertEqual(a.GetHashCode(), b.GetHashCode(), "Equal values hash alike");
        AssertEqual(2, new HashSet<OperatorEqualityRoot> { a, b, c }.Count, "HashSet merges equal values");
    }

    public void TestDerivedClassEqualsFollowsItsOwnOperator()
    {
        using var a = new OperatorEqualityDerived(4);
        using var b = new OperatorEqualityDerived(4);
        using var c = new OperatorEqualityDerived(5);
        AssertTrue(a == b, "Derived == compares keys");
        AssertTrue(a.Equals(b), "Derived Equals agrees with its ==");
        AssertTrue(((object)a).Equals(b), "Equals through object agrees");
        AssertFalse(a.Equals(c), "Derived Equals distinguishes keys");
        AssertEqual(a.GetHashCode(), b.GetHashCode(), "Equal values hash alike");
        AssertEqual(2, new HashSet<OperatorEqualityDerived> { a, b, c }.Count, "HashSet merges equal values");
    }

    public void TestObjCDerivedEqualsFollowsSwiftHashable()
    {
        using var a = new OperatorEqualityObjCDerived(6);
        using var b = new OperatorEqualityObjCDerived(6);
        AssertTrue(a == b, "The class's own == compares keys");
        var swiftMerges = TestLibFunctions.OperatorObjCSetCount(a, b) == 1;
        AssertEqual(swiftMerges, a.Equals(b), "Equals agrees with Swift's Hashable (NSObject isEqual:)");
        AssertTrue(a.Equals(a), "Equals is reflexive");
        if (a.Equals(b))
            AssertEqual(a.GetHashCode(), b.GetHashCode(), "Equal values hash alike");
        var expected = swiftMerges ? 1 : 2;
        AssertEqual(expected, new HashSet<OperatorEqualityObjCDerived> { a, b }.Count, "HashSet matches Swift's Set");
    }

    public void TestSubclassOperatorBesideInheritedEquatable()
    {
        using var a = new OperatorEquatableDerived(8, 1);
        using var b = new OperatorEquatableDerived(8, 2);
        AssertFalse(a == b, "The subclass's stricter == compares both fields");
        AssertEqual(1, TestLibFunctions.OperatorEquatableSetCount(a, b), "Swift's Set uses the base Hashable (key only)");
        AssertTrue(a.Equals(b), "Equals follows the Equatable witness, as Swift's Set does");
        AssertEqual(a.GetHashCode(), b.GetHashCode(), "Equal values hash alike");
        AssertEqual(1, new HashSet<OperatorEquatableDerived> { a, b }.Count, "HashSet matches Swift's Set");
    }
    /// <summary>
    /// A subclass <c>==</c> floored above the running OS does not floor <c>Equals</c>, which reaches
    /// the base's inherited Equatable witness: <c>Equals</c> and <c>GetHashCode</c> work, and only the
    /// subclass's own <c>==</c> refuses with PlatformNotSupportedException before reaching Swift.
    /// </summary>
    public void TestFlooredSubclassOperatorDoesNotFloorInheritedEquality()
    {
        using var a = new OperatorEquatableFlooredDerived(9, 1);
        using var b = new OperatorEquatableFlooredDerived(9, 2);
        using var c = new OperatorEquatableFlooredDerived(10, 1);
        AssertTrue(a.Equals(b), "Equals follows the inherited witness (key only)");
        AssertFalse(a.Equals(c), "Equals distinguishes keys");
        AssertEqual(a.GetHashCode(), b.GetHashCode(), "Equal values hash alike");
        AssertEqual(1, new HashSet<OperatorEquatableFlooredDerived> { a, b }.Count, "HashSet merges witness-equal values");
        var op = typeof(OperatorEquatableFlooredDerived).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "op_Equality" && m.GetParameters().Length == 2
                && m.GetParameters()[0].ParameterType == typeof(OperatorEquatableFlooredDerived));
        AssertTrue(op != null, "The subclass's own == is bound");
        var thrown = false;
        try { op!.Invoke(null, new object[] { a, b }); }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is PlatformNotSupportedException) { thrown = true; }
        AssertTrue(thrown, "The subclass's iOS 99 == refuses before Swift");
    }
}
