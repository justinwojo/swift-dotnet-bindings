// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Intra-protocol async/sync effect overload (audit §6 #12)
//
// A SINGLE protocol declaring BOTH a sync and an async method that share name +
// params + return TYPE — `func m(_:) -> Int32` AND `func m(_:) async -> Int32`.
// This is valid Swift: effectful overloading makes them two DISTINCT witness-table
// requirements occupying two SEPARATE vtable slots.
//
// Pre-fix bug (§6 #12): the three intra-protocol method-identity keys
// (`EveryProtocolEmitter.GetMethodKey`, `WitnessDispatchEmitter.GetMethodKey`,
// `ProtocolSignatureHelper.GetMethodSignatureKey`) keyed only `name(labels:types)`
// with NO `async` axis, so the two requirements COLLAPSED onto ONE slot. The async
// requirement's slot / receiver was dropped while the C# interface still declared
// BOTH `IntraEffectTag(int)` and `IntraEffectTagAsync(int, CancellationToken)` — a
// missing dispatch (or CS1061/CS1955 if a later layer referenced the dropped member).
// Worse, dropping a slot drifts the C# proxy vtable's slot count from Swift's
// witness-table layout — a StructLayout mismatch.
//
// Fix: all three slot-allocation keys carry the `async` effect, so the sync and async
// requirements get DISTINCT slots (index-preserving for every non-colliding protocol —
// only a protocol that currently collapses an async/sync pair gains its second slot).
// This is the INTRA-protocol twin of the CROSS-protocol sibling fix in
// SiblingMethodDispatch.swift (AsyncRefineModifierBase / SyncRefineModifier) — one
// keying mechanism over (per-protocol slot allocation, not owner/peer witness grouping).
//
// `: AnyObject` so the C# proxy is class-backed. The C# impl implements BOTH members;
// only the SYNC path is exercised at runtime (async over CallConvSwift hits Mono Issue-1
// on the simulator — the async slot is compile-gated, and the sync round-trip proves the
// sync slot routes correctly with the async slot present).
public protocol IntraEffectTagged: AnyObject {
    func intraEffectTag(_ n: Int32) -> Int32
    func intraEffectTag(_ n: Int32) async -> Int32
}

// SYNC driver — routes the sync requirement through the proxy's sync vtable slot. If the
// two requirements collapsed onto one slot (pre-fix), the sync slot index would shift or
// the async slot would be missing; with both slots present the sync call reaches the C#
// impl's `IntraEffectTag(int)`. Runtime-callable on Mono (no async execution).
public func callIntraEffectTagSync(_ x: any IntraEffectTagged, _ n: Int32) -> Int32 {
    return x.intraEffectTag(n)
}
