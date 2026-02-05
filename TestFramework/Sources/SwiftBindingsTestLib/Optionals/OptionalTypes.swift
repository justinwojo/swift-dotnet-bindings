// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional Blittable Return

/// Finds the index of a value in the array, or nil if not found.
public func findIndex(_ array: [Int32], value: Int32) -> Int32? {
    if let idx = array.firstIndex(of: value) {
        return Int32(idx)
    }
    return nil
}

// MARK: - Optional Class Return

/// Finds an animal by name from the array, or nil if not found.
public func findAnimalByName(_ animals: [Animal], name: String) -> Animal? {
    return animals.first { $0.name == name }
}

// MARK: - Optional Parameter

/// Describes an optional Int32, returning "nil" or the string value.
public func describeOptionalInt(_ value: Int32?) -> String {
    if let v = value {
        return "Value: \(v)"
    }
    return "nil"
}

// MARK: - Struct with Optional Properties

/// A frozen struct with optional properties for testing optional field emission.
@frozen
public struct OptionalConfig {
    public var label: String?
    public var count: Int32?
    public var fallbackLabel: String

    public init(label: String?, count: Int32?, fallbackLabel: String) {
        self.label = label
        self.count = count
        self.fallbackLabel = fallbackLabel
    }

    /// Returns the effective label (label or fallbackLabel).
    public func effectiveLabel() -> String {
        return label ?? fallbackLabel
    }
}
