// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Collisions;

/// <summary>
/// Two sibling overloads share a Swift name AND the same projected C# parameter signature
/// (<c>Configure(int)</c>) but differ only by Swift argument label, so they survive primary dedup
/// yet collide at the secondary projected-C# dedup. One keeps the bare name (<c>Configure</c>), the
/// other is suffixed (<c>Configure2</c>).
///
/// The bare name must belong to the FIRST-DECLARED overload — the published C# surface is the
/// consumer contract, so a regen must not move it onto a different overload. A real
/// <c>generatePlane(width:height:…)</c> / <c>generatePlane(width:depth:…)</c> factory pair (both
/// projecting to <c>GeneratePlane(float,float,float)</c>) broke when a content-derived rank ordered
/// the colliding group alphabetically by Swift signature — <c>depth</c> sorts before <c>height</c>,
/// so the bare name jumped off the first-declared <c>height</c> overload and callers of
/// <c>GeneratePlane(width:height:…)</c> stopped compiling.
///
/// The fixture's labels make declaration order DISAGREE with alphabetical order, so these
/// assertions go red under the alphabetical/content rank. Distinct offsets prove WHICH Swift body
/// each emitted name reaches.
/// </summary>
public class OverloadDeclarationOrderBareNameTests : TestBase
{
    public OverloadDeclarationOrderBareNameTests(TestResults results) : base(results) { }

    public void TestDeclarationOrderOwnsBareName()
    {
        using var c = new CollisionDeclarationOrderBareName();
        // Bare `Configure` binds the FIRST-declared overload (zebra, +700) even though "zebra" sorts
        // alphabetically AFTER "alpha". A content/alphabetical rank would have given `Configure` to
        // alpha (+800) and pushed zebra to `Configure2`, inverting both assertions.
        AssertEqual(705, c.Configure(5),
            "Configure -> first-declared configure(zebra:) +700 (declaration order owns the bare name)");
        AssertEqual(805, c.Configure2(5),
            "Configure2 -> later-declared configure(alpha:) +800");
    }

    public void TestThreeWayDeclarationOrderSuffixing()
    {
        using var r = new CollisionDeclarationOrderThreeWay();
        // Declaration order (gamma, beta, alpha) is the exact reverse of alphabetical, so each suffix
        // follows declaration order, not the label's alphabetical position.
        AssertEqual(15, r.Render(5), "Render -> first-declared render(gamma:) +10");
        AssertEqual(25, r.Render2(5), "Render2 -> second-declared render(beta:) +20");
        AssertEqual(35, r.Render3(5), "Render3 -> third-declared render(alpha:) +30");
    }
}
