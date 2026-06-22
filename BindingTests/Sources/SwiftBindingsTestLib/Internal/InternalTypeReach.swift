// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Internal-Type-Reach Fixture (Pattern 2 emission-time gate)
//
// Models the dominant Pattern 2 shape where `@usableFromInline internal` types
// show up in the swiftinterface and become reachable from emitted wrappers. Swift's source rules forbid a plain `public` member from naming
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
//   * `@usableFromInline internal` *types* with `public` members
//     (`InternalHolder` + `InternalFrozenOperand`). Swift allows the members
//     (their declared signatures are public-only), but the generated wrapper
//     bodies must reference the internal parent type. The emission outcome
//     splits by whether a clean CallConvSwift fallback exists:
//       - **Sync** method/ctor/property/subscript (`describe()`,
//         `subscript(offset:)`) — caught at emission by
//         `WrapperValidation.GetMemberRejectionReason` arm 2b
//         (`parent_module_internal`): the broken `@_cdecl` wrapper is rejected
//         and the member falls back to a direct CallConvSwift P/Invoke against
//         the member's exported ABI symbol — the `Tj` dispatch thunk for a
//         non-final class's instance method or subscript getter, the bare silgen
//         symbol for a constructor or a struct/final-class member — so the
//         member is KEPT, not stripped, and a public protocol requirement is
//         still satisfied (no CS0535).
//       - **Async** / **closure-bearing** methods (`describeAsync()`,
//         `transform(using:)`, and the closure-RETURN case `makeAdder()`) — no
//         clean CallConvSwift fallback (async always needs a Swift bridge wrapper
//         that names the parent; a closure parameter degrades to a faulting legacy
//         CallConvSwift path; a closure return through a direct CallConvSwift
//         P/Invoke crashes Mono+NativeAOT), so the member is DROPPED at emission by
//         `MemberValidationPipeline.ValidateMethodEmission` — which scans the whole
//         signature, return type included — (`ParentModuleInternalNoFallback`).
//       - **Frozen-struct operators** (`InternalFrozenOperand.+`) — must be a
//         `@_cdecl` wrapper (a static-operator CallConvSwift P/Invoke crashes
//         ILC on NativeAOT) that names the parent, with no fallback, so the
//         operator is DROPPED at emission by `OperatorHandler.EmitOperator`
//         (`ParentModuleInternalNoFallback`).
//     The three emission-time drops are public-API-identical to the previous
//     emit-then-strip + C# reconcile, but decided at the emission layer, so the
//     `SwiftWrapperPostProcessor` no longer strips any internal-receiver shape
//     (the post-processor remains in place for the other strip classes it owns
//     — `NSInvocation`, EveryProtocol/safety-net placeholders, extension/private
//     `_SBW_` protocol blocks, and standalone wrapper funcs).
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
/// Pattern 2 shape. Plays the role of an internal type referenced from emitted
/// wrappers but off-limits to the binding's external surface.
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

/// `@usableFromInline internal` class with `public` members. Their declared
/// signatures are public-only (Swift refuses anything else), so the
/// signature-reach walker does not catch them. The generated wrapper bodies
/// must reference `InternalHolder` as `self`, and the Swift compiler rejects
/// internal-type references inside `@_cdecl` (and `@_silgen_name`) bodies. The
/// members split into two emission outcomes by whether a clean CallConvSwift
/// fallback exists:
///
///   * **Sync** members with a fallback — `describe()` (method) and
///     `subscript(offset:)` (a sync accessor pair like a property) — are caught
///     at emission by `WrapperValidation.GetMemberRejectionReason` arm 2b
///     (`parent_module_internal`): the broken `@_cdecl` wrapper is rejected and
///     each member falls back to a direct CallConvSwift P/Invoke against the
///     exported `Tj` dispatch thunk (a non-final class's instance method and
///     subscript-getter accessor are both vtable-dispatched, so the `Tj` thunk
///     is exported), so the member is KEPT (not stripped).
///
///   * **Async** (`describeAsync()`) and **closure-bearing** members — a closure
///     parameter (`transform(using:)`) or a closure RETURN (`makeAdder()`) — have
///     NO clean CallConvSwift fallback: an async member always needs a Swift bridge
///     wrapper (which still names the internal parent under `@_silgen_name`), a
///     closure parameter degrades to a legacy CallConvSwift path that faults at
///     runtime, and a closure return through a direct CallConvSwift P/Invoke
///     crashes Mono+NativeAOT. The correct emission outcome is therefore to DROP
///     the member, which `MemberValidationPipeline.ValidateMethodEmission` now does
///     (the parent-module-internal-no-fallback gate, reason
///     `ParentModuleInternalNoFallback`, scanning the whole signature so the
///     closure return is caught) — emission-time, before any wrapper is produced,
///     so nothing is left for the post-processor to strip. This is
///     public-API-identical to the previous emit-then-strip + C# reconcile, but
///     decided at the emission layer alongside the sync arm-2b decision.
///
/// All four members are unreachable at runtime by the construction barrier (the
/// `init` is `@usableFromInline internal`, so the emitted shell has no public
/// constructor), so they are strip-count-hygiene cases asserted via the
/// `wrapper_stripped_count` tripwire staying 0 and via member-presence/absence
/// on the emitted shell, not via a runtime call.
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

    /// Public subscript on the internal class — the same arm 2b case as
    /// `describe()`, but the `Subscript` member kind, so it exercises the
    /// `SubscriptWrapperEmitter` → `GetMemberRejectionReason` wiring. Read-only +
    /// blittable `Int32` to mirror the proven property fallback shape.
    public subscript(offset index: Int32) -> Int32 {
        return Int32(label.count) &+ index
    }

    /// Public **async** method on the internal class. An async member always
    /// needs a Swift bridge wrapper, and that wrapper still names the internal
    /// parent — there is no direct CallConvSwift fallback the way a sync member
    /// has one. So this is DROPPED at emission by
    /// `MemberValidationPipeline.ValidateMethodEmission`
    /// (`ParentModuleInternalNoFallback`) rather than emitted-then-stripped.
    public func describeAsync() async -> String {
        return label.lowercased()
    }

    /// Public **closure-bearing** method on the internal class. The closure
    /// wrapper body would name the internal parent, and the closure path's only
    /// fallback is a legacy CallConvSwift route that faults at runtime — no clean
    /// fallback, so this is DROPPED at emission by the same gate.
    public func transform(using f: (Int32) -> Int32) -> Int32 {
        return f(Int32(label.count))
    }

    /// Public method that **returns a closure** (no closure parameter) on the
    /// internal class. A closure return forces the closure-@_cdecl carrier — a
    /// closure returned through a direct CallConvSwift P/Invoke crashes Mono and
    /// NativeAOT (see `WrapperValidation.IsReturnTypeCdeclRequired`) — and that
    /// wrapper names the internal parent, so there is no fallback. This is the
    /// closure-RETURN trap: a parameter-only scan would let it slip past gate 3c
    /// into the arm-2b "keep via direct CallConvSwift" path and bind a crashing
    /// carrier. `MemberValidationPipeline.ValidateMethodEmission` scans the whole
    /// signature (return + parameters), so it is DROPPED at emission like the
    /// closure-parameter case (`ParentModuleInternalNoFallback`).
    public func makeAdder() -> (Int32) -> Int32 {
        let base = Int32(label.count)
        return { base &+ $0 }
    }
}

/// `@frozen @usableFromInline internal` struct whose only public member is an
/// operator. A frozen-struct operator must be emitted as a `@_cdecl` wrapper —
/// a direct CallConvSwift P/Invoke for a *static* operator function segfaults
/// ILC on NativeAOT (see `OperatorHandler.ShouldEmitOperatorWrapper`). That
/// wrapper body names the internal parent, which the separate
/// wrapper-compilation module cannot reference, and there is no CallConvSwift
/// fallback. So the operator is DROPPED at emission by
/// `OperatorHandler.EmitOperator` (`ParentModuleInternalNoFallback`) rather than
/// emitted-then-stripped.
///
/// Like `InternalCarrier`, the `init` is internal, so the emitted shell is not
/// constructible from C# — this is a strip-count-hygiene case asserted via the
/// `wrapper_stripped_count` tripwire staying 0 and the operator's absence from
/// the emitted shell, not via a runtime call.
@frozen
@usableFromInline
internal struct InternalFrozenOperand {
    @usableFromInline
    internal var value: Int32

    @usableFromInline
    internal init(value: Int32) {
        self.value = value
    }

    public static func + (lhs: InternalFrozenOperand, rhs: InternalFrozenOperand) -> InternalFrozenOperand {
        return InternalFrozenOperand(value: lhs.value &+ rhs.value)
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
