// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for NameProvider.MakeNonCollidingSyntheticName() and SyntheticNameScope — the
/// reserved-synthetic-name guard (P1-22 infra). The guard escapes emitter-chosen synthetic
/// locals (tag, result, resultPtr, handle, session, userData, …) when a user's projected
/// parameter/member name spells the same identifier, so the generated C# does not trip
/// CS0136 (local shadows enclosing local/param) or CS0100 (duplicate parameter).
/// </summary>
public class NameProviderSyntheticNameTests
{
    private static IReadOnlySet<string> Reserved(params string[] names)
        => new HashSet<string>(names, StringComparer.Ordinal);

    #region MakeNonCollidingSyntheticName

    [Fact]
    public void NoCollision_ReturnsDesiredName()
    {
        Assert.Equal("result", NameProvider.MakeNonCollidingSyntheticName("result", Reserved("count", "value")));
    }

    [Fact]
    public void EmptyReservedSet_ReturnsDesiredName()
    {
        Assert.Equal("result", NameProvider.MakeNonCollidingSyntheticName("result", Reserved()));
    }

    // The full set of synthetic locals the P1-22 family targets — each must escape to "__name"
    // when a user identifier collides.
    [Theory]
    [InlineData("tag")]
    [InlineData("result")]
    [InlineData("resultPtr")]
    [InlineData("handle")]
    [InlineData("session")]
    [InlineData("userData")]
    public void Collision_PrefixesWithDoubleUnderscore(string synthetic)
    {
        Assert.Equal($"__{synthetic}", NameProvider.MakeNonCollidingSyntheticName(synthetic, Reserved(synthetic)));
    }

    [Fact]
    public void Collision_WithPrefixedFormAlsoReserved_EscalatesNumericSuffix()
    {
        // Both "result" and "__result" are taken by user identifiers → "__result2".
        Assert.Equal("__result2",
            NameProvider.MakeNonCollidingSyntheticName("result", Reserved("result", "__result")));
    }

    [Fact]
    public void Collision_WithMultiplePrefixedFormsReserved_FindsFirstFreeSuffix()
    {
        Assert.Equal("__result4",
            NameProvider.MakeNonCollidingSyntheticName("result",
                Reserved("result", "__result", "__result2", "__result3")));
    }

    [Fact]
    public void VerbatimReservedName_CollidesWithBareSynthetic()
    {
        // A user parameter that is a C# keyword arrives "@"-prefixed (e.g. "@event"); the bare
        // synthetic "event" must still be treated as colliding.
        Assert.Equal("__event", NameProvider.MakeNonCollidingSyntheticName("event", Reserved("@event")));
    }

    [Fact]
    public void VerbatimDesiredName_NormalizedBeforeCompare()
    {
        // Desired name carrying a "@" prefix compares on its bare form and, on collision, returns
        // the bare "__"-prefixed name (never "__@event").
        Assert.Equal("__event", NameProvider.MakeNonCollidingSyntheticName("@event", Reserved("event")));
    }

    [Fact]
    public void VerbatimDesiredName_NoCollision_ReturnsBareNeverAtPrefixed()
    {
        // Contract: the result is never "@"-prefixed. A free "@"-prefixed desired name must come
        // back stripped to its bare form, matching what the collision path produces ("__event").
        Assert.Equal("event", NameProvider.MakeNonCollidingSyntheticName("@event", Reserved("x")));
    }

    [Fact]
    public void VerbatimDesiredName_EmptyReservedSet_ReturnsBareNeverAtPrefixed()
    {
        // Same contract on the empty-reserved fast path: never leak the "@" prefix.
        Assert.Equal("event", NameProvider.MakeNonCollidingSyntheticName("@event", Reserved()));
    }

    [Fact]
    public void CaseSensitive_DistinctCasingDoesNotCollide()
    {
        // C# identifiers are case-sensitive: "Result" (a user member) does not collide with the
        // synthetic "result".
        Assert.Equal("result", NameProvider.MakeNonCollidingSyntheticName("result", Reserved("Result")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyOrNullDesiredName_Throws(string desired)
    {
        Assert.Throws<ArgumentException>(() =>
            NameProvider.MakeNonCollidingSyntheticName(desired, Reserved("x")));
    }

    [Fact]
    public void VerbatimPrefixOnly_StripsToEmpty_Throws()
    {
        // "@" carries no identifier after the verbatim prefix. Rejecting it keeps the contract
        // "the result is a valid bare identifier" honest — it would otherwise return "".
        Assert.Throws<ArgumentException>(() =>
            NameProvider.MakeNonCollidingSyntheticName("@", Reserved()));
        // SyntheticNameScope.Reserve delegates to the same guard.
        Assert.Throws<ArgumentException>(() => new SyntheticNameScope().Reserve("@"));
    }

    #endregion

    #region SyntheticNameScope

    [Fact]
    public void Scope_NoSeed_FirstReservePassesThrough()
    {
        var scope = new SyntheticNameScope();
        Assert.Equal("result", scope.Reserve("result"));
    }

    [Fact]
    public void Scope_SeededWithUserName_EscapesCollidingSynthetic()
    {
        var scope = new SyntheticNameScope(new[] { "result", "count" });
        Assert.Equal("__result", scope.Reserve("result"));
        // A non-colliding synthetic still passes through.
        Assert.Equal("resultPtr", scope.Reserve("resultPtr"));
    }

    [Fact]
    public void Scope_ReservingSameNameTwice_SecondEscapes()
    {
        var scope = new SyntheticNameScope();
        Assert.Equal("result", scope.Reserve("result"));
        // First reservation is now in-scope, so the second must avoid it.
        Assert.Equal("__result", scope.Reserve("result"));
    }

    [Fact]
    public void Scope_AllocatesMultipleDistinctSynthetics()
    {
        // User parameter named "result" forces the synthetic to escape; the rest are free and the
        // scope keeps all allocations distinct.
        var scope = new SyntheticNameScope(new[] { "result" });
        var allocated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var synthetic in new[] { "tag", "result", "resultPtr", "handle", "session", "userData" })
            Assert.True(allocated.Add(scope.Reserve(synthetic)), $"duplicate allocation for {synthetic}");

        Assert.Contains("__result", allocated);
        Assert.Contains("tag", allocated);
    }

    [Fact]
    public void Scope_IsReserved_ReflectsSeedAndAllocations()
    {
        var scope = new SyntheticNameScope(new[] { "@event" });
        // Seeded user name, normalized across the verbatim prefix.
        Assert.True(scope.IsReserved("event"));
        Assert.True(scope.IsReserved("@event"));
        Assert.False(scope.IsReserved("result"));

        scope.Reserve("result");
        Assert.True(scope.IsReserved("result"));
    }

    [Fact]
    public void Scope_NullSeedEntries_Ignored()
    {
        var scope = new SyntheticNameScope(new string[] { null, "", "result" });
        Assert.Equal("__result", scope.Reserve("result"));
        Assert.Equal("tag", scope.Reserve("tag"));
    }

    [Fact]
    public void Scope_VerbatimDesiredName_ReservedAsBare()
    {
        // Reserving a free "@"-prefixed synthetic returns the bare form (never "@event") and records
        // it under the normalized key, so a later bare "event" reservation escapes.
        var scope = new SyntheticNameScope();
        Assert.Equal("event", scope.Reserve("@event"));
        Assert.True(scope.IsReserved("event"));
        Assert.Equal("__event", scope.Reserve("event"));
    }

    #endregion
}
