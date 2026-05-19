// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Regression fixture for the parent-only sync CSM gap. Before this work, the
// CSM engine (`ConcreteSpecializationEngine.FindSpecializableMethods`)
// filtered out any method with zero method-own generic parameters via an
// `ownParams.Count == 0` early continue, so plain instance methods on a
// PAT-constrained generic parent never reached the per-conformer extension
// emission path in `EmitConcreteSpecializationsForGenericParent`. The
// emitter's `methodParams.Count == 0` branch was wired but unreachable.
//
// `CubbyBag<Item: Cubby>` mirrors the `MusicLibraryRequest<T>.filter(text:)`
// shape: a generic struct with one PAT-constrained parameter and instance
// methods that take no method-own generics. After the engine fix, each method
// must emit one overload per closed conformer inside a per-conformer
// `CubbyBag{Conformer}CsmExtensions` extension class — exactly the pattern
// `GenericContainer<T: SearchableItem>` already exercises for the
// method-generic case in `MethodLevelGenerics.swift`.
//
// `Cubby` is registered in `specialization-hints.json` with both
// `StringCubby` and `IntCubby` so the engine's parent-baseline resolver
// (`ResolveParentSpecializableParams`) finds non-empty conformer sets.

public protocol Cubby {
    associatedtype Slot
}

public struct StringCubby: Cubby {
    public typealias Slot = String
    public init() {}
}

public struct IntCubby: Cubby {
    public typealias Slot = Int32
    public init() {}
}

public struct CubbyBag<Item: Cubby> {
    public var counter: Int32 = 0
    public init() {}

    /// Parent-only mutating sync method. No method-own generics; `Item` is the
    /// parent's PAT param. Before the engine fix, this was silently dropped by
    /// CSM and would have fallen back to the BoundGenericsHandler path (direct
    /// CallConvSwift with metadata) — the same path that crashes Mono JIT on
    /// `GenericContainer.count()/tagBytes()`.
    public mutating func bump(by amount: Int32) {
        counter &+= amount
    }

    /// Parent-only mutating sync method with a second `Int32` argument so the
    /// test can witness an incremental delta plus a cumulative read in a single
    /// call. Returns the per-call increment for direct verification.
    public mutating func track(amount: Int32) -> Int32 {
        counter &+= amount
        return amount
    }

    /// Parent-only non-mutating sync read. Acts as the runtime witness that
    /// `bump(by:)` and `track(amount:)` actually mutated state inside the
    /// per-conformer-specialized cdecl wrapper.
    public func read() -> Int32 {
        return counter
    }
}

// MARK: - Closed-conformer factories
//
// Mirror `PatParentPlainProperties.swift`: expose typed factories so the C#
// test path does not depend on a generic constructor surface. The CSM
// extensions emit on `CubbyBag<StringCubby>` and `CubbyBag<IntCubby>` —
// callers obtain instances through these factories.

public func makeCubbyBagStringCubby() -> CubbyBag<StringCubby> {
    return CubbyBag<StringCubby>()
}

public func makeCubbyBagIntCubby() -> CubbyBag<IntCubby> {
    return CubbyBag<IntCubby>()
}
