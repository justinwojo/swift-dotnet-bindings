// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Durable gate for the GSF (generic-static-factory) constructor path's parent-generic-param
// `where` clause emission (WrapperEmitterHelpers.BuildParentSameTypeExtensionWhere).
//
// `MarkerCtorBox<Value>` is an UNCONSTRAINED generic struct whose `init(sendableCount:)` lives in
// `extension MarkerCtorBox where Value: Sendable` — a constructor-added STDLIB MARKER constraint.
// This is the one constraint shape that reaches the helper's conformance branch in practice:
// same-type pins and real-protocol stricter conformances are refused upstream
// (HasSameTypeConstraintOnParentGenericParam / HasUnsatisfiableParentGenericExtensionConstraint),
// but a marker is dropped from `GenericConformances` (so the admissibility gate never sees it)
// while it survives in `ParsedGenericSignature`.
//
// The marker MUST be dropped from the emitted GSF conformance: Swift forbids a non-marker
// protocol's conditional conformance from depending on a marker protocol
// ("conditional conformance to non-marker protocol '_SBW_GSF_…' cannot depend on conformance of
// 'Value' to marker protocol 'Sendable'"). If the helper emits
// `extension MarkerCtorBox: _SBW_GSF_… where Value: Sendable`, the wrapper fails `swiftc`, the
// block is stripped, and the C# `init(sendableCount:)` is left dangling. Dropping the marker
// leaves an UNCONDITIONAL `extension MarkerCtorBox: _SBW_GSF_…` that compiles and dispatches
// correctly (a marker has no runtime witness). Reverting the drop turns the `--compile-only`
// gate red.
public struct MarkerCtorBox<Value> {
    public let tag: String
    public init(tag: String) { self.tag = tag }
}

extension MarkerCtorBox where Value: Sendable {
    public init(sendableCount: Int) {
        self.init(tag: "sendable-\(sendableCount)")
    }
}
