// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Reverse-dispatch lifetime & identity invariants (Design B2 / Defect G)
//
// These fixtures pin the three behavioural invariants the Design B2 reverse-dispatch
// lifetime model introduces (see src/docs/session1-reverse-dispatch-lifetime-vtable.md):
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
