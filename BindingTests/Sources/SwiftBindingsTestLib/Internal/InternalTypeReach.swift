// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Internal-Type-Reach Fixture (Pattern 2 emission-time gate)
//
// Models the dominant Pattern 2 shape from CryptoSwift / SkeletonView /
// NVActivityIndicatorView / XMLCoder: `@usableFromInline internal` types
// that show up in the swiftinterface and become reachable from emitted
// wrappers. Swift's source rules forbid a plain `public` member from naming
// an internal type in its signature, so the compile-time positive cases here
// are limited to what the language actually accepts. The fixture exercises
// several distinct skip points; the runtime absence assertions are what
// matter — the comments below name the gate that actually fires for each
// case so future readers can update the right code if a regression appears.
//
//   * `@usableFromInline internal` *methods* on a *public* type
//     (`PublicHostWithInternalMembers.registerCarrier` / `makeCarrier`).
//     Method emission has no pre-existing `IsModuleInternal` filter for
//     TypeDecl-parented methods, so these reach the new walker gate in
//     `MemberValidationPipeline.ValidateMethodEmission` and skip with
//     `SkipReason.Pattern2InternalTypeReach`. THIS is the case the new gate
//     was added for.
//
//   * `@usableFromInline internal` free functions (`makeCarrier`,
//     `readCarrier`). Skipped by the pre-existing early gate at
//     `MemberValidationPipeline.cs:70` (`IsModuleInternal && parent is
//     ModuleDecl`) BEFORE the new walker gate runs. Kept here as runtime
//     absence assertions, not as evidence of the new gate.
//
//   * `@usableFromInline internal` *property* on a public type
//     (`PublicHostWithInternalMembers.freshCarrier`). Skipped by the
//     pre-existing `MemberEmissionValidator.CanEmitProperty` filter
//     (`property.IsModuleInternal`) before `PropertyHandler` reaches
//     `MemberValidationPipeline.ValidatePropertyEmission`. The new property
//     gate is a belt-and-braces backstop that today only fires for protocol
//     properties via `MemberGateEvaluator.EvaluateProperty`. Kept as a
//     runtime absence assertion.
//
//   * `@usableFromInline internal` *subscript* on a public type
//     (`PublicHostWithInternalMembers.subscript[carrier:]`). Caught by the
//     new walker gate via the subscript index branch — `SubscriptHandler`
//     calls `MemberValidationPipeline.ValidateSubscriptEmission` first
//     thing in `EmitSubscripts` (`SubscriptHandler.cs:47`) before any
//     other check. (The unrelated `MemberEmissionValidator.CanEmitSubscript`
//     path is for conformance validation, not concrete emission.)
//
//   * `@usableFromInline internal` *types* with `public` member methods
//     (`InternalHolder.describe()`). Swift allows the methods (their declared
//     signatures are public-only), but the generated wrapper bodies must
//     reference the internal parent type. This shape is **formally retained
//     as post-processing scope**: the receiver-aware emission-time gate is
//     not viable because at emission time we don't know whether the
//     containing type satisfies a public protocol that the C# co-gater would
//     need to protect (CryptoSwift `BlockEncryptor : Cryptor` was the
//     concrete regression that rejected option (a)). The
//     `SwiftWrapperPostProcessor` Pattern 2 (B) body-reference scrub strips
//     the broken wrapper, and `CSharpWrapperCoGater` (with its
//     `BuildTypeProtectedMembers` interface-member protection) removes the
//     C# member when there's no protocol to satisfy.
//
//   * `@frozen public struct` with `@usableFromInline internal` stored
//     properties — the public storage boundary. The struct is constructible
//     from C#; the internal-typed property accessor is suppressed by the
//     pre-existing `MemberEmissionValidator.CanEmitProperty` filter (same
//     pre-existing path as `freshCarrier`).
//
// The negative side (`DoesNotReachInternal`) exists so we notice immediately
// if any of these gates over-strip neighbouring public-only signatures.

// MARK: Internal carriers — visible in the swiftinterface, NOT to consumers.

/// `@usableFromInline internal` struct exposed to the ABI for the dominant
/// Pattern 2 shape. Plays the role of CryptoSwift's `BlockEncryptor` /
/// XMLCoder's `_XMLPlistEncodingContainer` — referenced from emitted wrappers
/// but off-limits to the binding's external surface.
///
/// The C# binding emits this as a shell type (no type-level filter exists for
/// `@usableFromInline internal`; the metadata anchor is needed for cross-module
/// references), but every consumer-facing entry point is gated. The
/// runtime-test invariant is that *no public constructor exists* on the
/// emitted shell — see `TestInternalCarrierTypeIsUncreatable`.
@usableFromInline
internal struct InternalCarrier {
    @usableFromInline
    internal var value: Int32

    @usableFromInline
    internal init(value: Int32) {
        self.value = value
    }
}

// MARK: Positive — internal-typed signatures (caught by various gates).

/// `@usableFromInline internal` free function whose return type reaches an
/// internal type. Caught by the pre-existing early gate in
/// `MemberValidationPipeline.cs:70` (`IsModuleInternal && parent is
/// ModuleDecl`) BEFORE the new walker gate runs. Kept here so the runtime
/// absence assertion notices if the early gate ever regresses.
@usableFromInline
internal func makeCarrier(value: Int32) -> InternalCarrier {
    return InternalCarrier(value: value)
}

/// Same shape, but parameter rather than return. Same gate (early
/// ModuleInternal) catches it.
@usableFromInline
internal func readCarrier(_ carrier: InternalCarrier) -> Int32 {
    return carrier.value
}

/// `@usableFromInline internal` class with `public` member methods. The
/// methods' declared signatures are public-only (Swift refuses anything
/// else), so the signature-reach walker does not catch them. The generated
/// wrapper bodies must reference `InternalHolder` as `self`, and the Swift
/// compiler rejects internal-type references inside `@_cdecl` bodies. This
/// shape is formally retained as post-processing scope: the
/// `SwiftWrapperPostProcessor` Pattern 2 (B) body-reference scrub strips
/// these wrappers post-emission, and `CSharpWrapperCoGater` removes the
/// matching C# members (preserving interface-implementation members so
/// types that conform to public protocols still compile).
@usableFromInline
internal class InternalHolder {
    @usableFromInline
    internal var label: String

    @usableFromInline
    internal init(label: String) {
        self.label = label
    }

    public func describe() -> String {
        return label.uppercased()
    }
}

/// `@frozen public struct` with a `@usableFromInline internal` stored
/// property. Constructible from C# via the public init; the internal-typed
/// stored property accessor stays suppressed by the existing internal-member
/// filter, so the new gate's only job here is "do not over-strip the public
/// init."
@frozen
public struct PublicWithInternalStored {
    @usableFromInline
    internal var carrier: InternalCarrier

    public init(seed: Int32) {
        self.carrier = InternalCarrier(value: seed)
    }

    /// Public-only signature — must continue to emit even though the parent
    /// struct holds an internal-typed stored property.
    public func seedDoubled() -> Int32 {
        return carrier.value &* 2
    }
}

/// `@usableFromInline internal` members on a *public* type. The two methods
/// (`registerCarrier`, `makeCarrier`) are the case the new emission-time
/// gate is specifically designed to catch end-to-end: parent is a public
/// TypeDecl, so the early ModuleInternal gate at
/// `MemberValidationPipeline.cs:70` (which only fires for `parent is
/// ModuleDecl`) does NOT skip them, and `MemberEmissionValidator.CanEmitMethod`
/// has no equivalent IsModuleInternal filter — the methods reach
/// `ValidateMethodEmission`, where the new walker gate catches them with
/// `SkipReason.Pattern2InternalTypeReach`.
///
/// The subscript here also exercises the new walker gate (subscript-index
/// branch, via `SubscriptHandler.EmitSubscripts` →
/// `ValidateSubscriptEmission`). The property is suppressed by an older gate
/// (`MemberEmissionValidator.CanEmitProperty` IsModuleInternal filter) before
/// the new property gate ever runs — see the file-level comment for the
/// per-member gate map. Both stay as runtime absence assertions guarding
/// against regressions in any of those paths.
public struct PublicHostWithInternalMembers {
    public init() {}

    /// Public-only sibling — must keep emitting. Catches over-stripping if any
    /// of the gates ever generalizes from "signature reaches an internal type"
    /// to "any member on a host with at least one internal member."
    public func plain(value: Int32) -> Int32 {
        return value &* 2
    }

    /// Internal method whose parameter reaches `InternalCarrier`. THIS hits
    /// the new walker gate via the parameter branch — the only fixture entry
    /// that does so for the parameter case.
    @usableFromInline
    internal func registerCarrier(_ c: InternalCarrier) -> Int32 {
        return c.value
    }

    /// Internal method whose return type reaches `InternalCarrier`. THIS hits
    /// the new walker gate via the return-type branch — the only fixture
    /// entry that does so for the return case.
    @usableFromInline
    internal func makeCarrier(value: Int32) -> InternalCarrier {
        return InternalCarrier(value: value)
    }

    /// Internal computed property whose declared type reaches `InternalCarrier`.
    /// Caught by `MemberEmissionValidator.CanEmitProperty`'s pre-existing
    /// `property.IsModuleInternal` filter (line ~84) BEFORE PropertyHandler
    /// reaches `MemberValidationPipeline.ValidatePropertyEmission`. Kept as a
    /// runtime absence assertion.
    @usableFromInline
    internal var freshCarrier: InternalCarrier {
        return InternalCarrier(value: 0)
    }

    /// Internal subscript whose index parameter reaches `InternalCarrier`.
    /// Caught by the NEW walker gate via the subscript index branch —
    /// `SubscriptHandler.EmitSubscripts` calls
    /// `MemberValidationPipeline.ValidateSubscriptEmission` first thing
    /// (`SubscriptHandler.cs:47`) before any other check. This is the third
    /// fixture entry that exercises the new gate end-to-end.
    @usableFromInline
    internal subscript(carrier c: InternalCarrier) -> Int32 {
        return c.value
    }
}

// MARK: Negative — public surface that does NOT reach the internal types.

/// Every member here must still emit. Exercised in `RuntimeTestsApp` to
/// catch over-stripping in the new gate (false positives via name collision,
/// generic-arg over-walk, etc.).
public struct DoesNotReachInternal {
    public var label: String

    public init(label: String) {
        self.label = label
    }

    public func plain(value: Int32) -> Int32 {
        return value &* 2
    }

    public func optionalString(value: Int32?) -> String? {
        guard let value else { return nil }
        return "\(value)"
    }

    public func describe(values: [Int32]) -> [String] {
        return values.map { "v=\($0)" }
    }

    public var seedValue: Int32 {
        return 11
    }

    public subscript(stringAt index: Int32) -> String {
        return "[\(index)]"
    }
}
