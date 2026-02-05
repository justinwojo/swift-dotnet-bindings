// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Arithmetic Operators

/// Frozen struct for testing arithmetic operator emission.
@frozen
public struct ArithmeticValue {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public static func + (lhs: ArithmeticValue, rhs: ArithmeticValue) -> ArithmeticValue {
        return ArithmeticValue(value: lhs.value + rhs.value)
    }

    public static func - (lhs: ArithmeticValue, rhs: ArithmeticValue) -> ArithmeticValue {
        return ArithmeticValue(value: lhs.value - rhs.value)
    }

    public static func * (lhs: ArithmeticValue, rhs: ArithmeticValue) -> ArithmeticValue {
        return ArithmeticValue(value: lhs.value * rhs.value)
    }

    public static func / (lhs: ArithmeticValue, rhs: ArithmeticValue) -> ArithmeticValue {
        return ArithmeticValue(value: lhs.value / rhs.value)
    }

    public static func % (lhs: ArithmeticValue, rhs: ArithmeticValue) -> ArithmeticValue {
        return ArithmeticValue(value: lhs.value % rhs.value)
    }
}
