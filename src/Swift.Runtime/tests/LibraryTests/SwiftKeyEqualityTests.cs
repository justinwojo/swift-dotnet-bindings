// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Keys Swift alone can compare: an optional value key (<c>[Int?: V]</c>, <c>Set&lt;String?&gt;</c>), which
/// Swift hashes through the standard library's conditional <c>Optional: Hashable</c>, and the comparer a
/// binding gives a managed copy of a Swift collection whose key type's managed equality is not Swift's.
/// </summary>
public class SwiftKeyEqualityTests
{
    private static SwiftOptional<nint> Some(nint value) => SwiftOptional<nint>.NewSome(value);

    private static SwiftOptional<nint> None() => SwiftOptional<nint>.NewNone();

    [Fact]
    public void OptionalIntKeys_HashThroughOptionalsConditionalConformance()
    {
        using var dictionary = new SwiftDictionary<SwiftOptional<nint>, nint>();
        dictionary[Some(1)] = 10;
        dictionary[None()] = 20;
        dictionary[Some(2)] = 30;

        Assert.Equal(3, dictionary.Count);
        Assert.True(dictionary.TryGetValue(Some(1), out var one));
        Assert.Equal((nint)10, one);
        Assert.True(dictionary.TryGetValue(None(), out var nil));
        Assert.Equal((nint)20, nil);
        Assert.False(dictionary.ContainsKey(Some(3)));

        dictionary[Some(1)] = 11;
        Assert.Equal(3, dictionary.Count);
        Assert.Equal(new long?[] { null, 1, 2 }, dictionary.Keys.Select(k => k.HasValue ? (long?)k.Some : null).OrderBy(k => k));
    }

    [Fact]
    public void OptionalStringElements_HashThroughOptionalsConditionalConformance()
    {
        using var set = new SwiftSet<SwiftOptional<SwiftString>>();
        set.Add(SwiftOptional<SwiftString>.NewSome(new SwiftString("alpha")));
        set.Add(SwiftOptional<SwiftString>.NewNone());
        set.Add(SwiftOptional<SwiftString>.NewSome(new SwiftString("alpha")));

        Assert.Equal(2, set.Count);
        Assert.Contains(SwiftOptional<SwiftString>.NewNone(), set);
        Assert.Contains(SwiftOptional<SwiftString>.NewSome(new SwiftString("alpha")), set);
        Assert.DoesNotContain(SwiftOptional<SwiftString>.NewSome(new SwiftString("beta")), set);
    }

    [Fact]
    public void Comparer_ComparesAndHashesAsSwiftDoes()
    {
        // Swift's String equality is canonical equivalence: a precomposed "é" equals "e" followed by
        // a combining acute accent, which .NET's ordinal string equality calls different.
        var comparer = SwiftKeyComparer.Create<string, SwiftString>(key => new SwiftString(key));
        const string precomposed = "café";
        const string decomposed = "café";

        Assert.NotEqual(precomposed, decomposed);
        Assert.True(comparer.Equals(precomposed, decomposed));
        Assert.Equal(comparer.GetHashCode(precomposed), comparer.GetHashCode(decomposed));
        Assert.False(comparer.Equals(precomposed, "cafe"));

        var index = new Dictionary<string, int>(comparer) { [precomposed] = 1 };
        Assert.True(index.TryGetValue(decomposed, out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void Comparer_NilEqualsOnlyNil()
    {
        var comparer = SwiftKeyComparer.Create<string?, SwiftString>(key => new SwiftString(key!));

        Assert.True(comparer.Equals(null, null));
        Assert.False(comparer.Equals(null, "a"));
        Assert.False(comparer.Equals("a", null));
        Assert.Equal(0, comparer.GetHashCode(null!));
    }

    [Fact]
    public void Comparer_OptionalCarrier_ComparesThroughOptional()
    {
        var comparer = SwiftKeyComparer.Create<long?, SwiftOptional<nint>>(key => Some((nint)key!.Value));

        Assert.True(comparer.Equals(5, 5));
        Assert.False(comparer.Equals(5, 6));
        Assert.Equal(comparer.GetHashCode(5), comparer.GetHashCode(5));
    }

    [Fact]
    public void NilKeyDictionary_TakesTheComparerForEveryNonNilKey()
    {
        var comparer = SwiftKeyComparer.Create<string?, SwiftString>(key => new SwiftString(key!));
        var source = new[] { ("café", 1), ((string?)null, 2) };

        var dictionary = NilKeyDictionary.From(source, p => p.Item1, p => p.Item2, comparer);

        Assert.Equal(2, dictionary.Count);
        Assert.Equal(1, dictionary["café"]);
        Assert.Equal(2, dictionary[null]);
        Assert.Throws<ArgumentException>(() => dictionary.Add("café", 3));
    }
}
