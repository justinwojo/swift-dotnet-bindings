// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Static Properties

/// Struct with static var and let properties.
public struct StaticProps {
    /// Static constant.
    public static let defaultValue: Int32 = 42

    /// Static mutable variable.
    public static var sharedCounter: Int32 = 0

    /// Static computed property.
    public static var counterDescription: String {
        return "Counter: \(sharedCounter)"
    }

    public init() {}
}

// MARK: - Static Methods (Getter/Setter pattern)

/// Struct with static getter/setter methods (alternative to properties).
public struct StaticMethods {
    private static var _storedValue: Int32 = 0

    public init() {}

    /// Static getter method.
    public static func getStoredValue() -> Int32 {
        return _storedValue
    }

    /// Static setter method.
    public static func setStoredValue(_ value: Int32) {
        _storedValue = value
    }

    /// Static method that increments and returns.
    public static func incrementAndGet() -> Int32 {
        _storedValue += 1
        return _storedValue
    }
}
