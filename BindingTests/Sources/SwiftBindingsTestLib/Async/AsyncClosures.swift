// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async Closure Parameters
// Tests: Functions accepting async closures, calling with await
// Expected C#: Func<Task<T>> or similar async delegate pattern
// Most shapes are still guarded — Session A only ungates the baseline
// `@escaping () async throws -> T` shape where T is a BitwiseCopyable primitive
// and the outer method is `async throws`.

/// Baseline async-throwing closure shape supported by Session A:
/// no args, BitwiseCopyable primitive return, outer method is `async throws`.
public func callAsyncThrowingClosure(_ closure: @escaping () async throws -> Int32) async throws -> Int32 {
    return try await closure()
}

#if swift(>=99.0)

/// Accepts an async closure and awaits its result.
public func callAsyncClosure(_ closure: @escaping () async -> Int32) async -> Int32 {
    return await closure()
}

/// Accepts an async closure returning String.
public func callAsyncStringClosure(_ closure: @escaping () async -> String) async -> String {
    return await closure()
}

/// Accepts an async closure with a parameter.
public func callAsyncClosureWithParam(_ value: Int32, closure: @escaping (Int32) async -> Int32) async -> Int32 {
    return await closure(value)
}

/// Accepts an async closure returning void.
public func callAsyncVoidClosure(_ closure: @escaping () async -> Void) async {
    await closure()
}

// MARK: - Struct with Async Closure Methods

/// A struct with methods that accept async closures.
public struct AsyncClosureConsumer {
    public let label: String

    public init(label: String) {
        self.label = label
    }

    /// Instance method accepting an async closure.
    public func transform(value: Int32, using closure: @escaping (Int32) async -> Int32) async -> Int32 {
        return await closure(value)
    }

    /// Static method accepting an async closure.
    public static func processAsync(input: String, closure: @escaping (String) async -> String) async -> String {
        return await closure(input)
    }
}

#endif
