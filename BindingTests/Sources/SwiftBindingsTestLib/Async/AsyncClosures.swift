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

/// Invokes the same async-throwing closure twice within a single outer call.
/// Exercises the continuation box / adapter lifetime when a single closure
/// value is awaited multiple times — the adapter must build a fresh
/// CheckedContinuation + box on each invocation, not reuse stale state.
public func callAsyncThrowingClosureTwice(_ closure: @escaping () async throws -> Int32) async throws -> Int32 {
    let a = try await closure()
    let b = try await closure()
    return a &+ b
}

// MARK: - Session B: arg-bearing async-throwing closures
// Exercises the per-arity adapter that marshals closure args from Swift to C#
// synchronously before Task.Run spawns the managed async work.

/// Box used as the Swift class argument for async-throwing closures. Lets the
/// C# test assert the instance identity round-trips through the per-arity
/// bridge (Unmanaged.passUnretained → Arc.Retain → SwiftMarshal.MarshalFromSwift).
public final class AsyncClosureArgBox {
    public let tag: Int32
    public init(tag: Int32) { self.tag = tag }
}

/// Arity-1 primitive arg: `(Int32) async throws -> Int32`.
public func callAsyncThrowingClosureOneArg(
    _ value: Int32,
    _ closure: @escaping (Int32) async throws -> Int32
) async throws -> Int32 {
    return try await closure(value)
}

/// Arity-2 mixed: `(Int32, String) async throws -> Int32`. Confirms the String
/// arg survives the Swift→C# synchronous marshal (withUnsafePointer + SwiftString
/// borrowed marshal) before Task.Run captures the managed value.
public func callAsyncThrowingClosureTwoArgs(
    _ n: Int32,
    _ tag: String,
    _ closure: @escaping (Int32, String) async throws -> Int32
) async throws -> Int32 {
    return try await closure(n, tag)
}

/// Arity-3 with a Swift class arg: `(Int32, String, AsyncClosureArgBox) async throws -> Int32`.
/// Ensures the class arg round-trips via Unmanaged.passUnretained → Arc.Retain
/// and that the adapter keeps the original reference alive until the managed
/// Task completes.
public func callAsyncThrowingClosureThreeArgs(
    _ n: Int32,
    _ tag: String,
    _ box: AsyncClosureArgBox,
    _ closure: @escaping (Int32, String, AsyncClosureArgBox) async throws -> Int32
) async throws -> Int32 {
    return try await closure(n, tag, box)
}

/// Arity-1 primitive arg, error path: same shape as the one-arg success path
/// but the closure is expected to throw. Covers the error route of the arg-
/// bearing bridge (continuation.resume(throwing:) → SwiftBindingsBridgeError).
public func callAsyncThrowingClosureOneArgForError(
    _ value: Int32,
    _ closure: @escaping (Int32) async throws -> Int32
) async throws -> Int32 {
    return try await closure(value)
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
