// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Sendable Type

/// A struct conforming to Sendable, safe to pass across concurrency domains.
/// Tests: Sendable conformance on value types.
/// Expected C#: Regular struct emission; Sendable is a marker protocol.
public struct SendablePoint: Sendable {
    public var x: Int32
    public var y: Int32

    public init(x: Int32, y: Int32) {
        self.x = x
        self.y = y
    }

    /// Returns distance from origin as an approximation.
    public func manhattanDistance() -> Int32 {
        return abs(x) + abs(y)
    }
}

// MARK: - Sendable Class

/// A final class conforming to Sendable with immutable state.
/// Tests: Sendable conformance on reference types (requires immutability).
public final class SendableConfig: Sendable {
    public let name: String
    public let maxRetries: Int32

    public init(name: String, maxRetries: Int32) {
        self.name = name
        self.maxRetries = maxRetries
    }

    /// Returns a description of this config.
    public func describe() -> String {
        return "\(name): maxRetries=\(maxRetries)"
    }
}

// MARK: - @Sendable Closure

/// Accepts a @Sendable closure, which must be safe to call from any concurrency domain.
/// Tests: @Sendable annotation on closure parameters.
/// Expected C#: Same as regular closure; @Sendable is a compile-time check.
public func performWithSendable(_ work: @Sendable () -> Int32) -> Int32 {
    return work()
}

/// Accepts a @Sendable escaping closure.
public func storeAndExecuteSendable(_ work: @escaping @Sendable () -> String) -> String {
    return work()
}

// MARK: - Free Functions

/// Creates a SendablePoint.
public func createSendablePoint(x: Int32, y: Int32) -> SendablePoint {
    return SendablePoint(x: x, y: y)
}

/// Creates a SendableConfig.
public func createSendableConfig(name: String, maxRetries: Int32) -> SendableConfig {
    return SendableConfig(name: name, maxRetries: maxRetries)
}
