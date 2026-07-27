// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Skip-class fixture for the CROSS-MODULE closure tombstone.
//
// The SB0005 closure tombstone keeps an otherwise-unbindable member visible at the
// C# surface: the unsupported closure parameter collapses to `object?`, the member
// carries `[Obsolete(DiagnosticId="SB0005")]` + `[UnsupportedSwiftType(...)]`, and
// the body throws. Every OTHER parameter and the return type are rendered with their
// real projected C# names — which is the hazard when one of them belongs to a
// different Swift module.
//
// A consuming binding project declares its inter-framework dependency natively (the
// SDK resolves the dependency's xcframework so the Swift side can build against it),
// and that declaration does NOT put the dependency's managed assembly on the
// consumer's compile. So a tombstone that writes a dependency-module type name into
// the consumer's C# fails with CS0246 — an error with no fix available to the
// consumer, produced for a member that was being skipped anyway. Qualifying the name
// does not help: the assembly reference is missing, not the namespace.
//
// The tombstone must therefore disqualify itself whenever a rendered (non-closure)
// slot reaches a module the emitting module cannot see, dropping the member outright
// instead of emitting an uncompilable stub. This host pairs an unsupported closure
// (which is what makes the member tombstone-eligible in the first place) with a
// dependency-module type in a rendered slot — parameter and return position — so the
// disqualification is exercised on both axes.
//
// NOTE ON THE COMPILE GATE: the BindingTests compile check compiles this module and
// its dependency module into a SINGLE assembly, so the CS0246 this fixture describes
// cannot reproduce here. The fixture pins the observable emission outcome (no
// tombstone for these members); the reference-reachability decision itself is
// covered by the unit tests over the tombstone emitter's eligibility predicate.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Cross-Module Tombstone Disqualification

/// Unsupported closure shape (async-throwing closure returning a Swift class is not
/// the baseline async bridge shape) paired with a dependency-module type in a
/// rendered PARAMETER slot. Tombstone-eligible on the closure, disqualified on the
/// unreachable-module parameter.
public final class CrossModuleClosureTombstoneHost {
    public init() {}

    /// Rendered slot: the `origin` parameter names a dependency-module struct.
    public func configure(origin: DependencyPoint,
                          factory: @escaping () async throws -> AsyncFactoryPayload) {
        _ = origin
        _ = factory
    }

    /// Rendered slot: the RETURN type names a dependency-module struct.
    public func resolve(factory: @escaping () async throws -> AsyncFactoryPayload) -> DependencyPoint {
        _ = factory
        return DependencyPoint(x: 0, y: 0)
    }

    /// Rendered slot nested inside a generic: the dependency-module struct is reached
    /// only through an array element, so a shallow name check would miss it.
    public func collect(points: [DependencyPoint],
                        factory: @escaping () async throws -> AsyncFactoryPayload) {
        _ = points
        _ = factory
    }

    /// Control: same unsupported closure, but every rendered slot stays inside the
    /// emitting module (and the standard library). This one MUST keep its tombstone —
    /// the disqualification is about reachability, not about closures.
    public func describe(label: String,
                         factory: @escaping () async throws -> AsyncFactoryPayload) -> Int32 {
        _ = label
        _ = factory
        return 0
    }
}
