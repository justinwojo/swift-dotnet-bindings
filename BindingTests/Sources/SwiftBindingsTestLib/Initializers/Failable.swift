// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Failable Initializers (S1)
// Tests: init? projected as TryCreate with out param

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

/// Non-frozen struct with a failable initializer.
/// Tests TryCreate with `result = default!` for class-projected structs (CS8625 fix).
public struct NonEmptyString {
    public let value: String
    public let length: Int32

    /// Failable init: returns nil if the string is empty.
    public init?(_ string: String) {
        guard !string.isEmpty else { return nil }
        self.value = string
        self.length = Int32(string.count)
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
