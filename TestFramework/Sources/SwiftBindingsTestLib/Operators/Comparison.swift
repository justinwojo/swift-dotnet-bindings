// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Comparison Operators

/// Frozen struct for testing comparison operator emission.
@frozen
public struct ComparableValue: Equatable, Comparable {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public static func == (lhs: ComparableValue, rhs: ComparableValue) -> Bool {
        return lhs.value == rhs.value
    }

    public static func < (lhs: ComparableValue, rhs: ComparableValue) -> Bool {
        return lhs.value < rhs.value
    }

    // Note: >, <=, >= are automatically synthesized from == and < by the binding generator.
}
