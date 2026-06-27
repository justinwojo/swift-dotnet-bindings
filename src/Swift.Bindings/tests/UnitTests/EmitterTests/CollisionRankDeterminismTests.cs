// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The overload-collision disambiguation suffix is assigned in SOURCE/DECLARATION order.
/// <see cref="BaseHandler.BuildCollisionRankMap"/> is the single core both the class-member path
/// (<c>HandleBaseDecl</c>) and the free-function path (<c>ModuleHandler</c>) feed, in the order the
/// emission walk visits each overload. Within a same-projected-key group the first-declared overload
/// takes rank 0 (the natural, unsuffixed name) and later siblings ascend (rank 1 → <c>…2</c>). This
/// matches the C# surface earlier releases shipped — the first-declared overload is the bare name's
/// least-surprising owner. A genuine name↔symbol retarget (e.g. from upstream reordering its
/// overloads) is caught separately by the api-manifest ratchet; see <see cref="ApiManifestBaseline"/>.
/// </summary>
public class CollisionRankDeterminismTests
{
    private static (MethodDecl, string) Entry(MethodDecl m, string projectedKey)
        => (m, projectedKey);

    // Two overloads share the projected C# key `Process(int)` but differ by Swift argument label, so
    // both survive primary dedup and form one collision group. The rank follows declaration order:
    // whichever is declared (passed) first owns the bare name.
    [Theory]
    [InlineData(false)] // declared first:second
    [InlineData(true)]  // declared second:first
    public void BuildCollisionRankMap_LabelOverloads_RanksFollowDeclarationOrder(bool reversed)
    {
        var first = TestDecls.Method("process");  // process(first:)
        var second = TestDecls.Method("process"); // process(second:)
        const string projectedKey = "Process(int)";

        var input = reversed
            ? new List<(MethodDecl, string)> { Entry(second, projectedKey), Entry(first, projectedKey) }
            : new List<(MethodDecl, string)> { Entry(first, projectedKey), Entry(second, projectedKey) };

        var ranks = BaseHandler.BuildCollisionRankMap(input);

        if (reversed)
        {
            // `second` is declared first → it owns rank 0 (natural name `Process`); `first` → `Process2`.
            Assert.Equal(0, ranks[second]);
            Assert.Equal(1, ranks[first]);
        }
        else
        {
            Assert.Equal(0, ranks[first]);
            Assert.Equal(1, ranks[second]);
        }
    }

    // Three-way group: ranks follow the order the overloads are declared (passed in), not their labels.
    [Fact]
    public void BuildCollisionRankMap_ThreeWayGroup_RanksByDeclarationOrder()
    {
        var a = TestDecls.Method("render"); // render(alpha:)
        var b = TestDecls.Method("render"); // render(beta:)
        var c = TestDecls.Method("render"); // render(gamma:)
        const string key = "Render(int)";

        // Declaration order c, a, b → that is the rank order, independent of label alphabetization.
        var ranks = BaseHandler.BuildCollisionRankMap(new List<(MethodDecl, string)>
        {
            (c, key),
            (a, key),
            (b, key),
        });

        Assert.Equal(0, ranks[c]);
        Assert.Equal(1, ranks[a]);
        Assert.Equal(2, ranks[b]);
    }

    // A method that shares no projected key with any sibling is not a collision — it is absent from the
    // map (the caller reads it as rank 0 and keeps the natural, unsuffixed name).
    [Fact]
    public void BuildCollisionRankMap_SingletonGroup_AbsentFromMap()
    {
        var lone = TestDecls.Method("configure");
        var grouped1 = TestDecls.Method("process");
        var grouped2 = TestDecls.Method("process");

        var ranks = BaseHandler.BuildCollisionRankMap(new List<(MethodDecl, string)>
        {
            (lone, "Configure()"),
            (grouped1, "Process(int)"),
            (grouped2, "Process(int)"),
        });

        Assert.False(ranks.ContainsKey(lone));
        Assert.True(ranks.ContainsKey(grouped1));
        Assert.True(ranks.ContainsKey(grouped2));
    }
}
