// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Non-View supporting types for SwiftUI bridge testing.
// These types are used as parameters by the SwiftUI Views in SimpleViews.swift
// and AsyncViews.swift, and should bind normally (not as SwiftUI Views).

// MARK: - BoundEnum parameter type

/// Simple enum for BoundEnum parameter testing.
@frozen public enum AlertStyle: Int32 {
    case info = 0
    case warning = 1
    case error = 2
}

// MARK: - BoundType parameter type

/// Simple class for BoundType parameter testing.
/// Includes deinit counter for lifetime validation.
public class SimpleModel {
    public static var deinitCount: Int32 = 0

    public var value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func getValue() -> Int32 {
        return value
    }

    deinit {
        SimpleModel.deinitCount += 1
    }
}

// MARK: - Bridge test supporting types

/// Frozen struct with a reference-holding String field for closure arg testing.
/// @frozen ensures the bridge emits CallConvSwift layout (not resilient indirect pointer).
@frozen public struct FrozenRefArg {
    public let s: String
    public init(s: String) { self.s = s }
}

// MARK: - Async chain types

/// Async service class for testing single-level async chain inference.
/// init(key:) is `async throws` — the bridge must construct this in a Task.
public class AsyncService {
    public let key: String

    public init(key: String) async throws {
        // Simulate async initialization (e.g. network validation)
        try await Task.sleep(nanoseconds: 1_000)
        self.key = key
    }

    public func getKey() -> String { key }
}

/// Intermediate class that depends on AsyncService — for testing deep chains.
/// init(service:, mode:) is synchronous but takes an async dependency.
public class Processor {
    public let service: AsyncService
    public let mode: Int32

    public init(service: AsyncService, mode: Int32) {
        self.service = service
        self.mode = mode
    }

    public func getMode() -> Int32 { mode }
}
