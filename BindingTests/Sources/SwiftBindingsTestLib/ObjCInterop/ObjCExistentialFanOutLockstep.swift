// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Reverse-dispatch FAN-OUT lockstep for a dropped @objc-existential requirement shared across TWO
// protocols.
//
// ObjCExistentialReverseVtableLockstep.swift covers the SINGLE-protocol drop. This fixture covers the
// distinct hazard the fan-out branch filter guards: when TWO plain protocols declare one IDENTICAL
// method whose parameter carries a nested @objc-protocol existential (`[any ObjCClassBoundShape]`),
// they form a single same-signature owner/peer group. The owner emits one EveryProtocol witness body
// and fans a nil-check branch out across every sibling that emits a per-protocol vtable FUNC field,
// each branch reading `siblingVtable.func_absorb_{idx}`. That field exists ONLY for a layout-included
// slot — and the nested @objc existential is dropped fail-closed from the layout (skip-but-consume:
// index consumed, no field). The fan-out branch filter therefore MUST consult the same vtable-layout
// membership oracle as the struct walk; a divergent predicate that kept the dropped sibling in the
// branch list would emit a branch over a `func_absorb_{idx}` member the struct never declared, failing
// Swift wrapper compilation for the WHOLE package.
//
// A SUPPORTED scalar requirement is declared LAST on each protocol so a conformer stays exercisable and
// reverse dispatch of the surviving slot round-trips. The dropped `absorb` members are asserted by
// ABSENCE (no C# test references them) and by the fact that the binding compiles. Uses the
// `ObjCClassBoundShape` @objc protocol from ObjCClassBoundExistential.swift.

import Foundation

public protocol ObjCShapeSinkA: AnyObject {
    // HAZARD: an @objc protocol existential in a container parameter position. Dropped fail-closed
    // from BOTH the C# interface and the reverse-dispatch vtable slot on this protocol.
    func absorb(shapes: [any ObjCClassBoundShape])
    // SUPPORTED scalar requirement — the conformer fills this slot; reverse dispatch round-trips.
    var sinkCountA: Int32 { get }
}

public protocol ObjCShapeSinkB: AnyObject {
    // HAZARD: the IDENTICAL nested-@objc-existential signature, so A and B land in ONE same-signature
    // fan-out group. This is the shape the branch filter must drop in lockstep with the layout.
    func absorb(shapes: [any ObjCClassBoundShape])
    // SUPPORTED scalar requirement.
    var sinkCountB: Int32 { get }
}

/// Reverse-dispatches the SUPPORTED `sinkCountA` requirement back into a C# conformer of
/// `ObjCShapeSinkA`. Proves the supported slot survives with the shared hazard requirement dropped.
public func readSinkCountA(_ sink: any ObjCShapeSinkA) -> Int32 {
    return sink.sinkCountA
}

/// Reverse-dispatches the SUPPORTED `sinkCountB` requirement back into a C# conformer of
/// `ObjCShapeSinkB`.
public func readSinkCountB(_ sink: any ObjCShapeSinkB) -> Int32 {
    return sink.sinkCountB
}
