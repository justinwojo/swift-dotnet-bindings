// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocols whose existentials another module vends
//
// The main test library returns and takes `(any P)?` of each of these
// (SwiftBindingsTestLib/CrossModule/CrossModuleOptionalExistentials.swift). The existential's size
// is decided by the protocol, which lives here: a class-bound Swift protocol's existential is a
// reference and one witness table, an @objc protocol's is one object pointer, and any other
// protocol's is an address-only box. An SDK consumer of the main library's binding knows these
// protocols only from the module database this module's binding publishes.

public protocol DependencyVendedBound: AnyObject {
    func vendedValue() -> Int32
}

@objc public protocol DependencyVendedObjC {
    func vendedValue() -> Int32
}

/// Class-bound through the protocol it refines, which its own declaration does not say.
public protocol DependencyVendedRefined: DependencyVendedBound {
    func vendedExtra() -> Int32
}

public protocol DependencyVendedOpaque {
    func vendedValue() -> Int32
}

/// Conforms to every Swift protocol above, so one instance answers each existential.
public final class DependencyVendedThing: DependencyVendedRefined, DependencyVendedOpaque {
    public let value: Int32

    public init(value: Int32) { self.value = value }

    public func vendedValue() -> Int32 { value }
    public func vendedExtra() -> Int32 { value + 1000 }
}

public final class DependencyVendedObjCThing: NSObject, DependencyVendedObjC {
    public let value: Int32

    public init(value: Int32) { self.value = value }

    public func vendedValue() -> Int32 { value }
}
