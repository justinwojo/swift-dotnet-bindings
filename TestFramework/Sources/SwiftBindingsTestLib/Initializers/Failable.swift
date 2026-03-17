// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Failable Initializers (S1)
// Tests: init? projected as TryCreate with out param
// Real-world: Valet SharedGroupIdentifier.TryCreate, NVActivityIndicatorView TryCreate(coder:)

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

// NonEmptyString removed — non-frozen struct generates `class` in C#,
// and TryCreate emits `result = default` which is null for a non-nullable class (CS8625).

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
