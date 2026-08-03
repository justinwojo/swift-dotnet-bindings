// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Same-module override of a label-disambiguated overload
//
// `process(first:)` and `process(second:)` share a method name AND a projected C#
// parameter signature (`Process(int)`), but differ in their Swift argument label —
// so they survive PRIMARY dedup (the label distinguishes them) yet collide at the
// SECONDARY projected-C# dedup. Each overload is named from its OWN argument label:
// the base emits `ProcessFirst` and `ProcessSecond`, and neither keeps the bare
// `Process` (a bare name goes only to a label-less overload).
//
// The same-module override verifier (`WrapperEmitter.Signature.cs`) used to recompute
// each ancestor method's C# name through a fresh NameProvider pass, which cannot see a
// disambiguated name that only exists because the ancestor's body contained a colliding
// sibling — so it never matched that slot and a derived override of `ProcessSecond` bound
// to the wrong base method (silent: C# erases the difference). The fix makes the verifier
// prefer each ancestor method's ground-truth `EmittedCSharpName`.
//
// Each method returns a distinct marker (value + an id offset) so a C# runtime test can
// prove WHICH Swift body actually dispatched through the base-typed reference.

open class CollisionOverrideBase {
    public init() {}

    open func process(first value: Int32) -> Int32 { return value + 100 }
    open func process(second value: Int32) -> Int32 { return value + 200 }
}

/// Scenario B — derived overrides BOTH overloads. Because the derived class body also
/// contains two colliding `process` methods, it independently recomputes `ProcessFirst` /
/// `ProcessSecond`; the verifier only needs to recognise the disambiguated ancestor slot
/// (the `EmittedCSharpName`-preference fix) for both overrides to bind correctly.
open class CollisionOverrideDerivedBoth: CollisionOverrideBase {
    public override init() { super.init() }

    open override func process(first value: Int32) -> Int32 { return value + 1100 }
    open override func process(second value: Int32) -> Int32 { return value + 1200 }
}

/// Scenario A — derived overrides ONLY the second (`process(second:)` → base `ProcessSecond`).
/// The derived class body has a single, uncontested `process`, so a naive name computation
/// would emit the bare `Process` and hijack nothing that exists. This is the deeper shape:
/// binding the override to the correct base slot requires adopting the ancestor's emitted
/// name, not recomputing from the derived's (collision-free) context.
open class CollisionOverrideDerivedSecondOnly: CollisionOverrideBase {
    public override init() { super.init() }

    open override func process(second value: Int32) -> Int32 { return value + 2200 }
}

/// Scenario C — derived overrides ONLY `process(second:)` (so it adopts the base `ProcessSecond`
/// slot) AND declares a brand-new `processSecond(_:)` whose own natural projected C# name is ALSO
/// `ProcessSecond`. The adopted override must keep that slot; the unrelated new sibling must be
/// pushed off it. Before the dedup loop reserved adopted-override keys up front, the override
/// emitted `ProcessSecond` while the new method ALSO emitted `ProcessSecond` → CS0111, and the whole
/// generated binding failed to compile. Distinct markers prove each binds to the correct Swift body.
open class CollisionOverrideDerivedSecondPlusSibling: CollisionOverrideBase {
    public override init() { super.init() }

    open override func process(second value: Int32) -> Int32 { return value + 3200 }
    open func processSecond(_ value: Int32) -> Int32 { return value + 3300 }
}

/// Default-argument source for Scenario D. The default expression is a function CALL, which is
/// deliberately NON-mappable to an inline C# default (`SwiftDefaultValueMapper` only maps literals,
/// nil, and enum cases). That forces `DefaultParameterOverloadEmitter` to synthesize a zero-arg
/// trimmed convenience overload — the only path that exercises the adopted-name propagation. A plain
/// literal default (`= 7`) would emit inline as `int value = 7` and skip the overload emitter entirely.
public func defaultSecondProcessValue() -> Int32 { return 9 }

/// Scenario D — derived overrides ONLY `process(second:)` (adopts base `ProcessSecond`) and gives the
/// parameter a non-mappable DEFAULT value. The default-parameter convenience overload the generator
/// synthesizes (zero-arg) must ALSO emit under the adopted name — `ProcessSecond()`, not the recomputed
/// bare `Process()`. Before the overload environment propagated the adopted name, the trimmed overload
/// recomputed `Process(...)`, pairing the convenience surface with the wrong slot (silent: the zero-arg
/// convenience would dispatch through the base `process(first:)` instead of the overridden second).
open class CollisionOverrideDerivedSecondDefaulted: CollisionOverrideBase {
    public override init() { super.init() }

    open override func process(second value: Int32 = defaultSecondProcessValue()) -> Int32 { return value + 4200 }
}

/// Scenario E — the REVERSE-declaration-order twin of Scenario C: the brand-new `processSecond(_:)`
/// sibling is declared BEFORE the `override process(second:)`. The adopted-name slot
/// (`ProcessSecond`) must still go to the override and the new sibling must still be pushed off it,
/// independent of source order. An in-loop reservation alone is order-dependent here — the sibling
/// claims `ProcessSecond` first, the override's later reservation no-ops, and both emit
/// `ProcessSecond` → CS0111. The up-front pre-reservation (`PreReserveAdoptedOverrideNames`) makes
/// the two orders produce identical C# shapes. Markers mirror Scenario C (offset by +1000) so the
/// runtime test can prove each slot still binds to the correct Swift body.
open class CollisionOverrideDerivedSiblingFirst: CollisionOverrideBase {
    public override init() { super.init() }

    open func processSecond(_ value: Int32) -> Int32 { return value + 4300 }
    open override func process(second value: Int32) -> Int32 { return value + 4200 }
}
