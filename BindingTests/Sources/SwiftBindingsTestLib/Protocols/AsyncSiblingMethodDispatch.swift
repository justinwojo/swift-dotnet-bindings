// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Sibling-protocol REAL-ASYNC method dispatch (S13 Pillar C fan-out)
//
// The async analog of SiblingMethodDispatch.swift's SiblingMethodOwner/Peer pair.
// Two class-bound protocols declare the SAME real-async-eligible signature — a
// non-throwing `func ...(_:) async -> Int32` pair AND a throwing
// `func ...(_:) async throws -> Int32` pair. Both signatures satisfy
// EmitsRealAsyncWitness (non-inout blittable-primitive arg + return, arity 1), so
// each protocol's EveryProtocol witness is a GENUINE continuation handoff
// (withCheckedContinuation / withCheckedThrowingContinuation), NOT the legacy
// thread-blocking slot.
//
// Because both protocols share the real-async signature, they form ONE owner group
// (the grouping key carries `async` for a real-async witness); the
// lexicographically-smaller protocol owns the witness body, the other gets an empty
// stitched extension that Swift's cross-extension witness resolution routes into the
// owner's body.
//
// Pre-fix bug: the real-async witness body force-unwrapped the OWNER's OWN widened
// vtable slot, and the owner's real-async receiver resolved only the owner interface —
// so a C# impl conforming to ONLY the non-owner peer left the owner slot nil and
// dispatch through the peer existential SIGSEGV'd on the nil function pointer; and once
// any owner proxy primed the owner's process-wide vtable, the owner branch fired but its
// receiver could not locate the peer's per-instance proxy and FailFast'd a live impl.
// This is the exact Bug #2 crash class the SYNC sibling witness already fans out to
// avoid (SiblingMethodOwner/Peer) — the real-async path simply never inherited the
// fan-out when S13 Pillar C added it.
//
// Fix: the real-async owner witness fans out across every sibling's widened vtable slot
// (dispatch through the first non-nil one, fatalError if none), and the owner's
// real-async receiver resolves the impl across the primary then each recorded sibling
// interface — exactly the EmitMethodFanOutBody + ComputeSiblingMethodFallbacks path the
// sync witness uses, now applied to the continuation-handoff slot.
//
// `AsyncSiblingOwner` sorts before `AsyncSiblingPeer` (Owner < Peer), so the owner is
// deterministic and the *Peer existential is the crash path. `: AnyObject` so the C#
// proxy is class-backed like the sync siblings.

public protocol AsyncSiblingOwner: AnyObject {
    func asyncSiblingModify(_ n: Int32) async -> Int32
}

public protocol AsyncSiblingPeer: AnyObject {
    func asyncSiblingModify(_ n: Int32) async -> Int32
}

// Driver for the OWNER existential — owner-body dispatch through the owner's own widened
// slot. Also the priming path: registering an owner proxy populates the owner's
// process-wide vtable, setting up the reverse-order (Case B) regression for the peer.
public func callAsyncSiblingViaOwner(_ x: any AsyncSiblingOwner, _ n: Int32) async -> Int32 {
    return await x.asyncSiblingModify(n)
}

// Driver for the PEER (non-owner) existential — the Bug #2 crash path. Routes into the
// owner's witness body via cross-extension resolution; only succeeds if that body fans
// out to the peer's populated vtable AND the receiver resolves the peer proxy.
public func callAsyncSiblingViaPeer(_ x: any AsyncSiblingPeer, _ n: Int32) async -> Int32 {
    return await x.asyncSiblingModify(n)
}

// MARK: - Throwing real-async sibling pair
//
// Proves the fan-out + receiver sibling-fallback are effect-agnostic: the widened slot
// is +3 (continuation box + success/error FPs) either way, and the throwing box wraps
// CheckedContinuation<Int32, Error> with a live `_error` resume symbol. Same Owner < Peer
// ownership.

public protocol AsyncSiblingThrowingOwner: AnyObject {
    func asyncSiblingThrowingModify(_ n: Int32) async throws -> Int32
}

public protocol AsyncSiblingThrowingPeer: AnyObject {
    func asyncSiblingThrowingModify(_ n: Int32) async throws -> Int32
}

public func callAsyncSiblingThrowingViaOwner(_ x: any AsyncSiblingThrowingOwner, _ n: Int32) async throws -> Int32 {
    return try await x.asyncSiblingThrowingModify(n)
}

public func callAsyncSiblingThrowingViaPeer(_ x: any AsyncSiblingThrowingPeer, _ n: Int32) async throws -> Int32 {
    return try await x.asyncSiblingThrowingModify(n)
}
