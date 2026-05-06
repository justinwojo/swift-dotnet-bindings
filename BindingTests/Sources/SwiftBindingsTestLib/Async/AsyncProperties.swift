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

// MARK: - X1: AsyncStream Property (Nuke ImageTask pattern)
// Generator has AsyncStreamEmitter.cs, runtime has SwiftAsyncStream.cs — both untested.

/// Class with AsyncStream computed properties.
/// Tests AsyncStream with both String (ISwiftObject) and Int32 (primitive) element types.
public class AsyncValueSource {
    public init() {}

    public var messages: AsyncStream<String> {
        AsyncStream { continuation in
            continuation.yield("first")
            continuation.yield("second")
            continuation.yield("third")
            continuation.finish()
        }
    }

    /// AsyncStream with primitive Int32 elements.
    /// Tests that SwiftAsyncStream<T> constraint was relaxed from ISwiftObject.
    public var counts: AsyncStream<Int32> {
        AsyncStream { continuation in
            continuation.yield(10)
            continuation.yield(20)
            continuation.yield(30)
            continuation.finish()
        }
    }

    /// AsyncStream whose element type is a Swift array. Regression coverage for
    /// `gap-0.10.0-swiftarray-at-api-boundary.md` (Bundle 04 #8): pre-fix the property
    /// surfaced as `IAsyncEnumerable<Swift.SwiftArray<Int32>>`, leaking the runtime
    /// helper type at the public API boundary. Post-fix the property surfaces as
    /// `IAsyncEnumerable<IReadOnlyList<Int32>>` while the channel still stores
    /// `SwiftArray<Int32>` internally — covariance (`IAsyncEnumerable<out T>` plus
    /// `SwiftArray<T> : IReadOnlyList<T>`) closes the loop. Mirrors BlinkIDUX's
    /// `BlinkIDEventStream.stream: AsyncStream<[UIEvent]>` discovery case.
    public var batches: AsyncStream<[Int32]> {
        AsyncStream { continuation in
            continuation.yield([1, 2, 3])
            continuation.yield([4, 5])
            continuation.yield([6])
            continuation.finish()
        }
    }
}
