// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Operators with a one-word Optional value result, and binary and prefix forms of one symbol

/// A class whose operators return a one-word Optional value (`Int32?`, `Bool?`) and that declares
/// both the binary and the prefix form of `-` and `+`. The Optional result comes back from Swift in
/// its payload-and-tag form, so it has to be read into the projected `int?` / `bool?` rather than
/// returned as is. The binary and prefix forms share an operator symbol, so each needs its own
/// entry point the call reader can tell apart from the other's.
public final class OptionalValueOperatorClass {
    public let value: Int32

    public init(_ value: Int32) {
        self.value = value
    }

    /// The sum, or nil when it is zero.
    public static func + (lhs: OptionalValueOperatorClass, rhs: OptionalValueOperatorClass) -> Int32? {
        let sum = lhs.value + rhs.value
        return sum == 0 ? nil : sum
    }

    /// Whether exactly one of the two values is negative, or nil when both are zero.
    public static func ^ (lhs: OptionalValueOperatorClass, rhs: OptionalValueOperatorClass) -> Bool? {
        lhs.value == 0 && rhs.value == 0 ? nil : (lhs.value < 0) != (rhs.value < 0)
    }

    /// The difference.
    public static func - (lhs: OptionalValueOperatorClass, rhs: OptionalValueOperatorClass) -> Int32 {
        lhs.value - rhs.value
    }

    /// The negated value; throws for zero, which has no sign to flip.
    public static prefix func - (operand: OptionalValueOperatorClass) throws -> Int32 {
        guard operand.value != 0 else { throw OptionalValueOperatorError.zero }
        return -operand.value
    }

    /// The value doubled: a prefix form whose symbol the binary `+` above also uses.
    public static prefix func + (operand: OptionalValueOperatorClass) -> Int32 {
        operand.value * 2
    }
}

/// The struct counterpart of `OptionalValueOperatorClass`.
public struct OptionalValueOperatorValue {
    public let value: Int32

    public init(_ value: Int32) {
        self.value = value
    }

    /// The sum, or nil when it is zero.
    public static func + (lhs: OptionalValueOperatorValue, rhs: OptionalValueOperatorValue) -> Int32? {
        let sum = lhs.value + rhs.value
        return sum == 0 ? nil : sum
    }

    /// The difference.
    public static func - (lhs: OptionalValueOperatorValue, rhs: OptionalValueOperatorValue) -> Int32 {
        lhs.value - rhs.value
    }

    /// The negated value.
    public static prefix func - (operand: OptionalValueOperatorValue) -> Int32 {
        -operand.value
    }
}

public enum OptionalValueOperatorError: Error {
    case zero
}
