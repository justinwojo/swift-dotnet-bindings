// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Arrays of another module's class-bound existentials
//
// Each member returns `[any DependencyVendedBound]`, a protocol SwiftBindingsTestLibDependency
// declares (VendedExistentialProtocols.swift). A class-bound existential is a reference and one
// witness table, so the array is read at a 16-byte stride through the shared class-existential
// carrier. That carrier's metadata must resolve however the process got here: nothing in this
// module has to have run the dependency's module initializer first. The builder's per-conformer
// specializations return the same shape through their own wrappers.

public final class CrossModuleBoundRoster {
    public let base: Int32

    public init(base: Int32) { self.base = base }

    /// `count` vended things, valued `base`, `base + 1`, ...
    public func members(_ count: Int32) -> [any DependencyVendedBound] {
        (0..<max(count, 0)).map { DependencyVendedThing(value: base + $0) }
    }

    /// The sum of the values of an array handed back in.
    public func total(_ members: [any DependencyVendedBound]) -> Int32 {
        members.reduce(0) { $0 + $1.vendedValue() }
    }
}

/// A free function returning the cross-module class-bound array.
public func crossModuleBoundMembers(base: Int32, count: Int32) -> [any DependencyVendedBound] {
    (0..<max(count, 0)).map { DependencyVendedThing(value: base + $0) }
}

/// A static generic builder over this module's `LedgerOption`, bound once per conformer: each
/// specialization returns the other module's class-bound existentials.
public struct CrossModuleBoundBuilder {
    public static func vend(_ option: some LedgerOption) -> [any DependencyVendedBound] {
        [DependencyVendedThing(value: option.optionCode), DependencyVendedThing(value: option.optionCode + 1)]
    }
}
