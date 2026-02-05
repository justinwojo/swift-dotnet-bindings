// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Functions Returning Closures

/// Returns an adder closure.
public func makeAdder(_ base: Int32) -> (Int32) -> Int32 {
    return { x in base + x }
}

/// Returns a multiplier closure.
public func makeMultiplier(_ factor: Int32) -> (Int32) -> Int32 {
    return { x in factor * x }
}

/// Returns a predicate closure.
public func makeGreaterThan(_ threshold: Int32) -> (Int32) -> Bool {
    return { x in x > threshold }
}

// MARK: - Struct Returning Closures

/// A frozen struct with methods that return closures.
@frozen
public struct ClosureFactory {
    public let base: Int32

    public init(base: Int32) {
        self.base = base
    }

    /// Instance method returning a closure.
    public func makeTransform() -> (Int32) -> Int32 {
        return { x in self.base + x }
    }

    /// Static method returning a closure.
    public static func makeScaler(_ scale: Int32) -> (Int32) -> Int32 {
        return { x in x * scale }
    }
}
