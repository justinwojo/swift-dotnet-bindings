// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Isolation Control (Swift 6.1/6.2)
// Tests: nonisolated(unsafe) property, isolation control attributes
// Expected C#: Isolation attributes may appear in ABI JSON as function/property annotations
// Limitation: Isolation control features are not yet supported by the generator

/// A class with shared mutable state using `nonisolated(unsafe)`.
///
/// `nonisolated(unsafe)` allows a stored property to be accessed from
/// any isolation domain without compiler enforcement. This is an ABI-visible
/// annotation that the binding generator may encounter.
public class SharedState {
    /// A nonisolated(unsafe) property — accessible from any context without isolation.
    nonisolated(unsafe) public var counter: Int32 = 0

    /// A regular stored property for comparison.
    public var label: String

    public init(label: String) {
        self.label = label
    }

    /// Reads the counter value.
    public func getCounter() -> Int32 {
        return counter
    }

    /// Increments the counter.
    public func incrementCounter() {
        counter += 1
    }
}

// MARK: - Free Functions (Creation Helpers)

/// Creates a SharedState instance.
public func createSharedState(label: String) -> SharedState {
    return SharedState(label: label)
}
