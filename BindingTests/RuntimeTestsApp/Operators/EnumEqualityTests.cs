// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Operators;

/// <summary>
/// Layer A coverage for the "Equatable enum-as-class" lowering gap
/// (Defect 2: Equatable enums with associated values lowering to reference equality).
///
/// Equatable enums with associated values lower to a C# class. Before the
/// EnumEqualityMethodsWriter bridge, those classes inherited reference equality
/// from <see cref="object"/>, so two structurally identical instances compared
/// unequal and the synthesized Swift <c>==</c> was silently dropped.
///
/// These tests assert the projection at three layers:
///   * .NET reference equality is replaced by Swift's <c>==</c>.
///   * <c>GetHashCode()</c> agrees with <c>Equals()</c>.
///   * The C# operator and IEquatable&lt;T&gt; surfaces both call into Swift.
/// </summary>
public class EnumEqualityTests : TestBase
{
    public EnumEqualityTests(TestResults results) : base(results) { }

    public void TestEqualPayloadEnumValuesAreEqual()
    {
        var a = TestLibFunctions.EquatablePayloadInteger(7);
        var b = TestLibFunctions.EquatablePayloadInteger(7);

        AssertTrue(a.Equals(b), "Equals(other) must use Swift == for payload enum");
        AssertTrue(a == b, "operator == must route through the Swift wrapper");
        AssertTrue(!(a != b), "operator != must mirror operator ==");
        AssertEqual(a.GetHashCode(), b.GetHashCode(),
            "Equal payload values must produce the same hash code");

        // Swift authoritative cross-check.
        AssertTrue(TestLibFunctions.EquatablePayloadEnumSwiftEquals(a, b),
            "Swift's own == must agree with the C# projection");
    }

    public void TestDifferentPayloadEnumValuesAreNotEqual()
    {
        var a = TestLibFunctions.EquatablePayloadInteger(7);
        var b = TestLibFunctions.EquatablePayloadInteger(8);

        AssertTrue(!a.Equals(b), "Unequal integer payloads must not be Equals");
        AssertTrue(a != b, "operator != must report inequality");
        AssertTrue(!(a == b), "operator == must report inequality");
        AssertTrue(!TestLibFunctions.EquatablePayloadEnumSwiftEquals(a, b),
            "Swift cross-check");
    }

    public void TestDifferentCasesAreNotEqual()
    {
        var integerCase = TestLibFunctions.EquatablePayloadInteger(7);
        var labelledCase = TestLibFunctions.EquatablePayloadLabelled("seven", 7);
        var emptyCase = TestLibFunctions.GetEquatablePayloadEmpty();

        AssertTrue(!integerCase.Equals(labelledCase), "different cases compare unequal");
        AssertTrue(!labelledCase.Equals(emptyCase), "different cases compare unequal");
        AssertTrue(!emptyCase.Equals(integerCase), "different cases compare unequal");
    }

    public void TestLabelledPayloadEqualityRoundTrips()
    {
        var a = TestLibFunctions.EquatablePayloadLabelled("alpha", 3);
        var b = TestLibFunctions.EquatablePayloadLabelled("alpha", 3);
        var c = TestLibFunctions.EquatablePayloadLabelled("beta", 3);
        var d = TestLibFunctions.EquatablePayloadLabelled("alpha", 4);

        AssertTrue(a == b, "Identical labelled payloads must be ==");
        AssertTrue(a != c, "Different name field must be !=");
        AssertTrue(a != d, "Different count field must be !=");
        AssertEqual(a.GetHashCode(), b.GetHashCode(), "Equal labelled payloads share a hash");
    }

    public void TestPayloadEnumImplementsIEquatable()
    {
        var a = TestLibFunctions.GetEquatablePayloadEmpty();
        AssertTrue(a is IEquatable<EquatablePayloadEnum>,
            "Generated payload enum must expose IEquatable<T> after the lowering fix");
    }
}
