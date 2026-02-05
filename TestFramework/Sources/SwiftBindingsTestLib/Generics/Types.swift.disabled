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

    /// Swaps the pair elements.
    public func swapped() -> GenericPair<U, T> {
        return GenericPair<U, T>(first: second, second: first)
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
