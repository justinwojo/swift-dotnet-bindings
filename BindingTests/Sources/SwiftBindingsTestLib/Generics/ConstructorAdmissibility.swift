// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Unified constructor-admissibility fixture (AppIntents EntityProperty shape)
//
// Reproduces the init-erasure facets that each independently caused the
// AppIntents `final class EntityProperty<Value>` Swift wrapper to fail `swiftc`
// (see src/docs/keypath-subsystem/08b-entityproperty-init-keypath.md). All flow
// through a PAT-constrained generic-parent final class so they exercise BOTH
// erasure mechanisms:
//
//   * CSM (Concrete Specialization Machinery) emits closed per-conformer
//     wrappers for `CtorAdmBox<CtorAdmIntValue>` / `<CtorAdmRopeValue>`.
//   * The `_SBW_CI_` open-type-erasure path emits an unconditional
//     `extension CtorAdmBox: _SBW_CI_{hash} {}` for non-generic inits.
//
// Facets and where they leak pre-fix:
//
//   (a) `_const`-param init (`constId: _const String`). The OPEN ctor path
//       already drops it (ConstructorWrapperEmitter `_const` gate), but CSM did
//       NOT share that gate, so CSM emitted a runtime wrapper passing a runtime
//       String for the `_const` param → "expect a compile-time constant literal".
//       Modeled on AppIntents' `EntityProperty<IntentFile>(identifier: String)`.
//
//   (b) constrained-extension SAME-TYPE init (`intMarker:` only where
//       `Value.Element == Int`). Pre-fix:
//         - the `_SBW_CI_` open path emits `extension CtorAdmBox: _SBW_CI_… {}`
//           unconditionally → "type 'CtorAdmBox<Value>' does not conform" (the
//           unconstrained type has no such init), AND
//         - CSM emits the closed form for the NON-satisfying conformer
//           (`CtorAdmBox<CtorAdmRopeValue>(intMarker:)`, Element == CtorAdmRope
//           ≠ Int) → "requires the types … be equivalent".
//
//   (c) constrained-extension CONFORMANCE init (`ropeFlag:` only where
//       `Value.Element: CtorAdmCollectionish`), mirroring AppIntents'
//       `where Value.UnwrappedType : Collection`. A module-local marker protocol
//       is used (rather than stdlib `Collection`) so the conformance is provable
//       from the indexed ABI — `Collection` on a stdlib payload is not in the
//       TypeDatabase and would fail-closed for every conformer, exercising only
//       the skip path. With a module marker, BOTH the satisfy and the skip path
//       are deterministic. Pre-fix CSM emits the closed form for the
//       NON-satisfying conformer (`CtorAdmBox<CtorAdmIntValue>(ropeFlag:)`, Int
//       is not CtorAdmCollectionish) → "does not conform" / "no exact matches".
//
// Conformer design — each `Value` conformer satisfies EXACTLY ONE constrained
// init, so the post-fix closed forms are unambiguous:
//   * CtorAdmIntValue:  Element == Int          → satisfies (b) only.
//   * CtorAdmRopeValue: Element == CtorAdmRope   → satisfies (c) only
//                       (CtorAdmRope: CtorAdmCollectionish).
//
// Distinct C# parameter TYPES per init avoid DuplicateSignature collisions
// (C# constructors overload on type, not label): constId→ctor(string),
// intMarker→ctor(nint), ropeFlag→ctor(bool), the admissible designated init→
// ctor(string, nint).
//
// Runtime assertions in ConstructorAdmissibilityTests.cs are reflection-only:
// the bound generic-parent constructors route through closed CSM/_SBW_CI_
// wrappers, and the fixture's point is that the binding ASSEMBLY LINKS (pre-fix
// the synthesized wrappers fail the Swift compile and nothing links) and that
// the correct closed specializations appear / the wrong ones are absent.

public protocol CtorAdmValue {
    associatedtype Element
    var element: Element { get }
}

// Module-local marker for the conformance-constraint facet — provable from the
// indexed ABI (unlike stdlib `Collection`), so Stage 2 can both prove and reject.
public protocol CtorAdmCollectionish {}

public struct CtorAdmRope: CtorAdmCollectionish {
    public init() {}
}

// Satisfies `where Value.Element == Int`; does NOT satisfy `: CtorAdmCollectionish`.
public struct CtorAdmIntValue: CtorAdmValue {
    public typealias Element = Int
    public let element: Int
    public init(element: Int) { self.element = element }
}

// Satisfies `where Value.Element: CtorAdmCollectionish`; does NOT satisfy `== Int`.
public struct CtorAdmRopeValue: CtorAdmValue {
    public typealias Element = CtorAdmRope
    public let element: CtorAdmRope
    public init(element: CtorAdmRope) { self.element = element }
}

public final class CtorAdmBox<Value: CtorAdmValue> {
    public let tag: String

    // Plain admissible designated init — no `_const`, no parent-generic extension
    // constraint. Distinct signature ctor(string, nint). MUST emit on every closed
    // form and back the open erasure (the unconstrained type genuinely has it).
    public init(tag: String, salt: Int) {
        self.tag = tag
        _ = salt
    }

    // Facet (a): `_const` parameter. CSM must SKIP this on every closed form (a
    // runtime wrapper cannot supply a compile-time constant literal). The open
    // path already drops it via the ShouldEmitWrapper `_const` gate.
    public convenience init(constId: _const String) {
        self.init(tag: constId, salt: 0)
    }

    // Facet (d): `@available(*, unavailable)` init, modeled on AppIntents'
    // unavailable `EntityProperty<IntentFile>` init. CSM must SKIP this on every
    // closed form (the normal path drops unavailable inits via the parser's
    // IsModuleInternal flag; CSM did not mirror that). Pre-fix CSM emits a
    // `CtorAdmBox<…>(legacyScale:)` wrapper calling an unavailable init →
    // "'init(legacyScale:)' is unavailable". A distinct-typed param (Double →
    // C# ctor(double)) routes it through the same CSM enumeration as the other
    // facets so the gate is exercised on the live path, not a zero-arg edge.
    @available(*, unavailable)
    public convenience init(legacyScale: Double) {
        self.init(tag: "", salt: Int(legacyScale))
    }
}

// Facet (b): same-type constraint on a parent-generic dependent member.
// Non-generic convenience init → routes through the `_SBW_CI_` Path-1 erasure.
// Open erasure must be SKIPPED (unconstrained CtorAdmBox<Value> lacks this init).
// CSM closed form must emit ONLY for CtorAdmIntValue (Element == Int), and be
// SKIPPED for CtorAdmRopeValue (Element == CtorAdmRope ≠ Int).
extension CtorAdmBox where Value.Element == Int {
    public convenience init(intMarker: Int) {
        self.init(tag: "int", salt: intMarker)
    }
}

// Facet (c): conformance constraint on a parent-generic dependent member,
// mirroring AppIntents' `where Value.UnwrappedType : Collection`. CSM closed
// form must emit ONLY for CtorAdmRopeValue (CtorAdmRope: CtorAdmCollectionish),
// and be SKIPPED for CtorAdmIntValue (Int is not CtorAdmCollectionish). Open
// erasure SKIPPED.
extension CtorAdmBox where Value.Element: CtorAdmCollectionish {
    public convenience init(ropeFlag: Bool) {
        self.init(tag: "rope", salt: ropeFlag ? 1 : 0)
    }
}
