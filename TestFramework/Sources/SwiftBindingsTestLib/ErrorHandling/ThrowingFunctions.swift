// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Throwing Functions (Tier 1)

/// Divides two Int32 values; throws on division by zero.
public func divide(a: Int32, b: Int32) throws -> Int32 {
    guard b != 0 else {
        throw MathError.divisionByZero
    }
    return a / b
}

/// Validates a string is non-empty and under a max length.
public func validate(_ input: String, maxLength: Int32 = 100) throws -> String {
    guard !input.isEmpty else {
        throw ValidationError.empty
    }
    guard input.count <= Int(maxLength) else {
        throw ValidationError.tooLong(maxLength: maxLength)
    }
    return input
}

// MARK: - Struct with Throwing Methods

/// Struct with instance and static throwing methods.
public struct ThrowingStruct {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    /// Instance throwing method.
    public func divideBy(_ divisor: Int32) throws -> Int32 {
        guard divisor != 0 else {
            throw MathError.divisionByZero
        }
        return value / divisor
    }

    /// Instance throwing method with validation.
    public func validatePositive() throws -> Int32 {
        guard value > 0 else {
            throw MathError.negativeInput
        }
        return value
    }

    /// Static throwing method.
    public static func safeDivide(_ a: Int32, _ b: Int32) throws -> Int32 {
        guard b != 0 else {
            throw MathError.divisionByZero
        }
        return a / b
    }
}
