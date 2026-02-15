// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Swift;
using Xunit;

namespace BindingsGeneration.Tests;

public class SwiftArrayProjectionTests
{
    [Fact]
    public void Count_DelegatesToSource()
    {
        var source = new SwiftArray<int>(new[] { 1, 2, 3 });
        var projection = source.AsProjected(x => x.ToString());
        Assert.Equal(3, projection.Count);
    }

    [Fact]
    public void Indexer_AppliesSelector()
    {
        var source = new SwiftArray<int>(new[] { 10, 20, 30 });
        var projection = source.AsProjected(x => x * 2);
        Assert.Equal(20, projection[0]);
        Assert.Equal(40, projection[1]);
        Assert.Equal(60, projection[2]);
    }

    [Fact]
    public void Indexer_LazyAccess()
    {
        int callCount = 0;
        var source = new SwiftArray<int>(new[] { 1, 2, 3 });
        var projection = source.AsProjected(x =>
        {
            callCount++;
            return x.ToString();
        });

        // No calls yet — projection is lazy
        Assert.Equal(0, callCount);

        // Accessing index 1 should invoke selector once
        _ = projection[1];
        Assert.Equal(1, callCount);

        // Accessing index 0 should invoke selector once more
        _ = projection[0];
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void Enumeration_AppliesSelector()
    {
        var source = new SwiftArray<int>(new[] { 1, 2, 3 });
        var projection = source.AsProjected(x => x + 100);
        var results = new List<int>();
        foreach (var item in projection)
        {
            results.Add(item);
        }
        Assert.Equal(new List<int> { 101, 102, 103 }, results);
    }

    [Fact]
    public void EmptyArray_ReturnsEmptyProjection()
    {
        var source = new SwiftArray<int>();
        var projection = source.AsProjected(x => x.ToString());
        Assert.Empty(projection);
    }

    [Fact]
    public void WithSwiftString_ProjectsToString()
    {
        var source = new SwiftArray<SwiftString>();
        source.Append(new SwiftString("Hello"));
        source.Append(new SwiftString("World"));

        var projection = source.AsProjected(e => e.ToString());
        Assert.Equal(2, projection.Count);
        Assert.Equal("Hello", projection[0]);
        Assert.Equal("World", projection[1]);
    }

    [Fact]
    public void BoundsCheck_ThrowsOnInvalidIndex()
    {
        var source = new SwiftArray<int>(new[] { 1, 2, 3 });
        var projection = source.AsProjected(x => x.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = projection[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = projection[3]);
    }

    [Fact]
    public void LiveView_ReflectsMutations()
    {
        var source = new SwiftArray<int>(new[] { 1, 2, 3 });
        var projection = source.AsProjected(x => x * 10);

        // Initial state
        Assert.Equal(3, projection.Count);
        Assert.Equal(10, projection[0]);

        // Mutate source
        source.Append(4);
        Assert.Equal(4, projection.Count);
        Assert.Equal(40, projection[3]);

        // Modify existing element
        source[0] = 99;
        Assert.Equal(990, projection[0]);
    }

    [Fact]
    public void AsIReadOnlyList_WorksWithLinq()
    {
        var source = new SwiftArray<int>(new[] { 1, 2, 3, 4, 5 });
        IReadOnlyList<string> projection = source.AsProjected(x => x.ToString());

        // Can use LINQ on IReadOnlyList
        Assert.Equal("3", projection.First(s => s == "3"));
        Assert.Equal(5, projection.Count);
    }
}
