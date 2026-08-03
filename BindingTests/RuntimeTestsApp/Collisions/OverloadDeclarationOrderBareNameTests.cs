// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Two sibling overloads share a Swift name AND the same projected C# parameter signature
/// (<c>Configure(int)</c>) but differ only by Swift argument label, so they survive primary dedup
/// yet collide at the secondary projected-C# dedup. Each one is named from its OWN Swift argument
/// label — <c>ConfigureZebra</c> / <c>ConfigureAlpha</c> — so an emitted name is a function of the
/// overload's own content, never of where it sits in the declaration list.
///
/// That is the whole point: the published C# surface is the consumer contract, and a regen must not
/// move a name onto a different overload. A real <c>generatePlane(width:height:…)</c> /
/// <c>generatePlane(width:depth:…)</c> factory pair (both projecting to
/// <c>GeneratePlane(float,float,float)</c>) broke when the colliding group was ranked by content —
/// <c>depth</c> sorts before <c>height</c>, so the bare name jumped off the first-declared
/// <c>height</c> overload and callers stopped compiling. Ranking by declaration order has the mirror
/// failure: inserting a new upstream overload renumbers every sibling after it.
///
/// The fixture's labels make declaration order DISAGREE with alphabetical order, so a scheme that
/// leaked EITHER rank into the name would put these assertions on the wrong bodies. Distinct offsets
/// prove WHICH Swift body each emitted name reaches. Neither overload keeps the bare
/// <c>Configure</c>: with every sibling labelled there is no label-less overload to own it, and
/// handing it out by rank is exactly the instability above.
/// </summary>
public class OverloadDeclarationOrderBareNameTests : TestBase
{
    public OverloadDeclarationOrderBareNameTests(TestResults results) : base(results) { }

    public void TestLabelDerivedNamesTrackTheirOwnOverload()
    {
        using var c = new CollisionDeclarationOrderBareName();
        // The FIRST-declared overload is configure(zebra:) (+700) and the second is configure(alpha:)
        // (+800). Both names come from their own label, so neither rank shows up in the surface.
        AssertEqual(705, c.ConfigureZebra(5),
            "ConfigureZebra -> configure(zebra:) +700 (named from its own label, declared first)");
        AssertEqual(805, c.ConfigureAlpha(5),
            "ConfigureAlpha -> configure(alpha:) +800 (named from its own label, declared second)");
    }

    public void TestThreeWayLabelDerivedNames()
    {
        using var r = new CollisionDeclarationOrderThreeWay();
        // Declaration order (gamma, beta, alpha) is the exact reverse of alphabetical, so a name that
        // tracked either ordering would land on a different body than its label says.
        AssertEqual(15, r.RenderGamma(5), "RenderGamma -> render(gamma:) +10 (declared first)");
        AssertEqual(25, r.RenderBeta(5), "RenderBeta -> render(beta:) +20 (declared second)");
        AssertEqual(35, r.RenderAlpha(5), "RenderAlpha -> render(alpha:) +30 (declared third)");
    }

    /// <summary>
    /// The surface must carry no rank at all: no bare <c>Configure</c>/<c>Render</c> (every sibling
    /// is labelled, so nobody owns the bare name) and no numeric suffix (the scheme that made a
    /// name depend on its neighbours).
    /// </summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionDeclarationOrderBareName))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(CollisionDeclarationOrderThreeWay))]
    public void TestNoRankDerivedNameOnTheSurface()
    {
        AssertNull(typeof(CollisionDeclarationOrderBareName).GetMethod("Configure", new[] { typeof(int) }),
            "no bare Configure — every sibling is labelled, so the bare name has no owner");
        AssertNull(typeof(CollisionDeclarationOrderBareName).GetMethod("Configure2", new[] { typeof(int) }),
            "no Configure2 — numeric suffixes are not part of the public surface");

        AssertNull(typeof(CollisionDeclarationOrderThreeWay).GetMethod("Render", new[] { typeof(int) }),
            "no bare Render — every sibling is labelled");
        AssertNull(typeof(CollisionDeclarationOrderThreeWay).GetMethod("Render2", new[] { typeof(int) }),
            "no Render2 — numeric suffixes are not part of the public surface");
        AssertNull(typeof(CollisionDeclarationOrderThreeWay).GetMethod("Render3", new[] { typeof(int) }),
            "no Render3 — numeric suffixes are not part of the public surface");
    }
}
