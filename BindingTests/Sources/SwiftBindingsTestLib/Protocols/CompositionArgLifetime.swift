// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - EC2 composition lifetime: argument keepAlive + owned-return mint
//
// These fixtures close the EC2+ (composition `any P & Q`) half of the Design B2 borrowed-alias
// category. The single-protocol (EC1) sites were migrated to owned-mint / keepAlive; EC2+ stayed on
// the borrowed `GetExistentialContainer()` form, which is only safe under the OLD strong proxy
// registration. Under B2 the proxy is registered weakly, so:
//
//  - A composition existential passed as a +0 BORROWED argument aliases the proxy's sole construction
//    +1 (R0). The only C# type implementing the composition interface is the Swift-vended
//    `{Composition}Proxy`, whose `GetExistentialContainer()` hands back its stored bytes with no fresh
//    retain. A GC between the bytes being copied into the call buffer and Swift finishing its borrow
//    could finalize the proxy → release R0 → Swift borrows freed memory.
//
//  - A composition existential returned C#->Swift at +1 (OWNED) without minting an independent retain
//    hands Swift a copy that aliases the proxy's one R0. Swift's owned release and the proxy's eventual
//    release then both target the SAME +1 — a double-release of one instance.
//
// The `Nameable`/`Ageable` protocols and the `TrackedNameableAgeable` LifetimeTracker-counted class
// live in Protocols/Composition.swift and MemoryManagement/ExistentialReturnLeak.swift respectively;
// `makeTrackedNameableAgeable(tag:)` vends an EC2 proxy owning a tracked instance.

// MARK: Owned-return (C#->Swift) — closure-return emission site

/// Calls the C#-provided factory to obtain `any Nameable & Ageable`, reads its fields, and lets
/// Swift's owned +1 drop at scope exit. Exercises the closure-RETURN owned-existential marshalling
/// (ClosureEmitter): the C# callback must hand Swift an INDEPENDENT +1. If it instead returns a
/// borrowed alias of the source proxy's sole +1, Swift's release here drops the shared tracked
/// instance to zero while C# still holds the proxy — a premature dealloc the C# side observes as a
/// live count of 0, and a double-release when the proxy later tears down.
public func consumeProvidedNameableAgeable(_ make: () -> any Nameable & Ageable) -> String {
    let entity = make()
    return "\(entity.name):\(entity.age)"
}

// MARK: Owned-return (C#->Swift) — reverse-dispatch getter emission site

/// Class protocol whose getter returns a composition existential. A C# conformer's getter is invoked
/// by Swift (reverse dispatch); the returned `any Nameable & Ageable` flows through the
/// receiver-getter owned-return marshalling (ProtocolProxyEmitter.Receivers) — a distinct emission
/// site from the closure path but the same owned-EC2 mint obligation.
public protocol NameableAgeableProvider: AnyObject {
    var provided: any Nameable & Ageable { get }
}

/// Reads the C# conformer's `provided` getter (reverse dispatch into C#) and lets Swift's owned +1
/// drop. Same double-ownership hazard as `consumeProvidedNameableAgeable`, through the getter site.
public func readProvidedNameableAgeable(_ provider: NameableAgeableProvider) -> String {
    let entity = provider.provided
    return "\(entity.name):\(entity.age)"
}

// MARK: Borrowed argument (C#->Swift) — tuple-of-existential parameter

/// Tuple-of-existential PARAMETER. SILGen lowers `(any Nameable & Ageable, any Nameable & Ageable)`
/// to two `@guaranteed` (+0 borrowed) existential arguments, so each element is passed borrowed,
/// aliasing its source proxy's R0. Exercises the TupleProjection per-element existential parameter
/// path: each borrowed leaf must be rooted across the native call, or a GC mid-call could finalize a
/// source proxy and release the container Swift is still borrowing.
public func describeNameableAgeablePair(_ pair: (any Nameable & Ageable, any Nameable & Ageable)) -> String {
    return "\(pair.0.name):\(pair.0.age) & \(pair.1.name):\(pair.1.age)"
}
