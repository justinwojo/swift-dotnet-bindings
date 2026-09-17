// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import SwiftBindingsTestLibDependency

// Two halves of the same shape: a requirement of protocol Q satisfied, for every conformer, by a
// default declared in `extension P where Self : Q`. Because the member is spelled on P, attributing
// it only to P leaves Q's requirement abstract in C# and every type that relies on the default
// fails to implement it.
//
// `DefaultedRow` covers the case where BOTH protocols come from a dependency module, so the
// abstract member is emitted while generating that dependency and the unimplementable conformance
// only appears here. `DefaultedHeightRow` covers the mirror image: the extension is declared HERE
// but extends a dependency protocol, which classifies it as a foreign-type extension and keeps it
// out of this module's protocol-extension defaults entirely.
//
// Both defaults are reached through an umbrella protocol rather than a direct conformance, because
// the two routes fail differently and both need holding down: a direct conformance that looks
// unsatisfiable is dropped by the conformance validator — quiet, and only visible as a type that no
// longer implements the interface — whereas an umbrella re-introduces the requirement
// unconditionally and fails the build outright.
//
// Every protocol here carries a requirement of its own. A requirement-less protocol is a separate
// emission shape with its own known C#-only vtable divergence, and pulling that into these fixtures
// would blur what they are holding down.

/// Umbrella protocol that inherits the dependency-module capability, mirroring a model protocol
/// that composes several small "providing" protocols.
public protocol TaggedRow: SectionTagging, TaggedModel {
    var rowIndex: Int32 { get }
}

/// Relies entirely on the dependency module's `extension TaggedModel where Self : SectionTagging`
/// for its `sectionTag`; it declares every other requirement itself, so `sectionTag` is the only
/// member that can be missing.
public struct DefaultedRow: TaggedRow {
    public var modelLabel: Int32 { 3 }
    public var rowIndex: Int32 { 5 }

    public init() {}
}

/// The main-module capability whose default is declared below in an extension of a DEPENDENCY
/// protocol.
public protocol RowHeighting {
    var rowHeight: Int32 { get }
}

extension TaggedModel where Self: RowHeighting {
    public var rowHeight: Int32 { 44 }
}

/// Declares `rowHeight` itself, and deliberately does NOT conform to `TaggedModel`, so it cannot
/// reach the default. Its presence is load-bearing: the phantom-default detector infers a hidden
/// default only when NO same-module conformer can emit the member, so an explicit conformer keeps
/// that net inert and holds the fix to attributing the constrained extension properly.
public struct ExplicitHeightRow: RowHeighting {
    public var rowHeight: Int32 { 88 }

    public init() {}
}

/// Umbrella over the main-module capability plus the dependency protocol that carries the default.
public protocol HeightedRow: RowHeighting, TaggedModel {
    var heightIndex: Int32 { get }
}

/// Relies on the foreign-extension default for its `rowHeight`.
public struct DefaultedHeightRow: HeightedRow {
    public var modelLabel: Int32 { 9 }
    public var heightIndex: Int32 { 11 }

    public init() {}
}

/// Reverse dispatch for the foreign-extension half. Passing both conformers proves the default
/// answers for one while the explicit member still wins for the other.
public func readRowHeight(_ value: any RowHeighting) -> Int32 {
    value.rowHeight
}
