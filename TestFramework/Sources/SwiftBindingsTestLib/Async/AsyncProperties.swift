// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async Properties
// Tests: Computed properties with async getter
// Expected C#: Async property accessor or async method wrapper
// Limitation: Async properties are not yet supported by the generator

/// Struct with async computed properties.
public struct AsyncConfig {
    public let name: String
    public let delay: UInt64

    public init(name: String, delay: UInt64 = 1_000_000) {
        self.name = name
        self.delay = delay
    }

    /// Async computed property returning a String.
    public var asyncLabel: String {
        get async {
            try? await Task.sleep(nanoseconds: delay)
            return "Config: \(name)"
        }
    }

    /// Async computed property returning Int32.
    public var asyncNameLength: Int32 {
        get async {
            try? await Task.sleep(nanoseconds: delay)
            return Int32(name.count)
        }
    }
}

// MARK: - Class with Async Properties

/// Class with async computed properties.
public class AsyncDataSource {
    public let identifier: String

    public init(identifier: String) {
        self.identifier = identifier
    }

    /// Async computed property on a class.
    public var asyncItemCount: Int32 {
        get async {
            try? await Task.sleep(nanoseconds: 1_000_000)
            return Int32(identifier.count * 2)
        }
    }

    /// Async computed property returning String.
    public var asyncSummary: String {
        get async {
            try? await Task.sleep(nanoseconds: 1_000_000)
            return "DataSource[\(identifier)]"
        }
    }
}

// MARK: - Free Functions

/// Creates an AsyncConfig instance.
public func createAsyncConfig(name: String) -> AsyncConfig {
    return AsyncConfig(name: name)
}

/// Creates an AsyncDataSource instance.
public func createAsyncDataSource(identifier: String) -> AsyncDataSource {
    return AsyncDataSource(identifier: identifier)
}
