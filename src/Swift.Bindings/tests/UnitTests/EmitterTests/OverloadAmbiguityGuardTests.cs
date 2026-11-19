// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit tests for <see cref="OverloadAmbiguityGuard"/> — the set-validity check that keeps the
/// generator from emitting an overload set a consumer cannot call (CS0121).
///
/// Every ambiguity case below is stated as the call site that would fail, because that is the only
/// thing the rule is about: an argument list that binds two candidates equally well.
/// </summary>
public class OverloadAmbiguityGuardTests
{
    #region ParseKey

    [Fact]
    public void ParseKey_SplitsNameAndParameterTypes()
    {
        var shape = OverloadAmbiguityGuard.ParseKey("Configure(string,int)", requiredCount: 1);
        Assert.Equal("Configure", shape.Name);
        Assert.Equal(new[] { "string", "int" }, shape.ParameterTypes);
        Assert.Equal(1, shape.RequiredCount);
    }

    [Fact]
    public void ParseKey_NoParameters_YieldsEmptyTypeList()
    {
        var shape = OverloadAmbiguityGuard.ParseKey("Reset()", requiredCount: 0);
        Assert.Equal("Reset", shape.Name);
        Assert.Empty(shape.ParameterTypes);
    }

    [Fact]
    public void ParseKey_GenericArgumentCommasDoNotSplitParameters()
    {
        // A single IReadOnlyDictionary<string,int> parameter — its interior comma is not a separator.
        var shape = OverloadAmbiguityGuard.ParseKey(
            "Apply(global::System.Collections.Generic.IReadOnlyDictionary<string,int>,int)",
            requiredCount: 2);
        Assert.Equal(2, shape.ParameterTypes.Count);
        Assert.Equal(
            "global::System.Collections.Generic.IReadOnlyDictionary<string,int>",
            shape.ParameterTypes[0]);
        Assert.Equal("int", shape.ParameterTypes[1]);
    }

    [Fact]
    public void ParseKey_GenericAritySuffixStaysWithTheName()
    {
        // Two candidates with different method-generic arity are never an overload tie in C#, so the
        // `N marker has to travel with the name rather than be discarded.
        var one = OverloadAmbiguityGuard.ParseKey("Map(int)`1", requiredCount: 1);
        var two = OverloadAmbiguityGuard.ParseKey("Map(int)`2", requiredCount: 1);
        Assert.NotEqual(one.Name, two.Name);
    }

    [Fact]
    public void ParseKey_RequiredCountIsClampedToArity()
    {
        // Callers pass int.MaxValue to mean "unknown / treat as fully required".
        var shape = OverloadAmbiguityGuard.ParseKey("Configure(string,int)", requiredCount: int.MaxValue);
        Assert.Equal(2, shape.RequiredCount);
    }

    #endregion

    #region AreAmbiguous

    [Fact]
    public void AreAmbiguous_DifferentNames_NeverAmbiguous()
    {
        var a = OverloadAmbiguityGuard.ParseKey("Alpha(int,int)", requiredCount: 0);
        var b = OverloadAmbiguityGuard.ParseKey("Beta(int)", requiredCount: 0);
        Assert.False(OverloadAmbiguityGuard.AreAmbiguous(a, b));
    }

    [Fact]
    public void AreAmbiguous_ShorterCandidateSuppliesEveryArgument_NotAmbiguous()
    {
        // Foo(int) vs Foo(int, int = 0): the call Foo(1) supplies every parameter of the first, and
        // C# prefers the candidate needing no default substitution. Emitting both is legal.
        var full = OverloadAmbiguityGuard.ParseKey("Foo(int,int)", requiredCount: 1);
        var trimmed = OverloadAmbiguityGuard.ParseKey("Foo(int)", requiredCount: 1);
        Assert.False(OverloadAmbiguityGuard.AreAmbiguous(full, trimmed));
    }

    [Fact]
    public void AreAmbiguous_BothSidesSubstituteDefaults_IsAmbiguous()
    {
        // FooAsync(int, int = 3, CancellationToken = default) vs FooAsync(int, CancellationToken = default).
        // The call FooAsync(1) substitutes defaults on BOTH, so neither is better — CS0121.
        var full = OverloadAmbiguityGuard.ParseKey("FooAsync(int,int,CancellationToken)", requiredCount: 1);
        var trimmed = OverloadAmbiguityGuard.ParseKey("FooAsync(int,CancellationToken)", requiredCount: 1);
        Assert.True(OverloadAmbiguityGuard.AreAmbiguous(full, trimmed));
        Assert.True(OverloadAmbiguityGuard.AreAmbiguous(trimmed, full));
    }

    [Fact]
    public void AreAmbiguous_NoOptionalsOnEitherSide_NotAmbiguous()
    {
        // Both fully required: every applicable call supplies all of one candidate's parameters.
        var full = OverloadAmbiguityGuard.ParseKey("Foo(int,int,int)", requiredCount: 3);
        var trimmed = OverloadAmbiguityGuard.ParseKey("Foo(int)", requiredCount: 1);
        Assert.False(OverloadAmbiguityGuard.AreAmbiguous(full, trimmed));
    }

    [Fact]
    public void AreAmbiguous_PrefixTypesDiffer_NotAmbiguous()
    {
        // Foo(string, int = 0) vs Foo(int, int = 0, int = 0): no argument list reaches both by
        // ordinal type identity, so this rule does not fire (it is deliberately blind to ties that
        // only exist through implicit conversions).
        var a = OverloadAmbiguityGuard.ParseKey("Foo(string,int)", requiredCount: 1);
        var b = OverloadAmbiguityGuard.ParseKey("Foo(int,int,int)", requiredCount: 1);
        Assert.False(OverloadAmbiguityGuard.AreAmbiguous(a, b));
    }

    [Fact]
    public void AreAmbiguous_EqualAritySharedPrefixBothOptional_IsAmbiguous()
    {
        // Foo(int, int = 0) vs Foo(int, string = ""): the call Foo(1) reaches both and substitutes a
        // default on each. Equal arity is NOT an exemption.
        var a = OverloadAmbiguityGuard.ParseKey("Foo(int,int)", requiredCount: 1);
        var b = OverloadAmbiguityGuard.ParseKey("Foo(int,string)", requiredCount: 1);
        Assert.True(OverloadAmbiguityGuard.AreAmbiguous(a, b));
    }

    [Fact]
    public void AreAmbiguous_IdenticalSignature_NotReportedHere()
    {
        // One signature written twice is the exact-key dedup's job. Reporting it here would let a
        // reservation be found ambiguous with itself and delete a member that has no conflict.
        var a = OverloadAmbiguityGuard.ParseKey("Foo(int,int)", requiredCount: 1);
        var b = OverloadAmbiguityGuard.ParseKey("Foo(int,int)", requiredCount: 1);
        Assert.False(OverloadAmbiguityGuard.AreAmbiguous(a, b));
    }

    [Fact]
    public void AreAmbiguous_BothFullyOptionalAndOneIsAPrefixOfTheOther_IsAmbiguous()
    {
        // Foo() reaches Foo(int = 0) and Foo(int = 0, int = 0) alike.
        var a = OverloadAmbiguityGuard.ParseKey("Foo(int)", requiredCount: 0);
        var b = OverloadAmbiguityGuard.ParseKey("Foo(int,int)", requiredCount: 0);
        Assert.True(OverloadAmbiguityGuard.AreAmbiguous(a, b));
    }

    #endregion

    #region FindAmbiguousReservation

    [Fact]
    public void FindAmbiguousReservation_ReturnsTheConflictingKey()
    {
        var reservedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "FooAsync(int,int,CancellationToken)",
            "BarAsync(int,CancellationToken)",
        };
        var reservedShapes = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["FooAsync(int,int,CancellationToken)"] = 1,
            ["BarAsync(int,CancellationToken)"] = 1,
        };

        var conflict = OverloadAmbiguityGuard.FindAmbiguousReservation(
            reservedKeys, reservedShapes, "FooAsync(int,CancellationToken)", candidateRequiredCount: 1);

        Assert.Equal("FooAsync(int,int,CancellationToken)", conflict);
    }

    [Fact]
    public void FindAmbiguousReservation_CandidateWithNoOptionals_ReturnsNull()
    {
        var reservedKeys = new HashSet<string>(StringComparer.Ordinal) { "Foo(int,int)" };
        var reservedShapes = new Dictionary<string, int>(StringComparer.Ordinal) { ["Foo(int,int)"] = 0 };

        var conflict = OverloadAmbiguityGuard.FindAmbiguousReservation(
            reservedKeys, reservedShapes, "Foo(int)", candidateRequiredCount: 1);

        Assert.Null(conflict);
    }

    [Fact]
    public void FindAmbiguousReservation_UnrecordedShapeIsTreatedAsFullyRequired()
    {
        // A producer that reserves a key without recording its shape must degrade to today's
        // behavior (no suppression), never to a spurious one.
        var reservedKeys = new HashSet<string>(StringComparer.Ordinal) { "FooAsync(int,int,CancellationToken)" };

        var conflict = OverloadAmbiguityGuard.FindAmbiguousReservation(
            reservedKeys, reservedShapes: null, "FooAsync(int,CancellationToken)", candidateRequiredCount: 1);

        Assert.Null(conflict);
    }

    [Fact]
    public void FindAmbiguousReservation_ExactSameKey_ReturnsNull()
    {
        // Re-checking a key that is already reserved must not report a self-conflict; the exact-key
        // dedup owns that case.
        var reservedKeys = new HashSet<string>(StringComparer.Ordinal) { "FooAsync(int,CancellationToken)" };
        var reservedShapes = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["FooAsync(int,CancellationToken)"] = 1,
        };

        var conflict = OverloadAmbiguityGuard.FindAmbiguousReservation(
            reservedKeys, reservedShapes, "FooAsync(int,CancellationToken)", candidateRequiredCount: 1);

        Assert.Null(conflict);
    }

    [Fact]
    public void FindAmbiguousReservation_NoReservations_ReturnsNull()
    {
        Assert.Null(OverloadAmbiguityGuard.FindAmbiguousReservation(
            reservedKeys: null, reservedShapes: null, "Foo(int)", candidateRequiredCount: 0));
    }

    #endregion

    #region RecordReservation

    [Fact]
    public void RecordReservation_NullDictionary_IsANoOp()
    {
        // The unit harness constructs environments with no reservation table at all.
        OverloadAmbiguityGuard.RecordReservation(null, "Foo(int)", 1);
    }

    [Fact]
    public void RecordReservation_StoresTheRequiredCount()
    {
        var shapes = new Dictionary<string, int>(StringComparer.Ordinal);
        OverloadAmbiguityGuard.RecordReservation(shapes, "Foo(int,int)", 1);
        Assert.Equal(1, shapes["Foo(int,int)"]);
    }

    #endregion

    #region AreSeparated

    [Theory]
    // A position whose two types admit no common argument tells every full call apart.
    [InlineData("global::Foundation.NSUrl,string?", "byte[],global::Kit.Config", true)]
    [InlineData("string,nint", "nint,nint", true)]
    [InlineData("bool", "int", true)]
    [InlineData("int[]", "bool[]", true)]
    [InlineData("string", "global::Kit.Config", true)]
    // Numeric types share arguments through implicit conversions and constants, so any mix ties:
    // (int, long) against (long, int) is ambiguous for two int arguments, and the guard does not
    // try to rank the rest.
    [InlineData("int,long", "long,int", false)]
    [InlineData("int,nint", "nint,nint", false)]
    [InlineData("int", "uint", false)]
    // Named types may be related (a base and its derived class), interfaces, enums or carry
    // conversions, so they never separate a pair on their own.
    [InlineData("global::Kit.Derived,global::Kit.Base", "global::Kit.Base,global::Kit.Derived", false)]
    [InlineData("int", "global::Kit.Mode", false)]
    [InlineData("byte[]", "global::System.Collections.Generic.IReadOnlyList<byte>", false)]
    [InlineData("string", "global::Kit.IThing", false)]
    [InlineData("string", "object", false)]
    [InlineData("string", "global::Foundation.NSString", false)]
    [InlineData("string,int", "string,int,int", false)]
    public void AreSeparated_OnlyWhenSomePositionAdmitsNoCommonArgument(string a, string b, bool expected)
    {
        Assert.Equal(expected, OverloadAmbiguityGuard.AreSeparated(a.Split(','), b.Split(',')));
        Assert.Equal(expected, OverloadAmbiguityGuard.AreSeparated(b.Split(','), a.Split(',')));
    }

    #endregion
}
