// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Unary Operators

/// Frozen struct for testing unary operator emission.
@frozen
public struct UnaryValue {
    public let boolValue: Bool
    public let intValue: UInt32

    public init(boolValue: Bool, intValue: UInt32) {
        self.boolValue = boolValue
        self.intValue = intValue
    }

    /// Prefix logical NOT.
    public static prefix func ! (operand: UnaryValue) -> Bool {
        return !operand.boolValue
    }

    /// Prefix bitwise NOT.
    public static prefix func ~ (operand: UnaryValue) -> UnaryValue {
        return UnaryValue(boolValue: operand.boolValue, intValue: ~operand.intValue)
    }
}
