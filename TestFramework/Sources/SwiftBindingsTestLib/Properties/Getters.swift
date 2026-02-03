// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Read-Only Properties (Frozen)

/// Frozen struct with read-only stored and computed properties.
@frozen
public struct ReadOnlyProps {
    public let storedInt: Int32
    public let storedDouble: Double
    public let storedString: String

    public init(storedInt: Int32, storedDouble: Double, storedString: String) {
        self.storedInt = storedInt
        self.storedDouble = storedDouble
        self.storedString = storedString
    }

    /// Computed read-only property.
    public var summary: String {
        return "\(storedString): \(storedInt), \(storedDouble)"
    }

    /// Static let property.
    public static let version: Int32 = 1
}

// MARK: - Read-Only Properties (Non-Frozen)

/// Non-frozen struct with read-only properties.
public struct NonFrozenReadOnlyProps {
    public let storedInt: Int32
    public let storedString: String

    public init(storedInt: Int32, storedString: String) {
        self.storedInt = storedInt
        self.storedString = storedString
    }

    public var uppercased: String {
        return storedString.uppercased()
    }
}
