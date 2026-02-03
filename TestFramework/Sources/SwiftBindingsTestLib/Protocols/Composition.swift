// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocols for Composition

/// Simple protocol with a name.
public protocol Nameable {
    var name: String { get }
}

/// Simple protocol with an age.
public protocol Ageable {
    var age: Int32 { get }
}

// MARK: - Protocol Composition Functions

/// Free function accepting a protocol composition type.
public func describeEntity(_ entity: Nameable & Ageable) -> String {
    return "\(entity.name), age \(entity.age)"
}

/// Free function accepting an existential (any protocol).
public func processDescribable(_ item: any Describable) -> String {
    return item.describe()
}

/// Free function accepting a composition existential.
public func processNameableAgeable(_ item: any Nameable & Ageable) -> String {
    return "\(item.name) is \(item.age)"
}
