// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Reverse-dispatch vtable lockstep for a dropped @objc-existential requirement.
//
// A protocol requirement typed `[any ObjCClassBoundShape]` — an @objc protocol existential in a
// container position — used to earn a C# interface member AND a reverse-dispatch vtable slot whose
// receiver read a 40-byte ExistentialContainer1 carrier over the 8-byte @objc object-pointer stride
// (buffer over-read). Only bare `any P` / `Optional<any P>` are supported for an @objc protocol; the
// container/tuple/closure/nested positions must be dropped fail-closed.
//
// The drop MUST happen in LOCKSTEP across the member gate (which removes the C# interface member) and
// the vtable classifier (which removes the reverse-dispatch slot). If only one side drops, every later
// slot shifts and the C# conformer fills a different slot than Swift dispatches — a corruption that
// only SIGSEGVs on the NativeAOT device leg. This protocol declares the two hazard requirements FIRST
// and a SUPPORTED scalar getter LAST, so a lockstep failure that dropped one hazard side but not the
// other shifts `shapeCount`'s slot and `readShapeCollectorCount` returns garbage / crashes. When both
// sides drop in lockstep, the supported slot stays at index 0 and reverse dispatch round-trips.
//
// The dropped members are asserted by ABSENCE: no C# test references `Absorb`/`Shapes` (they are gone
// from the emitted `IObjCShapeCollector`), and the binding still compiles. Uses the `ObjCClassBoundShape`
// @objc protocol from ObjCClassBoundExistential.swift.

import Foundation

public protocol ObjCShapeCollector: AnyObject {
    // HAZARD (declared FIRST): an @objc protocol existential in a container parameter position. Dropped
    // fail-closed from BOTH the C# interface and the reverse-dispatch vtable.
    func absorb(shapes: [any ObjCClassBoundShape])
    // HAZARD: an @objc protocol existential in a container property position. Dropped fail-closed.
    var shapes: [any ObjCClassBoundShape] { get }

    // SUPPORTED scalar requirement declared AFTER the hazards. The C# conformer fills this slot and
    // Swift dispatches back into it; a lockstep failure that shifted the slot returns garbage here.
    var shapeCount: Int32 { get }
}

/// Reverse-dispatches the SUPPORTED `shapeCount` requirement back into a C# conformer of
/// `ObjCShapeCollector`. Proves the supported slot survives with the hazard requirements dropped.
public func readShapeCollectorCount(_ collector: any ObjCShapeCollector) -> Int32 {
    return collector.shapeCount
}
