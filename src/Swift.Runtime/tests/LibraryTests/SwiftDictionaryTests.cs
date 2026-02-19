// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftDictionaryTests : IClassFixture<SwiftDictionaryTests.TestFixture>
{
    private readonly TestFixture _fixture;

    public SwiftDictionaryTests(TestFixture fixture)
    {
        _fixture = fixture;
    }

    public class TestFixture
    {
        static TestFixture()
        {
        }
    }

    private static SwiftString S(string s) => new SwiftString(s);

    [Fact]
    public void MetadataOnly()
    {
        var metadata = TypeMetadata.GetTypeMetadataOrThrow<SwiftDictionary<SwiftString, nint>>();
        Assert.Equal((nuint)8, metadata.Size);
    }

    [Fact]
    public void InitOnly()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        Assert.NotNull(dict);
    }

    [Fact]
    public void CountOnly()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        var c = dict.Count;
        Assert.True(c == 0);
    }

    [Fact]
    public void SetterOnly()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k = S("key");
        dict[k] = 42;
        Assert.True(dict.Count == 1);
    }

    [Fact]
    public void GetterOnly()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k = S("key");
        dict[k] = 42;
        using var k2 = S("key");
        var val = dict[k2];
        Assert.True(val == 42);
    }

    [Fact]
    public void EnumerateEmpty()
    {
        // Enumerate an empty dict — tests MakeIterator + IteratorNext(.none)
        using var dict = new SwiftDictionary<SwiftString, nint>();
        var entries = dict.ToList();
        Assert.True(entries.Count == 0);
    }

    [Fact]
    public void EnumerateNonEmpty()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k = S("key");
        dict[k] = 42;
        // Verify dict is valid after setting
        Assert.True(dict.Count == 1);
        using var k2 = S("key");
        Assert.True(dict.TryGetValue(k2, out var val));
        Assert.True(val == 42);
        // Now enumerate
        var entries = dict.ToList();
        Assert.True(entries.Count == 1);
    }

    [Fact]
    public void SmokeTest()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        Assert.Empty(dict);
    }

    [Fact]
    public void SetAndGet_BasicTypes()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k1 = S("a");
        using var k2 = S("b");
        dict[k1] = 100;
        dict[k2] = 200;
        Assert.Equal(2, dict.Count);

        using var l1 = S("a");
        using var l2 = S("b");
        Assert.Equal(100, dict[l1]);
        Assert.Equal(200, dict[l2]);
    }

    [Fact]
    public void TryGetValue_ExistingKey_ReturnsTrue()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k = S("hello");
        dict[k] = 99;

        using var lookup = S("hello");
        Assert.True(dict.TryGetValue(lookup, out var value));
        Assert.Equal(99, value);
    }

    [Fact]
    public void TryGetValue_MissingKey_ReturnsFalse()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k = S("exists");
        dict[k] = 10;

        using var missing = S("missing");
        Assert.False(dict.TryGetValue(missing, out var value));
        Assert.Equal(default, value);
    }

    [Fact]
    public void ContainsKey_ExistingAndMissing()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k = S("present");
        dict[k] = 50;

        using var l1 = S("present");
        using var l2 = S("absent");
        Assert.True(dict.ContainsKey(l1));
        Assert.False(dict.ContainsKey(l2));
    }

    [Fact]
    public void Indexer_MissingKey_ThrowsKeyNotFoundException()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k = S("nope");
        Assert.Throws<KeyNotFoundException>(() => _ = dict[k]);
    }

    [Fact]
    public void GetEnumerator_ReturnsAllEntries()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k1 = S("a");
        using var k2 = S("b");
        using var k3 = S("c");
        dict[k1] = 10;
        dict[k2] = 20;
        dict[k3] = 30;

        var entries = dict.ToList();
        Assert.Equal(3, entries.Count);

        var sorted = entries.OrderBy(kvp => kvp.Key.ToString()).ToList();
        Assert.Equal("a", sorted[0].Key.ToString());
        Assert.Equal(10, sorted[0].Value);
        Assert.Equal("b", sorted[1].Key.ToString());
        Assert.Equal(20, sorted[1].Value);
        Assert.Equal("c", sorted[2].Key.ToString());
        Assert.Equal(30, sorted[2].Value);
    }

    [Fact]
    public void GetEnumerator_ZeroValuedEntries_NotDropped()
    {
        // Regression: iterator must not confuse zero-valued entries with Optional.none.
        // The old byte-zero check would treat value=0 as .none and stop iterating.
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k1 = S("x");
        using var k2 = S("y");
        using var k3 = S("z");
        dict[k1] = 0;
        dict[k2] = 0;
        dict[k3] = 0;

        var entries = dict.ToList();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal(0, e.Value));
    }

    [Fact]
    public void GetEnumerator_SingleEntry_NotDropped()
    {
        // Edge case: dictionary with only one entry with value=0.
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k = S("only");
        dict[k] = 0;

        var entries = dict.ToList();
        Assert.Single(entries);
        Assert.Equal("only", entries[0].Key.ToString());
        Assert.Equal(0, entries[0].Value);
    }

    [Fact]
    public void Keys_ReturnsAllKeys()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k1 = S("alpha");
        using var k2 = S("beta");
        dict[k1] = 100;
        dict[k2] = 200;

        var keys = dict.Keys.Select(k => k.ToString()).OrderBy(k => k).ToList();
        Assert.Equal(new[] { "alpha", "beta" }, keys);
    }

    [Fact]
    public void Values_ReturnsAllValues()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k1 = S("a");
        using var k2 = S("b");
        dict[k1] = 100;
        dict[k2] = 200;

        var values = dict.Values.OrderBy(v => v).ToList();
        Assert.Equal(new nint[] { 100, 200 }, values);
    }

    [Fact]
    public void RemoveValue_ExistingKey_ReturnsOldValue()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k = S("key");
        dict[k] = 42;

        using var lookup = S("key");
        var removed = dict.RemoveValue(lookup);
        Assert.Equal(42, removed);
        Assert.Empty(dict);
    }

    [Fact]
    public void RemoveValue_MissingKey_ReturnsDefault()
    {
        // Regression: RemoveValue must check Optional.none before marshalling.
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k = S("exists");
        dict[k] = 10;

        using var missing = S("missing");
        var removed = dict.RemoveValue(missing);
        Assert.Equal(default, removed);
        Assert.Single(dict); // original entry untouched
    }

    [Fact]
    public void RemoveAll_ClearsEntries()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k1 = S("a");
        using var k2 = S("b");
        dict[k1] = 10;
        dict[k2] = 20;
        dict.RemoveAll();

        Assert.Empty(dict);
    }

    [Fact]
    public void FromDictionary_CreatesFromKeyValuePairs()
    {
        using var k1 = S("x");
        using var k2 = S("y");
        using var k3 = S("z");
        var source = new List<KeyValuePair<SwiftString, nint>>
        {
            new(k1, 10),
            new(k2, 20),
            new(k3, 30),
        };

        using var dict = SwiftDictionary<SwiftString, nint>.FromDictionary(source);
        Assert.Equal(3, dict.Count);

        using var l1 = S("x");
        using var l2 = S("y");
        using var l3 = S("z");
        Assert.Equal(10, dict[l1]);
        Assert.Equal(20, dict[l2]);
        Assert.Equal(30, dict[l3]);
    }

    [Fact]
    public void AsProjected_ValueOnly_ProjectsValues()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k1 = S("a");
        using var k2 = S("b");
        dict[k1] = 10;
        dict[k2] = 20;

        IReadOnlyDictionary<SwiftString, string> projected = dict.AsProjected(v => v.ToString());

        using var l1 = S("a");
        using var l2 = S("b");
        Assert.Equal("10", projected[l1]);
        Assert.Equal("20", projected[l2]);
        Assert.Equal(2, projected.Count);
    }

    [Fact]
    public void AsProjected_KeyAndValue_ProjectsBoth()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k1 = S("a");
        using var k2 = S("b");
        dict[k1] = 10;
        dict[k2] = 20;

        IReadOnlyDictionary<string, string> projected = dict.AsProjected(
            k => k.ToString(),
            k => new SwiftString(k),
            v => v.ToString());

        Assert.Equal("10", projected["a"]);
        Assert.Equal("20", projected["b"]);
        Assert.True(projected.ContainsKey("a"));
        Assert.False(projected.ContainsKey("c"));
    }

    [Fact]
    public void IReadOnlyDictionary_Interface_Works()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        using var k1 = S("first");
        using var k2 = S("second");
        dict[k1] = 10;
        dict[k2] = 20;

        IReadOnlyDictionary<SwiftString, nint> iface = dict;
        Assert.Equal(2, iface.Count);

        using var l1 = S("first");
        using var l2 = S("second");
        Assert.Equal(10, iface[l1]);
        Assert.True(iface.ContainsKey(l1));
        Assert.True(iface.TryGetValue(l2, out var val));
        Assert.Equal(20, val);
    }

    [Fact]
    public void EmptyDictionary_Enumeration_ReturnsEmpty()
    {
        using var dict = new SwiftDictionary<SwiftString, nint>();
        Assert.Empty(dict.ToList());
        Assert.Empty(dict.Keys.ToList());
        Assert.Empty(dict.Values.ToList());
    }
}
