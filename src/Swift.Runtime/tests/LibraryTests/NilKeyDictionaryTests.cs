// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// <see cref="NilKeyDictionary{TKey, TValue}"/>: the dictionary a binding reads an optional-keyed
/// Swift dictionary into, where <c>nil</c> is a key like any other.
/// </summary>
public class NilKeyDictionaryTests
{
    [Fact]
    public void NilKey_IsOneEntryLikeAnyOther()
    {
        var dictionary = new NilKeyDictionary<string?, int> { [null] = 1, ["a"] = 2 };

        Assert.Equal(2, dictionary.Count);
        Assert.Equal(1, dictionary[null]);
        Assert.Equal(2, dictionary["a"]);
        Assert.True(dictionary.ContainsKey(null));
        Assert.True(dictionary.TryGetValue(null, out var nilValue));
        Assert.Equal(1, nilValue);
        Assert.Contains(new KeyValuePair<string?, int>(null, 1), dictionary);
        Assert.Contains(null, dictionary.Keys);
        Assert.Equal(new[] { 1, 2 }, dictionary.Values.OrderBy(v => v));

        dictionary[null] = 3;
        Assert.Equal(3, dictionary[null]);
        Assert.Equal(2, dictionary.Count);
    }

    [Fact]
    public void NilKey_AddRemoveAndMissingReads()
    {
        var dictionary = new NilKeyDictionary<string?, int>();
        Assert.False(dictionary.ContainsKey(null));
        Assert.False(dictionary.TryGetValue(null, out _));
        Assert.Throws<KeyNotFoundException>(() => dictionary[null]);
        Assert.False(dictionary.Remove(null));

        dictionary.Add(null, 4);
        Assert.Throws<ArgumentException>(() => dictionary.Add(null, 5));
        Assert.False(dictionary.Remove(new KeyValuePair<string?, int>(null, 5)));
        Assert.True(dictionary.Remove(new KeyValuePair<string?, int>(null, 4)));
        Assert.Empty(dictionary);

        dictionary.Add("a", 1);
        dictionary.Add(null, 2);
        dictionary.Clear();
        Assert.Empty(dictionary);
        Assert.False(dictionary.ContainsKey(null));
    }

    [Fact]
    public void From_KeepsANilKeyAndRefusesDuplicates()
    {
        var dictionary = NilKeyDictionary.From(new (string?, int)[] { ("x", 1), (null, 2) }, e => e.Item1, e => e.Item2);
        Assert.Equal(2, dictionary.Count);
        Assert.Equal(2, dictionary[null]);
        Assert.Equal(1, dictionary["x"]);

        Assert.Throws<ArgumentException>(() => NilKeyDictionary.From(new string?[] { null, null }, k => k, _ => 0));
        Assert.Throws<ArgumentException>(() => NilKeyDictionary.From(new[] { "x", "x" }, k => k, _ => 0));
    }

    [Fact]
    public void Entries_CopyAndEnumerateEveryKeyIncludingNil()
    {
        var dictionary = new NilKeyDictionary<string?, int>(new[]
        {
            new KeyValuePair<string?, int>("a", 1),
            new KeyValuePair<string?, int>(null, 0),
        });
        var copy = new KeyValuePair<string?, int>[3];
        dictionary.CopyTo(copy, 1);
        Assert.Equal(new int[] { 0, 1 }, copy.Skip(1).Select(e => e.Value).OrderBy(v => v));
        Assert.Throws<ArgumentException>(() => dictionary.CopyTo(new KeyValuePair<string?, int>[2], 1));

        IReadOnlyDictionary<string?, int> readOnly = dictionary;
        Assert.Equal(2, readOnly.Keys.Count());
        Assert.Equal(new int[] { 0, 1 }, readOnly.Select(e => e.Value).OrderBy(v => v));
    }

    [Fact]
    public void Comparer_AppliesToEveryKeyButNil()
    {
        var dictionary = new NilKeyDictionary<string?, int>(StringComparer.OrdinalIgnoreCase) { ["Key"] = 1, [null] = 2 };
        Assert.Equal(1, dictionary["KEY"]);
        Assert.Equal(2, dictionary[null]);
        Assert.Throws<ArgumentException>(() => dictionary.Add("key", 3));
    }
}
