// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Regression: dependent-member same-type constraint on extension property
//
// AppIntents 0.12.0 regen produced unsatisfiable `_SBW_PG_*` conformance
// extensions for properties declared on `extension IntentParameter
// where Value.ValueType == X`. The constraint targets an associated type
// (`Value.ValueType`) rather than the generic parameter itself (`Value`), so it
// lands in `GenericArgumentDecl.AssosiatedTypeConformances` rather than
// `GenericConformances`. `ExtractSameTypeConstraint` only inspects the latter
// (it needs a single concrete parent generic argument to re-emit a closed
// extension), so without a separate gate the open-generic protocol-group
// emission produces:
//
//     extension AppIntents.IntentParameter: _SBW_PG_82163CB5 {}
//
// Swift then fails to compile that conformance because `displayName` only
// exists when `Value.ValueType == Bool` — the unconstrained extension cannot
// satisfy the protocol's member requirement.
//
// The fix gates these properties in `MemberEmissionValidator.CanEmitProperty`:
// when the parent is generic and the property carries a same-type constraint
// on a parent associated type, drop it from emission. The closed-extension
// path cannot re-surface it either (no single concrete parent generic
// argument satisfies `Value.ValueType == X`), so the property is genuinely
// absent — but the type compiles, and other unconstrained members on the
// generic survive.
//
// The runtime test in DependentMemberSameTypeConstraintTests.cs verifies that:
//   1. `DependentMemberHost<BoolCarrier>` loads as a type via reflection —
//      pre-fix, the synthesized PG conformance would have failed the Swift
//      wrapper compile and the binding assembly would not have linked.
//   2. The unconstrained `Wrapped` reflection PropertyInfo is present,
//      proving the gate is targeted at the constrained property only.
//   3. `DisplayName` is NOT emitted as a property on the host (reflection
//      asserts absence — the dependent-member-constrained property is
//      dropped, not silently stubbed).
//
// The fixture deliberately uses reflection-only assertions and never
// constructs a host instance: the generated `DependentMemberHost(TValue)`
// constructor is marked [Obsolete(..., DiagnosticId = "SB0001")] (direct
// Swift ABI P/Invoke without an @_cdecl wrapper or native thunk), so a
// runtime invocation would SIGSEGV. That gap is a pre-existing generator
// limitation for generic-struct constructors with associated-type protocol
// constraints — orthogonal to the dependent-member same-type fix this
// fixture exists to cover.

public protocol AssociatedValue {
    associatedtype ValueType
    var rawValue: ValueType { get }
}

// Non-generic conformer: a generic Carrier<T> would force the generated
// DependentMemberHost<Carrier<Bool>> constructor through a generator path that
// currently lacks an @_cdecl wrapper (marked [Obsolete("No @_cdecl wrapper or
// native thunk available")] in the bindings), which crashes at runtime — a
// pre-existing limitation orthogonal to the dependent-member same-type fix.
// The parser still records `where Value.ValueType == Bool` as a dependent-member
// constraint (`Path = [τ_0_0, ValueType]`) regardless of whether Carrier itself
// is generic, so the gate is still exercised end-to-end.
public struct BoolCarrier: AssociatedValue {
    public typealias ValueType = Bool
    public let rawValue: Bool
    public init(rawValue: Bool) {
        self.rawValue = rawValue
    }
}

public struct DependentMemberHost<Value: AssociatedValue> {
    public let wrapped: Value
    public init(wrapped: Value) {
        self.wrapped = wrapped
    }
}

extension DependentMemberHost where Value.ValueType == Bool {
    // Pre-fix: this property surfaced on the unconstrained open-generic
    // `DependentMemberHost<Value>` and the PG emitter synthesized an
    // unsatisfiable `extension DependentMemberHost: _SBW_PG_… {}` to project
    // it — failing the Swift wrapper compile. Post-fix: gated by
    // MemberEmissionValidator.CanEmitProperty when the constraint lands in
    // AssosiatedTypeConformances rather than GenericConformances.
    public var displayName: String {
        return "bool-host"
    }
}
