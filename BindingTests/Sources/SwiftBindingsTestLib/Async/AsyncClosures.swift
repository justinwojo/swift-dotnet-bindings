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

/// Session D: async-throwing closure returning Foundation.Data. Routed through
/// DataAsyncClosureHelper.RunDataAsync + a Data-shaped Swift box that resumes
/// with Data(bytes: bytesPtr, count: length). The outer method returns a byte
/// checksum (Int64) rather than Data itself — Data return from the *closure* is
/// in scope for Session D, but Data return from the *outer async method* is a
/// separate (pre-existing) gap in the async outer-method emitter and out of scope
/// here. Checksumming proves the full byte payload round-tripped intact.
public func callAsyncThrowingDataClosure(_ closure: @escaping () async throws -> Data) async throws -> Int64 {
    let data = try await closure()
    var sum: Int64 = 0
    for b in data { sum = sum &+ Int64(b) }
    return sum
}

// MARK: - Session F: async-throwing closures returning Swift.String
// Routed through StringAsyncClosureHelper.RunStringAsync + a String-shaped Swift
// box that resumes with String(decoding: UnsafeBufferPointer(...), as: UTF8.self).
// Unlocks StripeConnect's only public ctor and PaymentSheet.IntentConfiguration
// handlers. Unlike Data, String supports full 0–4 arity.

/// Session F: no-arg async-throwing closure returning Swift.String.
public func callAsyncThrowingStringClosure(_ closure: @escaping () async throws -> String) async throws -> String {
    return try await closure()
}

/// Session F: invokes the same String-returning async-throwing closure twice.
/// Validates per-invocation continuation box / adapter lifetime.
public func callAsyncThrowingStringClosureTwice(_ closure: @escaping () async throws -> String) async throws -> String {
    let a = try await closure()
    let b = try await closure()
    return a + "|" + b
}

/// Session F: arity-1 primitive arg, String return.
public func callAsyncThrowingStringClosureArity1(
    _ closure: @escaping (Int32) async throws -> String
) async throws -> String {
    return try await closure(42)
}

/// Session F: arity-2 mixed (Int32, String) arg, String return. Shape matches
/// PaymentSheet.IntentConfiguration.ConfirmHandler.
public func callAsyncThrowingStringClosureArity2(
    _ closure: @escaping (Int32, String) async throws -> String
) async throws -> String {
    return try await closure(7, "hello")
}

// MARK: - Session C: non-throwing async closures (primitive return, 0–4 args)
// Baseline non-throwing shape: `@escaping (Args) async -> T` where T is a
// BitwiseCopyable primitive and the outer method is `async`. Exceptions inside
// the managed closure trigger Environment.FailFast (no Swift error channel).

/// Baseline non-throwing async closure: no args, primitive return.
public func callAsyncClosure(_ closure: @escaping () async -> Int32) async -> Int32 {
    return await closure()
}

/// Invokes the same non-throwing async closure twice within a single outer
/// call. Mirrors the throwing "…Twice" variant and validates the continuation
/// box / adapter lifetime when a single closure value is awaited multiple times.
public func callAsyncClosureTwice(_ closure: @escaping () async -> Int32) async -> Int32 {
    let a = await closure()
    let b = await closure()
    return a &+ b
}

/// Arity-1 primitive arg: `(Int32) async -> Int32`.
public func callAsyncClosureWithParam(_ value: Int32, closure: @escaping (Int32) async -> Int32) async -> Int32 {
    return await closure(value)
}

/// Arity-3 mixed non-throwing: `(Int32, String, AsyncClosureArgBox) async -> Int32`.
/// Mirrors `callAsyncThrowingClosureThreeArgs` for the Session C non-throwing
/// bridge so the String + class arg categories are exercised end-to-end on the
/// non-throwing path (not just primitives).
public func callAsyncClosureThreeArgs(
    _ n: Int32,
    _ tag: String,
    _ box: AsyncClosureArgBox,
    _ closure: @escaping (Int32, String, AsyncClosureArgBox) async -> Int32
) async -> Int32 {
    return await closure(n, tag, box)
}

#if swift(>=99.0)

/// Accepts an async closure returning String.
public func callAsyncStringClosure(_ closure: @escaping () async -> String) async -> String {
    return await closure()
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
