// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Forward-only (read-only) proxy for a reverse-impossible BUT forward-readable protocol
//
// Reproduces RealityFoundation `ModelComponent.materials -> [any Material]`, whose getter threw
// `NotSupportedException` at runtime because the `Material` proxy class was fully suppressed:
// `Material` cannot host an EveryProtocol reverse-dispatch conformance (an `__`-prefixed hidden
// requirement is stripped from the framework ABI JSON), yet `any Material` is perfectly readable
// through the existential's OWN witness table. Suppressing the whole proxy turned every getter that
// returns `any Material` / `[any Material]` / `(any Material)?` into a throwing stub.
//
// The hidden-requirement shape is NOT reproducible on this test library's toolchain: here
// swift-api-digester KEEPS `__`-prefixed names in the ABI JSON (unlike the Apple framework
// toolchain — see HiddenRequirementProtocolSkipping.swift), so the gate that fires for Material in
// the real framework never fires here. The IDENTICAL forward-read mechanism is instead driven
// through the deterministic shape: a protocol that INHERITS a stdlib protocol EveryProtocol cannot
// witness (`CustomStringConvertible` / `Equatable`). EveryProtocol can't synthesize those stdlib
// requirements, so the reverse conformance is impossible — but the existential is still a valid READ
// target. The forward-read path (`load(as: (any P).self)` + witness-table dispatch) is byte-for-byte
// the same one that fixes Material; only the reason the reverse conformance is impossible differs.
//
// Before the fix: a property/method returning `any P` / `[any P]` / `(any P)?` for such a `P`
// emitted `get => throw new NotSupportedException(...)`. After it: the forward-only proxy class is
// emitted and the getter projects the existential through the proxy.

/// Forward-readable protocol that inherits a stdlib protocol EveryProtocol cannot witness
/// (`CustomStringConvertible` → InheritsUnsatisfiedStdlibProtocol). Not class-bound, no `Self`
/// requirement, no associated types — a clean forward-only read target. `displayName`, the inherited
/// `description`, and `summary()` are plain `String` members dispatched through the existential's own
/// witness table.
public protocol ForwardReadableTrait: CustomStringConvertible {
    var displayName: String { get }
    func summary() -> String
}

public final class ForwardReadableTraitImpl: ForwardReadableTrait {
    public let displayName: String
    public init(displayName: String) { self.displayName = displayName }
    public var description: String { "desc(\(displayName))" }
    public func summary() -> String { "summary(\(displayName))" }
}

/// Vendor exposing the forward-only existential through the three read shapes the verified Material
/// bug spanned: a scalar `any P` property, an `[any P]` array property (the literal
/// `ModelComponent.materials` shape), and an `(any P)?` optional property — plus a method return.
/// Every one of these getters threw `NotSupportedException` before the fix.
public final class ForwardReadableVendor {
    private let one: ForwardReadableTraitImpl
    private let many: [ForwardReadableTraitImpl]
    public init() {
        one = ForwardReadableTraitImpl(displayName: "solo")
        many = [
            ForwardReadableTraitImpl(displayName: "alpha"),
            ForwardReadableTraitImpl(displayName: "beta"),
        ]
    }
    /// Scalar `any P` getter (the `ModelComponent.someMaterial: any Material` shape).
    public var primary: any ForwardReadableTrait { one }
    /// `[any P]` array getter (the literal `ModelComponent.materials: [any Material]` shape).
    public var all: [any ForwardReadableTrait] { many }
    /// `(any P)?` optional getter.
    public var maybe: (any ForwardReadableTrait)? { one }
    /// Method-return `any P`.
    public func makePrimary() -> any ForwardReadableTrait { one }
}

// MARK: - Equatable-constrained sibling (the literal PhysicsJoint shape)
//
// RealityFoundation `PhysicsJoint` is dropped with genericSig `<Self: Swift.Equatable>` — the same
// reverse-impossible-but-forward-readable family, surfaced through `Equatable` inheritance rather
// than `CustomStringConvertible`. EveryProtocol cannot synthesize `==`, so the reverse conformance
// is impossible; the existential remains a valid read target for the non-`Self` members. This sibling
// proves the admission generalizes across the stdlib protocols the gate recognizes.

/// Forward-readable protocol inheriting `Equatable` (the `PhysicsJoint: Equatable` shape). The
/// `jointLabel` String member is forward-dispatchable; the inherited `==` is a `Self`-typed static
/// requirement that is never part of the forward read path.
public protocol ForwardReadableJoint: Equatable {
    var jointLabel: String { get }
}

public final class ForwardReadableJointImpl: ForwardReadableJoint {
    public let jointLabel: String
    public init(jointLabel: String) { self.jointLabel = jointLabel }
    public static func == (lhs: ForwardReadableJointImpl, rhs: ForwardReadableJointImpl) -> Bool {
        lhs.jointLabel == rhs.jointLabel
    }
}

/// Vendor exposing the Equatable-constrained forward-only existential as a scalar getter.
public final class ForwardReadableJointVendor {
    private let joint: ForwardReadableJointImpl
    public init() { joint = ForwardReadableJointImpl(jointLabel: "hinge") }
    /// Scalar `any P` getter for the Equatable-constrained protocol.
    public var primaryJoint: any ForwardReadableJoint { joint }
}
