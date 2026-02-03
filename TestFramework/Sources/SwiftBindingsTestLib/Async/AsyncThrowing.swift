// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async Throwing Methods

/// Error type for async throwing tests.
public enum AsyncError: Error {
    case requestedThrow
    case timeout
}

/// Struct with async throwing methods for testing async + throws emission.
public struct AsyncThrowingWorker {
    public let name: String

    public init(name: String) {
        self.name = name
    }

    /// Async throwing instance method.
    public func asyncThrowingMethod(shouldThrow: Bool) async throws -> Int32 {
        try? await Task.sleep(nanoseconds: 1_000_000)
        if shouldThrow {
            throw AsyncError.requestedThrow
        }
        return 42
    }

    /// Async throwing void method.
    public func asyncThrowingVoid(shouldThrow: Bool) async throws {
        try? await Task.sleep(nanoseconds: 1_000_000)
        if shouldThrow {
            throw AsyncError.requestedThrow
        }
    }

    /// Async throwing static method.
    public static func asyncStaticThrowing(shouldThrow: Bool) async throws -> String {
        try? await Task.sleep(nanoseconds: 1_000_000)
        if shouldThrow {
            throw AsyncError.timeout
        }
        return "success"
    }
}
