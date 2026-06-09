// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Throwing Initializer Error

/// Error type for ValidatedConfig.
public enum ConfigError: Error {
    case invalidTimeout
    case emptyName
}

// MARK: - Throwing Initializer

/// Struct with a throwing initializer.
public struct ValidatedConfig {
    public let name: String
    public let timeout: Int32

    /// Throwing init: throws if timeout is negative or name is empty.
    public init(name: String, timeout: Int32) throws {
        guard !name.isEmpty else {
            throw ConfigError.emptyName
        }
        guard timeout >= 0 else {
            throw ConfigError.invalidTimeout
        }
        self.name = name
        self.timeout = timeout
    }
}

// MARK: - Throwing Class Constructor: error-out leads, value args shift

/// Error for the throwing class constructor below.
public enum BoundsError: Error {
    case invalidRange
}

/// A class whose throwing constructor takes two value arguments. This is the one thunked shape whose
/// swifterror-out pointer LEADS the value arguments on the cdecl side: the error-out lands in the first
/// integer register and `lo`/`hi` shift up one register, so the thunk must capture the error-out from
/// the leading register and shift the value arguments back down for swiftcc. The earlier bug read the
/// error pointer as the first value argument (and never captured swifterror), corrupting `lo`/`hi` on
/// the success path and dropping the thrown error on the failure path. Exercised by
/// `ThrowingClassConstructorTests`.
public class RangeBox {
    public let lo: Int32
    public let hi: Int32

    /// Throwing init with two value args: throws when `lo > hi`, else stores both.
    public init(lo: Int32, hi: Int32) throws {
        guard lo <= hi else {
            throw BoundsError.invalidRange
        }
        self.lo = lo
        self.hi = hi
    }

    /// Returns the span — confirms both value arguments survived the error-leads register shift.
    public func span() -> Int32 {
        return hi &- lo
    }
}
