// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - ~Copyable values reaching a lane that can only copy them
//
// `TrackedResource` (Types/Noncopyable.swift) already covers the lanes that hand the value
// straight across the seam: a `consuming` parameter, a `consuming` self, a plain return. The
// shapes here are the ones where the value is reached INDIRECTLY — through a closure's argument
// or result, through an async staging buffer, or out of a constructor's result carrier. Every one
// of those lanes materialises the value with the value witness table's `initializeWithCopy`,
// which for a `~Copyable` type is `__swift_cannot_copy_noncopyable_type`: an unconditional runtime
// trap, never a compile error. Both compilers accept these declarations, so nothing but an
// emission-time decision keeps a consumer off them.
//
// Each shape is paired with a structurally identical COPYABLE twin. The twins are the adversarial
// half: they must keep binding exactly as they always have, which is what proves the decision is
// keyed on copyability and not on "a closure appears in the signature" or "the member is async".

/// The copyable twin of `TrackedResource` — same single `Int32` field, same accessor, no `deinit`
/// (a copyable struct cannot have one). Anything the generator does differently between this type
/// and `TrackedResource` is attributable to copyability alone.
public struct CopyableResourceToken {
    public let id: Int32

    public init(id: Int32) {
        self.id = id
    }

    public func peek() -> Int32 {
        return id
    }
}

// MARK: - Closure argument / result lanes

/// A closure whose ARGUMENT is `~Copyable`. The closure value itself is perfectly copyable, so the
/// member-level "is this value non-copyable" oracle answers false for it — the non-copyable type is
/// reached only by descending into the closure's argument list.
public func inspectThroughClosure(_ body: (borrowing TrackedResource) -> Int32) -> Int32 {
    let resource = TrackedResource(id: 4101)
    return body(resource)
}

/// The `@escaping` flavour: a different closure emission path from the non-escaping one above.
public func inspectThroughEscapingClosure(_ body: @escaping (borrowing TrackedResource) -> Int32) -> Int32 {
    let resource = TrackedResource(id: 4102)
    return body(resource)
}

/// The throwing flavour: a third closure emission path, with the error-propagation prologue.
public func inspectThroughThrowingClosure(_ body: (borrowing TrackedResource) throws -> Int32) throws -> Int32 {
    let resource = TrackedResource(id: 4103)
    return try body(resource)
}

/// Two arguments, so the closure's argument list is a TUPLE rather than a bare type — the shape
/// that proves the reach test descends through tuple elements and not just single-argument closures.
public func inspectThroughPairClosure(_ body: (borrowing TrackedResource, Int32) -> Int32) -> Int32 {
    let resource = TrackedResource(id: 4104)
    return body(resource, 3)
}

/// The `~Copyable` in RESULT position rather than argument position.
public func produceThroughClosure(_ make: () -> TrackedResource) -> Int32 {
    let resource = make()
    return resource.peek()
}

// Copyable twins — identical lanes, copyable payload. These must keep binding.

public func inspectThroughCopyableClosure(_ body: (CopyableResourceToken) -> Int32) -> Int32 {
    return body(CopyableResourceToken(id: 4201))
}

public func inspectThroughEscapingCopyableClosure(_ body: @escaping (CopyableResourceToken) -> Int32) -> Int32 {
    return body(CopyableResourceToken(id: 4202))
}

public func inspectThroughThrowingCopyableClosure(_ body: (CopyableResourceToken) throws -> Int32) throws -> Int32 {
    return try body(CopyableResourceToken(id: 4203))
}

public func inspectThroughCopyablePairClosure(_ body: (CopyableResourceToken, Int32) -> Int32) -> Int32 {
    return body(CopyableResourceToken(id: 4204), 3)
}

public func produceCopyableThroughClosure(_ make: () -> CopyableResourceToken) -> Int32 {
    return make().peek()
}

// MARK: - Async lanes

/// A `~Copyable` in async PARAMETER position. The async lane stages every non-frozen parameter in a
/// copy buffer that outlives the suspension, because the C# caller keeps its own handle across the
/// await — the caller's continued ownership is exactly why there is no take to substitute here.
public func awaitTrackedResourcePeek(_ resource: borrowing TrackedResource) async -> Int32 {
    return resource.peek()
}

/// The `consuming` flavour of the same position. Swift takes the value, but the C# side still
/// stages a copy for the duration of the await, so this reaches the same buffer.
public func consumeTrackedResourceAsync(_ resource: consuming TrackedResource) async -> Int32 {
    return resource.peek()
}

/// A `~Copyable` in async RETURN position. This is the one lane here with an owner to hand the
/// value over from: the async harness allocates the result carrier for this one consumer and frees
/// it afterwards, so C# can take the value out of it instead of duplicating it. The lane is a take
/// rather than a refusal, and the member binds.
public func makeTrackedResourceAsync(id: Int32) async -> TrackedResource {
    return TrackedResource(id: id)
}

// Copyable twins.

public func awaitCopyableResourcePeek(_ token: CopyableResourceToken) async -> Int32 {
    return token.peek()
}

public func makeCopyableResourceAsync(id: Int32) async -> CopyableResourceToken {
    return CopyableResourceToken(id: id)
}

// MARK: - Constructor-result lanes

/// A FAILABLE constructor whose `Self` is `~Copyable`. A failable initializer's result is spelled
/// `Optional<Self>`, and `Optional` is itself `~Copyable` when its payload is, so the member is
/// already refused one gate earlier as a non-copyable value in a generic slot — before the factory
/// that reads `Self` back out of the carrier is ever reached. The copyable twin below is what shows
/// the failable lane itself still binds.
public struct TrackedFailableResource: ~Copyable {
    public let id: Int32

    public init?(id: Int32) {
        if id < 0 {
            return nil
        }
        self.id = id
        recordTrackedAllocation()
    }

    public borrowing func peek() -> Int32 {
        return id
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// The copyable twin of the failable constructor.
public struct CopyableFailableResource {
    public let id: Int32

    public init?(id: Int32) {
        if id < 0 {
            return nil
        }
        self.id = id
    }

    public func peek() -> Int32 {
        return id
    }
}

/// Label protocol for the existential-argument constructor below.
public protocol TrackedResourceLabel {
    var labelText: String { get }
}

/// A concrete label, so a C# test has something to pass.
public struct PlainResourceLabel: TrackedResourceLabel {
    public let labelText: String

    public init(labelText: String) {
        self.labelText = labelText
    }
}

/// A non-failable, non-throwing constructor with an EXISTENTIAL argument and a `~Copyable` `Self`.
/// A bare `any P` argument is not a bound generic, so the bypass factory never claims this shape to
/// begin with — it only claims a constructor with an omittable, defaulted existential inside a
/// container. What this fixture holds down is the ordinary constructor path for the same combination:
/// `Self` is `~Copyable`, the argument is an existential, and the result is written straight into the
/// buffer C# owns — a move, and a binding, rather than a refusal. The bypass factory's own decline is
/// pinned by a unit test on the predicate it reads, not by a corpus member reaching that factory.
public struct TrackedLabeledResource: ~Copyable {
    public let id: Int32
    public let label: String

    public init(id: Int32, label: any TrackedResourceLabel) {
        self.id = id
        self.label = label.labelText
        recordTrackedAllocation()
    }

    public borrowing func peek() -> Int32 {
        return id
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// The copyable twin of the existential-argument constructor.
public struct CopyableLabeledResource {
    public let id: Int32
    public let label: String

    public init(id: Int32, label: any TrackedResourceLabel) {
        self.id = id
        self.label = label.labelText
    }

    public func peek() -> Int32 {
        return id
    }
}
