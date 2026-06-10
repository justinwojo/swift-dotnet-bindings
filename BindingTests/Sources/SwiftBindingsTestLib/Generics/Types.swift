// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Generic Structs

/// A generic wrapper struct.
/// Note: not @frozen because the binding generator cannot resolve generic type
/// parameter layouts (τ_0_0) when calculating frozen struct flags.
public struct Wrapper<T> {
    public let wrapped: T

    public init(_ wrapped: T) {
        self.wrapped = wrapped
    }

    /// Returns the wrapped value.
    public func unwrap() -> T {
        return wrapped
    }
}

/// A generic pair struct with two type parameters.
/// Note: not @frozen — see Wrapper<T> note above.
public struct GenericPair<T, U> {
    public let first: T
    public let second: U

    public init(first: T, second: U) {
        self.first = first
        self.second = second
    }

    #if swift(>=99.0)
    /// Swaps the pair elements.
    /// Guarded: generates CS8500 (pointer to managed generic type with swapped type params).
    public func swapped() -> GenericPair<U, T> {
        return GenericPair<U, T>(first: second, second: first)
    }
    #endif
}

// MARK: - Bound Generic Types (concrete specializations)

/// Concrete bound generic: GenericPair specialized to (Int32, Int32).
@frozen
public struct BoundIntPair {
    public let first: Int32
    public let second: Int32

    public init(first: Int32, second: Int32) {
        self.first = first
        self.second = second
    }

    public func sum() -> Int32 {
        return first + second
    }
}

/// Concrete bound generic: GenericPair specialized to (String, String).
public struct BoundStringPair {
    public let first: String
    public let second: String

    public init(first: String, second: String) {
        self.first = first
        self.second = second
    }

    public func joined() -> String {
        return "\(first) \(second)"
    }
}

// MARK: - Generic Class

/// A generic class with stored property and methods.
public class GenericClass<T> {
    public var value: T

    public init(value: T) {
        self.value = value
    }

    /// Returns the stored value.
    public func get() -> T {
        return value
    }

    /// Replaces the stored value.
    public func set(_ newValue: T) {
        value = newValue
    }
}

// MARK: - Generic Class Implementing Protocol (Quick AsyncBehavior pattern)

/// Generic class implementing the Named protocol with a String property.
/// Tests SBW_Free routing to PInvokeHelper for generic types (CS7042 fix).
public class GenericNamedBox<T>: Named {
    public let value: T
    public let name: String

    public init(value: T, name: String) {
        self.value = value
        self.name = name
    }
}

// MARK: - Generic Struct with Optional Generic Property (Quick TestState pattern)

/// Generic struct whose main property is Optional<T>.
/// Tests SwiftOptional<T> with generic metadata.
public struct OptionalWrapper<T> {
    public var value: T?

    public init(value: T?) {
        self.value = value
    }

    public var hasValue: Bool { value != nil }
}

// MARK: - Q2: Generic Class Inheriting Non-Generic Class

/// Non-generic base class for entity hierarchy.
public class BaseEntity {
    public let entityId: Int32

    public init(entityId: Int32) {
        self.entityId = entityId
    }

    public func getEntityId() -> Int32 {
        return entityId
    }
}

/// Generic class inheriting from a non-generic base class.
/// Tests SBW_Free routing + Payload `new` modifier for generic class inheritance.
public class TypedEntity<T>: BaseEntity {
    public let content: T

    public init(entityId: Int32, content: T) {
        self.content = content
        super.init(entityId: entityId)
    }
}
