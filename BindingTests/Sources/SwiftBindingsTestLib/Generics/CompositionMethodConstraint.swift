// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - CSM-eligible method with a protocol-COMPOSITION method-level constraint `<T: P & Q>`
//
// ABI Coverage Grid — generics corner. This fixture is the explicit revisit trigger for the
// roadmap Latent "CSM per-method where-clause filter: protocol-composition `T : P & Q` is
// treated as a single opaque target" (ConcreteSpecializationEngine.ParseMethodLevelConstraints).
//
// Unlike the open-generic FREE FUNCTION `multiConstrained<T: Describable & TestIdentifiable>`
// (which routes through the CallConvSwift open-generic fallback, NOT the engine), a method-level
// generic on a specializable HOST type is a Concrete-Specialization-Engine candidate: the engine
// is supposed to emit one concrete overload per known conformer that satisfies the constraint.
//
// The Latent: when the ABI signature serializes the per-method requirement as
// `τ_0_0 : SwiftBindingsTestLib.Describable & SwiftBindingsTestLib.TestIdentifiable`,
// ParseMethodLevelConstraints stores `"Describable & TestIdentifiable"` as ONE opaque target.
// `parentLevelNames.Contains(target)` then never matches a single declared protocol, so every
// conformer that satisfies BOTH protocols is false-rejected and the method is silently dropped
// (no `DescribeBoth(SimpleItem)` overload emitted). If the engine is correct, it splits the
// composition and verifies each protocol independently, emitting a specialization per conformer.
//
// SimpleItem and MultiProtocolEntity (Protocols/Conformance.swift) both conform to Describable
// AND TestIdentifiable, so a correct engine specializes `describeBoth` for each. The matching
// C# round-trip lives in CompositionMethodConstraintTests; if the Latent is live the method is
// absent and that test fails to compile (compile-gate red) — exactly the trigger this fixture
// is meant to fire.

public struct CompositionItemProcessor {
    public let prefix: String

    public init(prefix: String) {
        self.prefix = prefix
    }

    /// CSM-eligible: inline protocol-composition constraint on the method-level generic.
    public func describeBoth<T: Describable & TestIdentifiable>(_ item: T) -> String {
        return "\(prefix): [\(item.id)] \(item.describe())"
    }

    /// Same composition expressed as an explicit `where` clause — exercises the where-clause
    /// serialization path of the same filter (the Latent's `MissingConstraint` row contains
    /// `" & "` either way).
    public func tagBoth<T>(_ item: T, tag: Int32) -> String where T: Describable & TestIdentifiable {
        return "\(prefix)#\(tag): \(item.describe())"
    }
}
