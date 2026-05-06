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

// MARK: - Sendable Enum

/// An enum that conforms to Sendable. Mirrors how Apple frameworks declare
/// payload-bearing enums (e.g. `WeatherCondition`) that are still safe to
/// share across actor / Task boundaries.
/// Tests: Sendable conformance on enum types must surface as the
/// `[SwiftSendable]` marker attribute on the generated C# class.
public enum SendableSeverity: Sendable {
    case info
    case warning(code: Int32)
    case fatal(message: String)
}

/// Bare Sendable struct with no other conformances — guards against the
/// generator only annotating types that already pick up an attribute for
/// some other reason.
public struct SendableTokenOnly: Sendable {
    public let value: Int32
    public init(value: Int32) {
        self.value = value
    }
}

/// A struct with NO Sendable conformance, used as the negative control in
/// the `[SwiftSendable]` projection test.
public struct NotSendablePlain {
    public var value: Int32
    public init(value: Int32) {
        self.value = value
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
