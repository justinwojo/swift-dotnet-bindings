// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Struct with Tuple-Returning Methods

/// A frozen struct with methods that return tuples.
@frozen
public struct TupleReturner {
    public let x: Int32
    public let y: Int32

    public init(x: Int32, y: Int32) {
        self.x = x
        self.y = y
    }

    /// Instance method returning a tuple.
    public func asTuple() -> (Int32, Int32) {
        return (x, y)
    }

    /// Instance method returning a named tuple.
    public func asNamedTuple() -> (x: Int32, y: Int32) {
        return (x: x, y: y)
    }

    /// Static method returning a tuple.
    public static func makePair(a: Int32, b: Int32) -> (Int32, Int32) {
        return (a, b)
    }
}

// MARK: - Division with Remainder

/// Returns quotient and remainder as a tuple.
public func divmod(a: Int32, b: Int32) -> (quotient: Int32, remainder: Int32) {
    return (quotient: a / b, remainder: a % b)
}

/// Returns min and max of two values.
public func minmax(_ a: Int32, _ b: Int32) -> (min: Int32, max: Int32) {
    if a <= b {
        return (min: a, max: b)
    } else {
        return (min: b, max: a)
    }
}
