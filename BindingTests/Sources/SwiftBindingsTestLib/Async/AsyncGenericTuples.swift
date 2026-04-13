// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async Tuple Returns on Generic Types

/// A generic container with async methods returning tuples.
/// Tests Part A (async generic tuple pipeline) and Part B (async callback hoisting).
public class AsyncGenericContainer<T> {
    public var storedValue: T

    public init(value: T) {
        self.storedValue = value
    }

    /// Async method returning void — tests basic async callback hoisting in generic types.
    public func processAsync() async -> Int32 {
        return 42
    }

    /// Async method returning a tuple with a generic element and a concrete element.
    /// Tests GenericContext threading through the async tuple pipeline.
    public func fetchPair() async -> (T, Int32) {
        return (storedValue, 99)
    }

    /// Async throwing method on generic type — tests error callback hoisting.
    public func fetchOrThrow(shouldFail: Bool) async throws -> Int32 {
        if shouldFail {
            throw NSError(domain: "TestError", code: 1, userInfo: nil)
        }
        return 77
    }
}

// MARK: - Non-generic async tuple returns (regression)

/// Non-generic struct with async tuple returns — verifies the existing pipeline still works
/// after GenericContext threading changes.
public struct AsyncTupleWorker {
    public let label: String

    public init(label: String) {
        self.label = label
    }

    /// Async function returning a simple (Int32, Int32) tuple.
    public func fetchIntPair() async -> (Int32, Int32) {
        return (10, 20)
    }

    /// Async function returning a (String, Int32) tuple.
    public func fetchLabeledPair() async -> (String, Int32) {
        return (label, 42)
    }
}
