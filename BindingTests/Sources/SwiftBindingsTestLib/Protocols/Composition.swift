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

// MARK: - Multi-Protocol Conformance (4+ protocols)

/// Protocol for types that support addition.
public protocol Addable {
    func add(_ other: Int32) -> Int32
}

/// Protocol for types that support subtraction.
public protocol Subtractable {
    func subtract(_ other: Int32) -> Int32
}

/// Protocol for types that support multiplication.
public protocol Multipliable {
    func multiply(_ other: Int32) -> Int32
}

/// Protocol for types that support division.
public protocol Dividable {
    func divide(_ other: Int32) -> Int32
}

/// Frozen struct conforming to all four arithmetic protocols.
@frozen
public struct MultiConformingValue: Addable, Subtractable, Multipliable, Dividable {
    public var value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func add(_ other: Int32) -> Int32 {
        return value + other
    }

    public func subtract(_ other: Int32) -> Int32 {
        return value - other
    }

    public func multiply(_ other: Int32) -> Int32 {
        return value * other
    }

    public func divide(_ other: Int32) -> Int32 {
        guard other != 0 else { return 0 }
        return value / other
    }
}

/// Generic function with a compound 3-protocol constraint.
public func applyThreeProtocols<T: Addable & Subtractable & Multipliable>(_ val: T, a: Int32, b: Int32, c: Int32) -> Int32 {
    let sum = val.add(a)
    let diff = val.subtract(b)
    let prod = val.multiply(c)
    return sum + diff + prod
}

// MARK: - Factory Functions for Composition Testing

/// Creates a Person and returns it as `any Nameable & Ageable` (EC2 existential).
/// Enables C# tests to call processNameableAgeable without manual EC2 boxing.
public func makeNameableAgeable(name: String, age: Int32) -> any Nameable & Ageable {
    return Person(name: name, age: age)
}

/// Creates a Person and processes it through processNameableAgeable.
/// Tests the full EC2 round-trip: create Person → box as existential → process.
public func describePersonAsComposition(name: String, age: Int32) -> String {
    let person = Person(name: name, age: age)
    return processNameableAgeable(person)
}

/// Generic function with a compound 4-protocol constraint.
public func applyFourProtocols<T: Addable & Subtractable & Multipliable & Dividable>(_ val: T, a: Int32, b: Int32, c: Int32, d: Int32) -> Int32 {
    let sum = val.add(a)
    let diff = val.subtract(b)
    let prod = val.multiply(c)
    let quot = val.divide(d)
    return sum + diff + prod + quot
}
