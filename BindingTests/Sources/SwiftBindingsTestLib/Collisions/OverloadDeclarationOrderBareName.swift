// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - An overload's emitted name comes from its OWN content, not from any rank
//
// Two sibling overloads share a method name AND the same projected C# parameter signature
// (`Configure(int)`) but differ only by Swift argument label, so they survive PRIMARY dedup
// (the label distinguishes them) yet collide at the SECONDARY projected-C# dedup. Each is named
// from its own argument label: `ConfigureZebra` and `ConfigureAlpha`.
//
// WHICH overload gets which name is the regression this fixture guards. The binding's published C#
// surface is the consumer contract, and a regen must not silently move a name onto a different
// overload (that renames an already-shipped API and breaks every caller of it). A real-world
// consumer (a `MeshResource` factory pair `generatePlane(width:height:…)` /
// `generatePlane(width:depth:…)`, both projecting to `GeneratePlane(float,float,float)`) broke
// exactly this way when a content-derived RANK ordered the colliding group ALPHABETICALLY by Swift
// signature: `depth` sorts before `height`, so the bare `GeneratePlane` jumped from the
// first-declared `height` overload to the `depth` one, and consumers calling
// `GeneratePlane(width:height:…)` failed to compile. Ranking by declaration order instead has the
// mirror failure: an overload inserted upstream renumbers every sibling declared after it.
//
// Here the labels are chosen so declaration order DISAGREES with alphabetical order: the
// first-declared label (`zebra`) sorts AFTER the second (`alpha`). Under either rank the names
// would land on different bodies than the labels say, so the runtime assertions go RED on a
// regression. Each body returns `value + a distinct offset` so dispatch proves WHICH Swift body
// each emitted name actually reached. Neither sibling keeps the bare `Configure`: with every
// overload labelled there is no label-less member to own it, and handing it out by rank is the
// instability above.

open class CollisionDeclarationOrderBareName {
    public init() {}

    // `ConfigureZebra` — named from its own label, regardless of being declared first.
    open func configure(zebra value: Int32) -> Int32 { return value + 700 }
    // `ConfigureAlpha` — named from its own label, regardless of being declared second.
    open func configure(alpha value: Int32) -> Int32 { return value + 800 }
}

/// Three-way twin: declaration order (gamma, beta, alpha) is the exact REVERSE of alphabetical, so
/// a rank-derived scheme would assign names backwards under one ordering or the other. Label-derived
/// names sidestep both — `RenderGamma` (+10), `RenderBeta` (+20), `RenderAlpha` (+30).
open class CollisionDeclarationOrderThreeWay {
    public init() {}

    open func render(gamma value: Int32) -> Int32 { return value + 10 }
    open func render(beta value: Int32) -> Int32 { return value + 20 }
    open func render(alpha value: Int32) -> Int32 { return value + 30 }
}
