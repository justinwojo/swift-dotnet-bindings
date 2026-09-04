// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Reverse-dispatch lifetime & identity invariants (Design B2 / Defect G)
//
// These fixtures pin the three behavioural invariants the reverse-dispatch
// lifetime model introduces:
//
//   R1 — cross-talk: a single C# object implementing two *unrelated* reverse-dispatch
//        protocols gets one EveryProtocol handle per existential wrap, and each handle
//        must resolve ONLY its own protocol's view. `ProxyLifetimeTracker.ResolveImpl<T>`
//        is `impl as T`, a strictly wider predicate than the old proxy lookup; the design
//        review judged the widening unreachable from generated code, so this makes it a
//        tested invariant.
//
//   R4 — value round-trip (not identity): under B2 the C#-impl proxy may be collected
//        while Swift still holds a *stored* existential. A later read mints a fresh Swift
//        carrier, so `===` identity is no longer stable for class-bound stored
//        existentials — but the VALUE must still round-trip, because the impl is rooted by
//        Swift liveness (the strong impl GCHandle survives until the stored retain drops).
//
// All three protocols here are deliberately *opaque* (non-`AnyObject`) where the metadata
// word is load-bearing, except `ReverseStoredDelegate` which is class-bound to exercise
// the R4 stored-existential identity path that the design doc calls out specifically for
// class-bound storage.
//
// The methods RETURN distinct sentinels (input + a per-protocol offset) so a mis-resolved
// receiver surfaces as a wrong value, not merely a missed void callback.

// MARK: R1 — two unrelated opaque protocols

/// First of two unrelated reverse-dispatch protocols. Opaque (no `AnyObject`), so its
/// auto-wrapped proxy carries the per-module EveryProtocol metadata word — also exercising
/// the Finding-33 main-module side.
public protocol ReverseInvariantAlpha {
    /// Returns `value + 100` so the C# test can assert the alpha view (not beta) serviced
    /// the call.
    func alphaValue(_ value: Int32) -> Int32
}

/// Second, unrelated protocol — NO inheritance relationship with `ReverseInvariantAlpha`.
/// A single C# class can implement both; each must dispatch through its own witness table.
public protocol ReverseInvariantBeta {
    /// Returns `value + 200`.
    func betaValue(_ value: Int32) -> Int32
}

// MARK: R4 — class-bound stored existential

/// Class-bound reverse-dispatch protocol used for the R4 stored-existential value
/// round-trip. Stored strongly by `ReverseInvariantHarness`, so the C#-impl proxy can be
/// collected while Swift still holds the existential.
public protocol ReverseStoredDelegate: AnyObject {
    /// Returns `value + 1000`.
    func storedValue(_ value: Int32) -> Int32
}

/// Drives all of the main-module reverse-dispatch invariants.
public class ReverseInvariantHarness {
    /// Strong storage for the R4 stored existential. Public get+set so the C# test can
    /// assign an impl, drop its own reference, and later read the existential back.
    public var storedDelegate: ReverseStoredDelegate?

    public init() {}

    /// R1: dispatch through the alpha view. A correct resolver returns `value + 100`.
    public func pingAlpha(_ alpha: any ReverseInvariantAlpha, value: Int32) -> Int32 {
        return alpha.alphaValue(value)
    }

    /// R1: dispatch through the beta view of the SAME C# object. A correct resolver
    /// returns `value + 200`; cross-talk would return the alpha sentinel or trap.
    public func pingBeta(_ beta: any ReverseInvariantBeta, value: Int32) -> Int32 {
        return beta.betaValue(value)
    }

    /// R4: dispatch into the stored existential. After the C#-impl proxy is collected,
    /// Swift still holds `storedDelegate`, so the impl stays rooted (Swift liveness) and
    /// this must still return `value + 1000`.
    public func invokeStored(value: Int32) -> Int32 {
        return storedDelegate?.storedValue(value) ?? -1
    }
}

// MARK: R5 — non-retaining (weak / unowned) stored existential

/// Class-bound reverse-dispatch protocol stored through a NON-RETAINING property. Separate
/// from `ReverseStoredDelegate` so the two storage flavours cannot share a conformer and
/// mask each other's rooting behaviour.
public protocol ReverseWeakDelegate: AnyObject {
    /// Returns `value + 2000`.
    func weakValue(_ value: Int32) -> Int32
}

/// R5: the delegate shape Apple frameworks ship — `weak var delegate: (any P)?`.
///
/// A non-retaining sink takes no retain on the conformer box behind the existential, so
/// nothing on the Swift side keeps it alive once the setter returns. That makes this the
/// fixture for the managed-side ownership rule: the box follows the consumer's own
/// implementation object — it must survive for as long as the consumer holds that
/// implementation, and must go away with it even while the receiver is still alive.
///
/// `invokeWeak` returns the sentinel `value + 2000` when the delegate is still live and
/// `-1` when the weak storage has gone nil, so a lost root surfaces as a wrong value rather
/// than a missed void callback. `unownedDelegate` is the optional-`unowned` flavour of the
/// same hazard; it is read back only while its conformer is known live, since reading a
/// dangling `unowned` traps by design.
public class ReverseWeakSinkHarness {
    /// Non-retaining, zeroing storage. Optional by language rule (`weak` implies Optional).
    public weak var weakDelegate: (any ReverseWeakDelegate)?

    /// Non-retaining, non-zeroing storage. Declared Optional so the binding takes the same
    /// decomposed-optional setter path the `weak` property does.
    public unowned var unownedDelegate: (any ReverseWeakDelegate)?

    public init() {}

    /// R5: dispatch through the weak sink. `-1` means the weak storage read nil — expected
    /// once the consumer drops the implementation, a regression while they still hold it.
    public func invokeWeak(value: Int32) -> Int32 {
        return weakDelegate?.weakValue(value) ?? -1
    }

    /// R5: dispatch through the unowned sink. Same sentinel contract as `invokeWeak`.
    public func invokeUnowned(value: Int32) -> Int32 {
        return unownedDelegate?.weakValue(value) ?? -1
    }

    /// Observes the weak storage without vending an existential across the boundary, so a
    /// liveness assertion cannot itself mint a carrier that changes what it measures.
    public var hasWeakDelegate: Bool {
        return weakDelegate != nil
    }
}

// MARK: R6 — non-retaining sinks whose setter takes a different marshalling arm

/// `@objc` reverse-dispatch protocol stored through a non-retaining sink.
///
/// An `@objc` protocol existential is a single bare ObjC object pointer, not the decomposed
/// two-word carrier a native Swift class-bound existential uses, so its setter is marshalled
/// by a different arm than `ReverseWeakDelegate`'s. Ownership is a property of the sink, not
/// of the wire width, so this shape must reach the same managed-side rooting rule: the
/// conformer box follows the consumer's own implementation object.
@objc public protocol ObjCReverseWeakDelegate: AnyObject {
    /// Returns `value + 3000`.
    func objcWeakValue(_ value: Int32) -> Int32
}

/// R6: the `@objc` flavour of the `weak var delegate: (any P)?` sink. NSObject-rooted, since
/// an `@objc` member needs an ObjC-visible enclosing class.
public class ObjCReverseWeakSinkHarness: NSObject {
    /// Non-retaining, zeroing storage over a bare ObjC object pointer.
    public weak var objcDelegate: (any ObjCReverseWeakDelegate)?

    public override init() {
        super.init()
    }

    /// Dispatches through the `@objc` weak sink. `-1` means the storage read nil — expected
    /// once the consumer drops the implementation, a regression while they still hold it.
    public func invokeObjCWeak(value: Int32) -> Int32 {
        return objcDelegate?.objcWeakValue(value) ?? -1
    }

    /// Observes the storage without vending an existential across the boundary, so a liveness
    /// assertion cannot itself mint a carrier that changes what it measures.
    public var hasObjCDelegate: Bool {
        return objcDelegate != nil
    }
}

/// Class-bound reverse-dispatch protocol stored through a NON-OPTIONAL `unowned` sink.
public protocol ReverseUnownedSlotDelegate: AnyObject {
    /// Returns `value + 4000`.
    func unownedSlotValue(_ value: Int32) -> Int32
}

/// R6: `unowned var delegate: any P` — legal for a class-bound `P`, and not Optional, so its
/// setter never takes the decomposed-optional arm. The sink still retains nothing, so the
/// conformer box must follow the consumer's implementation exactly as in the Optional case.
///
/// The slot is never read after the implementation is dropped: reading a dangling `unowned`
/// traps by design, so the carrier's release is observed through the managed-side census
/// instead.
public class ReverseUnownedSlotHarness {
    /// Non-retaining, non-zeroing storage. Non-Optional, so it must be seeded through `init`.
    public unowned var slotDelegate: any ReverseUnownedSlotDelegate

    public init(slotDelegate: any ReverseUnownedSlotDelegate) {
        self.slotDelegate = slotDelegate
    }

    /// Dispatches through the `unowned` slot; returns `value + 4000` while the implementation
    /// is live.
    public func invokeSlot(value: Int32) -> Int32 {
        return slotDelegate.unownedSlotValue(value)
    }
}
