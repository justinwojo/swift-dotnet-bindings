// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Basic @autoclosure

/// Evaluates a condition lazily using @autoclosure.
/// Tests: @autoclosure parameter (non-escaping).
/// Expected C#: Func<bool> parameter (lazy evaluation).
public func logIfTrue(_ condition: @autoclosure () -> Bool, message: String) -> String {
    if condition() {
        return "TRUE: \(message)"
    }
    return "FALSE: \(message)"
}

/// Returns the first non-nil value using @autoclosure for lazy evaluation.
public func coalesce<T>(_ primary: T?, _ fallback: @autoclosure () -> T) -> T {
    return primary ?? fallback()
}

/// Asserts a condition and returns a status message.
public func checkCondition(_ condition: @autoclosure () -> Bool) -> Bool {
    return condition()
}

// MARK: - @autoclosure with @escaping

/// Stores an @autoclosure @escaping closure for deferred evaluation.
/// Tests: @autoclosure combined with @escaping.
/// Expected C#: Func<T> stored as delegate.
public class DeferredValue<T> {
    private let provider: () -> T

    public init(_ value: @autoclosure @escaping () -> T) {
        self.provider = value
    }

    /// Evaluates the deferred value.
    public func evaluate() -> T {
        return provider()
    }
}

/// Accepts an @autoclosure @escaping Bool and evaluates it later.
public func deferredCheck(_ condition: @autoclosure @escaping () -> Bool) -> Bool {
    let deferred = DeferredValue(condition())
    return deferred.evaluate()
}

// MARK: - Struct with @autoclosure Methods

/// A frozen struct with methods using @autoclosure parameters.
@frozen
public struct LazyEvaluator {
    public let threshold: Int32

    public init(threshold: Int32) {
        self.threshold = threshold
    }

    /// Returns the value if it meets the threshold, otherwise the fallback.
    public func valueOrFallback(_ value: Int32, fallback: @autoclosure () -> Int32) -> Int32 {
        if value >= threshold {
            return value
        }
        return fallback()
    }

    /// Static method with @autoclosure.
    public static func evaluate(_ condition: @autoclosure () -> Bool, ifTrue: Int32, ifFalse: Int32) -> Int32 {
        return condition() ? ifTrue : ifFalse
    }
}
