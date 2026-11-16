// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation
import SwiftBindingsTestLibDependency

// MARK: - Optional existentials of another module's protocols
//
// Each member returns or takes `(any P)?` of a protocol SwiftBindingsTestLibDependency declares
// (VendedExistentialProtocols.swift). swiftc returns the class-bound ones in two registers, the
// @objc one in one, and the opaque one through an indirect result of the 40-byte box, whose nil
// leaves the metadata word zero. A non-positive seed vends nil, so every member is exercised on
// both cases.

public final class CrossModuleExistentialVendor {
    public var seed: Int32

    public init(seed: Int32) { self.seed = seed }

    public func bound() -> (any DependencyVendedBound)? {
        seed > 0 ? DependencyVendedThing(value: seed) : nil
    }

    public func objc() -> (any DependencyVendedObjC)? {
        seed > 0 ? DependencyVendedObjCThing(value: seed) : nil
    }

    public func refined() -> (any DependencyVendedRefined)? {
        seed > 0 ? DependencyVendedThing(value: seed) : nil
    }

    public func opaque() -> (any DependencyVendedOpaque)? {
        seed > 0 ? DependencyVendedThing(value: seed) : nil
    }

    /// The value of a bound existential handed back in, or -1 for nil.
    public func valueOf(_ vended: (any DependencyVendedBound)?) -> Int32 {
        vended?.vendedValue() ?? -1
    }
}
