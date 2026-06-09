// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Same-module override of a collision-suffixed overload
//
// `process(first:)` and `process(second:)` share a method name AND a projected C#
// parameter signature (`Process(int)`), but differ in their Swift argument label —
// so they survive PRIMARY dedup (the label distinguishes them) yet collide at the
// SECONDARY projected-C# dedup. B15 disambiguates the second with a numeric suffix:
// the base emits `Process` (first) and `Process2` (second).
//
// The same-module override verifier (`WrapperEmitter.Signature.cs`) used to recompute
// each ancestor method's C# name through a fresh NameProvider pass, which cannot see a
// collision suffix that only exists because a sibling already claimed the base name —
// so it never matched the suffixed slot and a derived override of `Process2` bound to
// the wrong base method (silent: C# erases the difference). The fix makes the verifier
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
/// contains two colliding `process` methods, it independently recomputes `Process` /
/// `Process2`; the verifier only needs to recognise the suffixed ancestor slot (the
/// `EmittedCSharpName`-preference fix) for both overrides to bind correctly.
open class CollisionOverrideDerivedBoth: CollisionOverrideBase {
    public override init() { super.init() }

    open override func process(first value: Int32) -> Int32 { return value + 1100 }
    open override func process(second value: Int32) -> Int32 { return value + 1200 }
}

/// Scenario A — derived overrides ONLY the second (`process(second:)` → base `Process2`).
/// The derived class body has a single `process`, so its own collision index is 0 and a
/// naive name computation would emit `Process` and hijack the wrong base slot. This is the
/// deeper shape: binding the override to the correct base slot requires adopting the
/// ancestor's emitted name, not recomputing from the derived's (suffix-free) context.
open class CollisionOverrideDerivedSecondOnly: CollisionOverrideBase {
    public override init() { super.init() }

    open override func process(second value: Int32) -> Int32 { return value + 2200 }
}

/// Scenario C — derived overrides ONLY `process(second:)` (so it adopts the base `Process2` slot)
/// AND declares a brand-new `process2(_:)` whose own projected C# name is ALSO `Process2`. The
/// adopted override must keep the `Process2` slot; the unrelated new sibling must be pushed to a
/// fresh suffix (`Process22`). Before the dedup loop reserved adopted-override keys up front, the
/// override emitted `Process2` while the new method ALSO emitted `Process2` → CS0111, and the whole
/// generated binding failed to compile. Distinct markers prove each binds to the correct Swift body.
open class CollisionOverrideDerivedSecondPlusSibling: CollisionOverrideBase {
    public override init() { super.init() }

    open override func process(second value: Int32) -> Int32 { return value + 3200 }
    open func process2(_ value: Int32) -> Int32 { return value + 3300 }
}

/// Default-argument source for Scenario D. The default expression is a function CALL, which is
/// deliberately NON-mappable to an inline C# default (`SwiftDefaultValueMapper` only maps literals,
/// nil, and enum cases). That forces `DefaultParameterOverloadEmitter` to synthesize a zero-arg
/// trimmed convenience overload — the only path that exercises the adopted-name propagation. A plain
/// literal default (`= 7`) would emit inline as `int value = 7` and skip the overload emitter entirely.
public func defaultSecondProcessValue() -> Int32 { return 9 }

/// Scenario D — derived overrides ONLY `process(second:)` (adopts base `Process2`) and gives the
/// parameter a non-mappable DEFAULT value. The default-parameter convenience overload the generator
/// synthesizes (zero-arg) must ALSO emit under the adopted name — `Process2()`, not the recomputed
/// bare `Process()`. Before the overload environment propagated the adopted name, the trimmed overload
/// recomputed `Process(...)`, pairing the convenience surface with the wrong slot (silent: the zero-arg
/// convenience would dispatch through the base `process(first:)` instead of the overridden second).
open class CollisionOverrideDerivedSecondDefaulted: CollisionOverrideBase {
    public override init() { super.init() }

    open override func process(second value: Int32 = defaultSecondProcessValue()) -> Int32 { return value + 4200 }
}

/// Scenario E — the REVERSE-declaration-order twin of Scenario C: the brand-new `process2(_:)`
/// sibling is declared BEFORE the `override process(second:)`. The adopted-name slot (`Process2`)
/// must still go to the override and the new sibling must still be pushed to `Process22`,
/// independent of source order. An in-loop reservation alone is order-dependent here — the sibling
/// claims `Process2` first, the override's later reservation no-ops, and both emit `Process2` →
/// CS0111. The up-front pre-reservation (`PreReserveAdoptedOverrideNames`) makes the two orders
/// produce identical C# shapes. Markers mirror Scenario C (offset by +1000) so the runtime test can
/// prove each slot still binds to the correct Swift body.
open class CollisionOverrideDerivedSiblingFirst: CollisionOverrideBase {
    public override init() { super.init() }

    open func process2(_ value: Int32) -> Int32 { return value + 4300 }
    open override func process(second value: Int32) -> Int32 { return value + 4200 }
}
