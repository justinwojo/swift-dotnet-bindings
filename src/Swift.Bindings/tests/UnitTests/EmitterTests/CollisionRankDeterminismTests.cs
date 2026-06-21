// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// F52 (C.1): the overload-collision disambiguation suffix must be a pure function of the type/module
/// body's CONTENT, not the order the overloads were declared in Swift. <see cref="BaseHandler.BuildCollisionRankMap"/>
/// is the single core both the class-member path (<c>HandleBaseDecl</c>) and the free-function path
/// (<c>ModuleHandler</c>) feed; if it ranks the same set of overloads identically under any input order,
/// the C#-name↔Swift-symbol mapping the api-manifest records cannot silently retarget when upstream Swift
/// source is reordered.
/// </summary>
public class CollisionRankDeterminismTests
{
    private static (MethodDecl, string, string) Entry(MethodDecl m, string projectedKey, string signatureKey)
        => (m, projectedKey, signatureKey);

    // Two overloads share the projected C# key `Process(int)` but differ by Swift argument label, so they
    // carry distinct (injective) signature keys. The rank assignment must be identical in both source orders.
    [Theory]
    [InlineData(false)] // declared first:second
    [InlineData(true)]  // declared second:first (reordered source)
    public void BuildCollisionRankMap_LabelOverloads_RanksAreSourceOrderIndependent(bool reversed)
    {
        var first = TestDecls.Method("process");  // process(first:)
        var second = TestDecls.Method("process"); // process(second:)
        const string projectedKey = "Process(int)";
        var firstEntry = Entry(first, projectedKey, "method:process(first:Swift.Int)");
        var secondEntry = Entry(second, projectedKey, "method:process(second:Swift.Int)");

        var input = reversed
            ? new List<(MethodDecl, string, string)> { secondEntry, firstEntry }
            : new List<(MethodDecl, string, string)> { firstEntry, secondEntry };

        var ranks = BaseHandler.BuildCollisionRankMap(input);

        // `first:` sorts before `second:` by signature key, so it owns rank 0 (natural name `Process`)
        // and `second:` takes rank 1 (`Process2`) — regardless of which was passed in first.
        Assert.Equal(0, ranks[first]);
        Assert.Equal(1, ranks[second]);
    }

    // Three-way group: rank order follows the signature-key sort, not the input order.
    [Fact]
    public void BuildCollisionRankMap_ThreeWayGroup_RanksByContentNotInputOrder()
    {
        var a = TestDecls.Method("render"); // render(alpha:)
        var b = TestDecls.Method("render"); // render(beta:)
        var c = TestDecls.Method("render"); // render(gamma:)
        const string key = "Render(int)";

        // Shuffled input order; content order is alpha < beta < gamma.
        var ranks = BaseHandler.BuildCollisionRankMap(new List<(MethodDecl, string, string)>
        {
            (c, key, "method:render(gamma:Swift.Int)"),
            (a, key, "method:render(alpha:Swift.Int)"),
            (b, key, "method:render(beta:Swift.Int)"),
        });

        Assert.Equal(0, ranks[a]);
        Assert.Equal(1, ranks[b]);
        Assert.Equal(2, ranks[c]);
    }

    // A method that shares no projected key with any sibling is not a collision — it is absent from the
    // map (the caller reads it as rank 0 and keeps the natural, unsuffixed name).
    [Fact]
    public void BuildCollisionRankMap_SingletonGroup_AbsentFromMap()
    {
        var lone = TestDecls.Method("configure");
        var grouped1 = TestDecls.Method("process");
        var grouped2 = TestDecls.Method("process");

        var ranks = BaseHandler.BuildCollisionRankMap(new List<(MethodDecl, string, string)>
        {
            (lone, "Configure()", "method:configure()"),
            (grouped1, "Process(int)", "method:process(a:Swift.Int)"),
            (grouped2, "Process(int)", "method:process(b:Swift.Int)"),
        });

        Assert.False(ranks.ContainsKey(lone));
        Assert.True(ranks.ContainsKey(grouped1));
        Assert.True(ranks.ContainsKey(grouped2));
    }
}
