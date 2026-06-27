// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bare-name ownership follows DECLARATION order, not alphabetical/content order
//
// Two sibling overloads share a method name AND the same projected C# parameter signature
// (`Configure(int)`) but differ only by Swift argument label, so they survive PRIMARY dedup
// (the label distinguishes them) yet collide at the SECONDARY projected-C# dedup. One keeps the
// natural, unsuffixed name (`Configure`); the other is suffixed (`Configure2`).
//
// WHICH one keeps the bare name is the regression this fixture guards. The owner must be the
// FIRST-DECLARED overload — the binding's published C# surface is the consumer contract, and a
// regen must not silently move the bare name onto a different overload (that renames an
// already-shipped API and breaks every caller of it). A real-world consumer (a `MeshResource`
// factory pair `generatePlane(width:height:…)` / `generatePlane(width:depth:…)`, both projecting
// to `GeneratePlane(float,float,float)`) broke exactly this way when a content-derived rank
// ordered the colliding group ALPHABETICALLY by Swift signature: `depth` sorts before `height`,
// so the bare `GeneratePlane` jumped from the first-declared `height` overload to the `depth`
// one, and consumers calling `GeneratePlane(width:height:…)` failed to compile.
//
// Here the labels are chosen so declaration order DISAGREES with alphabetical order: the
// first-declared label (`zebra`) sorts AFTER the second (`alpha`). Declaration order must win —
// bare `Configure` binds the `zebra` body; `alpha` is pushed to `Configure2`. Under the
// alphabetical/content rank this fixture would invert (bare `Configure` → `alpha`), so the
// runtime assertions below go RED on a regression. Each body returns `value + a distinct offset`
// so dispatch proves WHICH Swift body the bare name actually reached.

open class CollisionDeclarationOrderBareName {
    public init() {}

    // Declared FIRST → owns the bare `Configure`, even though "zebra" sorts alphabetically LAST.
    open func configure(zebra value: Int32) -> Int32 { return value + 700 }
    // Declared SECOND → suffixed `Configure2`, even though "alpha" sorts alphabetically FIRST.
    open func configure(alpha value: Int32) -> Int32 { return value + 800 }
}

/// Three-way twin: declaration order (gamma, beta, alpha) is the exact REVERSE of alphabetical,
/// so a content/alphabetical rank would assign every suffix backwards. Declaration order must
/// win at each rank — `Render` → gamma (+10), `Render2` → beta (+20), `Render3` → alpha (+30).
open class CollisionDeclarationOrderThreeWay {
    public init() {}

    open func render(gamma value: Int32) -> Int32 { return value + 10 }
    open func render(beta value: Int32) -> Int32 { return value + 20 }
    open func render(alpha value: Int32) -> Int32 { return value + 30 }
}
