// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Actor Types
// Tests: Actor type declaration, isolated state, actor methods, nonisolated methods
// Expected C#: Class-like emission with actor isolation semantics
// Limitation: Actors are not yet supported by the generator

/// A simple actor with isolated mutable state.
public actor Counter {
    private var count: Int32 = 0

    public init() {}

    public init(initialCount: Int32) {
        self.count = initialCount
    }

    /// Isolated method — accesses actor state.
    public func increment() -> Int32 {
        count += 1
        return count
    }

    /// Isolated method — accesses actor state.
    public func decrement() -> Int32 {
        count -= 1
        return count
    }

    /// Isolated method — reads actor state.
    public func getCount() -> Int32 {
        return count
    }

    /// Isolated method with parameters.
    public func add(_ amount: Int32) -> Int32 {
        count += amount
        return count
    }

    /// Nonisolated method — does not access actor state.
    nonisolated public func description() -> String {
        return "Counter actor"
    }

    /// Nonisolated computed property.
    nonisolated public var typeName: String {
        return "Counter"
    }
}

// MARK: - Actor with Async Methods

/// An actor that performs async work with isolated state.
public actor AsyncProcessor {
    private var results: [String] = []

    public init() {}

    /// Async isolated method.
    public func process(input: String) async -> String {
        try? await Task.sleep(nanoseconds: 1_000_000)
        let result = "Processed: \(input)"
        results.append(result)
        return result
    }

    /// Returns the count of processed results.
    public func resultCount() -> Int32 {
        return Int32(results.count)
    }

    /// Nonisolated async method.
    nonisolated public func computeHash(for input: String) async -> Int32 {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return Int32(input.hashValue & 0x7FFFFFFF)
    }
}

// MARK: - Free Functions

/// Creates a Counter actor.
public func createCounter() -> Counter {
    return Counter()
}

/// Creates a Counter actor with an initial count.
public func createCounterWithInitial(_ initial: Int32) -> Counter {
    return Counter(initialCount: initial)
}

/// Creates an AsyncProcessor actor.
public func createAsyncProcessor() -> AsyncProcessor {
    return AsyncProcessor()
}
