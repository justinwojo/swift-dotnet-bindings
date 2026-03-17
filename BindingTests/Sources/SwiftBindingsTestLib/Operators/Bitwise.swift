// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bitwise Operators

/// Frozen struct for testing bitwise operator emission.
@frozen
public struct BitwiseValue {
    public let value: UInt32

    public init(value: UInt32) {
        self.value = value
    }

    public static func & (lhs: BitwiseValue, rhs: BitwiseValue) -> BitwiseValue {
        return BitwiseValue(value: lhs.value & rhs.value)
    }

    public static func | (lhs: BitwiseValue, rhs: BitwiseValue) -> BitwiseValue {
        return BitwiseValue(value: lhs.value | rhs.value)
    }

    public static func ^ (lhs: BitwiseValue, rhs: BitwiseValue) -> BitwiseValue {
        return BitwiseValue(value: lhs.value ^ rhs.value)
    }

    public static func << (lhs: BitwiseValue, rhs: BitwiseValue) -> BitwiseValue {
        return BitwiseValue(value: lhs.value << rhs.value)
    }

    public static func >> (lhs: BitwiseValue, rhs: BitwiseValue) -> BitwiseValue {
        return BitwiseValue(value: lhs.value >> rhs.value)
    }
}
