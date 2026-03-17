// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Variadic Int32

/// Sums variadic Int32 arguments.
/// Tests: Variadic parameter with Int32.
/// Expected C#: params int[] or similar array parameter.
public func sumAll(_ values: Int32...) -> Int32 {
    return values.reduce(0, +)
}

/// Returns the count of variadic Int32 arguments.
public func countValues(_ values: Int32...) -> Int32 {
    return Int32(values.count)
}

/// Returns the minimum of variadic Int32 arguments, or nil if empty.
public func minValue(_ values: Int32...) -> Int32? {
    return values.min()
}

// MARK: - Variadic String

/// Joins variadic String arguments with a separator.
/// Tests: Variadic parameter with String type.
/// Expected C#: params string[] or similar.
public func joinStrings(_ strings: String...) -> String {
    return strings.joined(separator: " ")
}

/// Joins variadic String arguments with a custom separator.
public func joinStringsWithSeparator(separator: String, _ strings: String...) -> String {
    return strings.joined(separator: separator)
}

// MARK: - Variadic with Other Parameters

/// Formats a label with variadic Int32 values.
/// Tests: Variadic parameter combined with non-variadic parameters.
public func formatWithLabel(label: String, values: Int32...) -> String {
    let valueStrings = values.map { String($0) }.joined(separator: ", ")
    return "\(label): [\(valueStrings)]"
}

/// Returns the average of variadic Double values, with a default value if empty.
public func averageOrDefault(defaultValue: Double, _ values: Double...) -> Double {
    guard !values.isEmpty else { return defaultValue }
    return values.reduce(0.0, +) / Double(values.count)
}

// MARK: - Struct with Variadic Method

/// A frozen struct with methods accepting variadic parameters.
@frozen
public struct VariadicConsumer {
    public let prefix: String

    public init(prefix: String) {
        self.prefix = prefix
    }

    /// Instance method with variadic Int32.
    public func sumWithPrefix(_ values: Int32...) -> String {
        let total = values.reduce(0 as Int32, +)
        return "\(prefix)\(total)"
    }

    /// Static method with variadic String.
    public static func combine(_ parts: String...) -> String {
        return parts.joined()
    }
}
