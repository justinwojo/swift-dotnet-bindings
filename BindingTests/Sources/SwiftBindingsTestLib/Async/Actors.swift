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

// MARK: - Actor with Throwing Isolated Method

public enum ActorVaultError: Error {
    case keyMissing
}

/// An actor whose isolated method throws — exercises `try await self.method()` in the wrapper.
public actor ActorVault {
    private var secrets: [String: String] = [:]

    public init() {}

    /// Isolated non-throwing writer.
    public func store(key: String, value: String) {
        secrets[key] = value
    }

    /// Isolated throwing reader — exercises try/await on an actor.
    public func reveal(key: String) throws -> String {
        guard let v = secrets[key] else { throw ActorVaultError.keyMissing }
        return v
    }
}

// MARK: - Actor with Isolated AsyncStream Property
// Non-async stored property on an `actor` that returns AsyncStream<T>.
// The @_cdecl wrapper must hop to the actor's serial executor
// via `await __self.events` from inside a Task.

public actor ActorEventStream {
    public let events: AsyncStream<Int32>
    private let continuation: AsyncStream<Int32>.Continuation

    public init() {
        var c: AsyncStream<Int32>.Continuation!
        self.events = AsyncStream<Int32> { continuation in
            c = continuation
        }
        self.continuation = c
    }

    public func emit(_ value: Int32) {
        continuation.yield(value)
    }

    public func end() {
        continuation.finish()
    }

    /// Nonisolated companion — access does not need `await`; regression for the
    /// `IsNonisolated` sub-branch of the emitter.
    nonisolated public var passthroughLabel: String {
        return "ActorEventStream"
    }
}

/// Creates an ActorEventStream actor.
public func createActorEventStream() -> ActorEventStream {
    return ActorEventStream()
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

/// Creates an ActorVault actor.
public func createActorVault() -> ActorVault {
    return ActorVault()
}

// MARK: - Actor with Already-Async Throwing Methods
// Actor methods declared `async throws` at the Swift source level (not sync-normalized
// by the parser). Exercises the async-throwing wrapper path on an actor receiver.
//
// Scope note: this fixture is a shell-stub to surface the API shape. Executor semantics
// (unownedExecutor / actor-isolation hop on entry) are deferred to a post-release follow-up
// — the @_cdecl wrapper today dispatches through a plain `Task { await self.method() }`
// instead of hopping to the actor's serial executor. Do not treat this as a full actor
// isolation implementation.

public enum WorkItemError: Error {
    case cancelled
}

public actor WorkItem {
    private var runs: Int32 = 0
    private var stopped: Bool = false

    public init() {}

    /// Already-async throwing isolated method — matches CaptureService.start().
    public func run() async throws -> Int32 {
        if stopped { throw WorkItemError.cancelled }
        runs += 1
        return runs
    }

    /// Already-async non-throwing isolated method — matches CaptureService.stop().
    public func stop() async {
        stopped = true
    }

    /// Isolated getter for the current run count.
    public func runCount() -> Int32 {
        return runs
    }
}

public func createWorkItem() -> WorkItem {
    return WorkItem()
}
