// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Durable fail-closed gate for the GSF (generic-static-factory) constructor path's ONE stdlib
// marker that the open erased form cannot honour: `BitwiseCopyable`.
//
// `BitwiseCtorBox<Value>` is an UNCONSTRAINED generic struct. Its base `init(tag:)` is admissible
// and round-trips through unconditional GSF dispatch (see TestBaseInit_RoundTripsTag). Its
// `init(bitwiseCount:)` lives in `extension BitwiseCtorBox where Value: BitwiseCopyable` — a
// constructor-added marker constraint.
//
// Unlike the erasure-SAFE markers covered by `MarkerCtorBox` (Sendable/Copyable/Escapable/
// SendableMetatype), which the where-clause drop turns into a legal unconditional GSF conformance,
// `BitwiseCopyable` is a real layout requirement. There is NO legal open erased form for it:
//   • an unconditional `extension BitwiseCtorBox: _SBW_GSF_… { … Self(bitwiseCount:) … }` body
//     fails `swiftc` ("referencing initializer 'init(bitwiseCount:)' … requires that 'Value'
//     conform to 'BitwiseCopyable'"), stripping the wrapper and leaving the C# ctor dangling; and
//   • the marker cannot be re-stated as a conditional conformance (a non-marker protocol's
//     conditional conformance may not depend on a marker).
// So the `init(bitwiseCount:)` constructor MUST fail closed — be refused entirely, not emitted —
// via `ConstructorAdmissibility.HasUnerasableParentMarkerConstraint`. The C# surface must expose
// the base `(String)` init and NOT the marker `(nint)` init. Reverting the refusal makes
// TestBitwiseConstrainedInit_NotEmitted go red (the dangling `(nint)` ctor reappears).
public struct BitwiseCtorBox<Value> {
    public let tag: String
    public init(tag: String) { self.tag = tag }
}

extension BitwiseCtorBox where Value: BitwiseCopyable {
    public init(bitwiseCount: Int) {
        self.init(tag: "bitwise-\(bitwiseCount)")
    }
}

// ── Associated-type MEMBER-clause form (the harder under-refusal) ────────────────────────
//
// `where Value.Item: BitwiseCopyable` does NOT constrain the parent param directly — it
// constrains an associated-type member of it. A direct-conformance-only scan therefore skips it,
// but the unconditional open GSF body still fails `swiftc` ("referencing initializer
// 'init(bitwiseItemCount:)' … requires that 'Value.Item' conform to 'BitwiseCopyable'"), so the
// wrapper is stripped and the `(bitwiseItemCount:)` constructor must ALSO fail closed.
//
// `BitwiseItemCarrier` is a normal (non-marker) protocol, so the base `init(tag:)` — which inherits
// only `Value: BitwiseItemCarrier` from the type declaration — stays admissible: its GSF conformance
// is conditional on a NORMAL protocol, which Swift permits. Only the marker-on-member init is
// refused. Reverting `ConformanceTargetsRootedAt` (member-inclusive) back to the direct-only scan
// makes TestMemberBitwiseConstrainedInit_NotEmitted go red.
public protocol BitwiseItemCarrier {
    associatedtype Item
}

public struct MemberBitwiseCtorBox<Value: BitwiseItemCarrier> {
    public let tag: String
    public init(tag: String) { self.tag = tag }
}

extension MemberBitwiseCtorBox where Value.Item: BitwiseCopyable {
    public init(bitwiseItemCount: Int) {
        self.init(tag: "member-\(bitwiseItemCount)")
    }
}
