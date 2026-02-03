// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Failable Initializers (Tier 2)

/// Struct with a failable initializer (division by zero guard).
@frozen
public struct SafeDiv {
    public let numerator: Int32
    public let denominator: Int32
    public let result: Double

    /// Failable init: returns nil if denominator is zero.
    public init?(numerator: Int32, denominator: Int32) {
        guard denominator != 0 else { return nil }
        self.numerator = numerator
        self.denominator = denominator
        self.result = Double(numerator) / Double(denominator)
    }
}

/// Struct wrapping a non-empty string.
public struct NonEmptyString {
    public let value: String

    /// Failable init: returns nil if the string is empty.
    public init?(_ string: String) {
        guard !string.isEmpty else { return nil }
        self.value = string
    }

    public var count: Int32 {
        return Int32(value.count)
    }
}

/// Struct with a range-validated initializer.
@frozen
public struct RangedInt {
    public let value: Int32
    public let min: Int32
    public let max: Int32

    /// Failable init: returns nil if value is outside [min, max].
    public init?(value: Int32, min: Int32, max: Int32) {
        guard value >= min && value <= max else { return nil }
        self.value = value
        self.min = min
        self.max = max
    }
}
