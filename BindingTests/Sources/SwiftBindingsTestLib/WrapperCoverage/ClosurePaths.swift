// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - @convention(c) Closure Parameter on Class Method

/// Class with method taking @convention(c) closure — exercises
/// ClosureEmitter:255-292 (@convention(c) vs escaping callback path) and
/// WrapperEmitter.Marshalling:255-292 (@convention(c) closure param).
/// Different from free function @convention(c) (already tested in ConventionC.swift)
/// because class methods have additional self parameter handling.
public class CCallbackRunner {
    public let scale: Int32

    public init(scale: Int32) {
        self.scale = scale
    }

    /// Runs a C-convention function pointer on the scaled value.
    public func runC(_ fn: @convention(c) (Int32) -> Int32) -> Int32 {
        return fn(scale)
    }

    /// C-convention void callback.
    public func runCVoid(_ fn: @convention(c) (Int32) -> Void) {
        fn(scale)
    }
}

// MARK: - Async + Throwing Closure Parameter

/// Class with method taking async+throwing closure — exercises
/// ClosureEmitter:294-300 (async + throwing closure param).
/// Note: This is a known challenging pattern; the test may expose generator limitations.
public class AsyncClosureRunner {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    /// Takes an async throwing closure — exercises the async+throwing closure emission path.
    public func runAsyncThrowing(_ handler: @escaping (Int32) async throws -> String) async throws -> String {
        return try await handler(value)
    }

    /// Simpler async closure (non-throwing) for comparison.
    public func runAsync(_ handler: @escaping (Int32) async -> Int32) async -> Int32 {
        return await handler(value)
    }
}
