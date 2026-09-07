// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Concrete-specialised members on a scalar-only frozen generic struct parent
//
// The concrete-specialisation machinery emits a per-conformer extension whose body has
// to hand `self` to the P/Invoke. It picks between two carriers: the parent's `Payload`
// SafeHandle, or `((ISwiftObject)self).SwiftHandle`. Both assume the parent projects to a
// C# *class*.
//
// A frozen struct earns that class projection only when its layout needs managed
// bookkeeping. A frozen struct whose every stored field is a fixed-width scalar projects
// to a plain C# struct instead: it has no `Payload`, and it implements `ISwiftObject`
// explicitly with a `SwiftHandle` that throws. So one carrier does not exist and the other
// only appears to — a member routed onto the specialised path either names a member the
// handler never wrote (a generated binding that does not compile) or compiles and throws
// on first use.
//
// `ScalarWeighedBox` is that parent: `@frozen`, generic over a protocol constraint with
// real conformers in this module (so specialisation has something to close over), and with
// the generic parameter appearing only in its initialiser and never in storage — every
// stored field is an `Int32`. It carries both an `async` and a `sync` specialisable
// member, because the two routes make the carrier decision at separate sites and a fix to
// one says nothing about the other.
//
// `RefWeighedBox` is the positive control on the other side of the same line: same shape
// but storing the conformer itself, which puts it on the reference-bearing projection that
// really does expose `Payload`. It has to keep specialising — a refusal wide enough to
// swallow it would have given up working coverage rather than fixed anything.

/// Constraint protocol for the generic parents below. The conformers are what the
/// specialisation engine closes the parent's generic over.
public protocol SlotWeight {
    var weightUnits: Int32 { get }
}

// The conformers carry a `String`, which puts them on the reference-bearing projection.
// That is a requirement of the machinery rather than a property of this test: a
// scalar-only conformer is refused structurally (the specialised call has no way to hand
// a plain C# value struct to Swift), and refusing it collapses the parent's conformer set
// to empty, which switches specialisation off entirely and would leave the parent under
// test unexercised. The scalar-only shape being probed here is the PARENT's, not theirs.

@frozen
public struct LightSlot: SlotWeight {
    public let weightUnits: Int32
    public let slotName: String
    public init(weightUnits: Int32, slotName: String) {
        self.weightUnits = weightUnits
        self.slotName = slotName
    }
}

@frozen
public struct HeavySlot: SlotWeight {
    public let weightUnits: Int32
    public let slotName: String
    public init(weightUnits: Int32, slotName: String) {
        self.weightUnits = weightUnits
        self.slotName = slotName
    }
}

/// Scalar-only frozen generic parent. `T` is carried by the initialiser (so it is still
/// inferred at every call site and still specialisable) but never stored, leaving the
/// layout as two `Int32`s — a plain C# struct with no `Payload` and only a throwing
/// explicit `ISwiftObject.SwiftHandle`.
@frozen
public struct ScalarWeighedBox<T: SlotWeight> {
    // Storage stays private so the layout is exactly two scalars; the public surface below
    // is what the carrier decision is probed through.
    private let units: Int32
    private let tare: Int32

    public init(slot: T, tare: Int32) {
        self.units = slot.weightUnits
        self.tare = tare
    }

    /// Async specialisable member — the async generic-parent CSM route.
    public func totalUnits() async -> Int32 {
        return units + tare
    }

    /// Sync specialisable member — the sync generic-parent CSM route, which makes the
    /// same carrier decision at its own emission site.
    public func netUnits() -> Int32 {
        return units - tare
    }

    /// A property accessor takes the same `self` receiver an instance method does, so it
    /// lands on the same carrier decision by a different validation path. Without a
    /// property-side arm this is the member that still reaches the open-generic surface.
    public var netUnitsValue: Int32 {
        return units - tare
    }

    /// Subscripts take that same receiver, through a third validation path.
    public subscript(scale: Int32) -> Int32 {
        return (units - tare) * scale
    }
}

/// Positive control: storing the conformer puts this parent on the reference-bearing
/// projection that does expose `Payload`, so specialisation on it must keep working.
@frozen
public struct RefWeighedBox<T: SlotWeight> {
    private let slot: T
    private let label: String

    public init(slot: T, label: String) {
        self.slot = slot
        self.label = label
    }

    public func labelledUnits() async -> Int32 {
        return slot.weightUnits
    }

    public func plainUnits() -> Int32 {
        return slot.weightUnits
    }
}
