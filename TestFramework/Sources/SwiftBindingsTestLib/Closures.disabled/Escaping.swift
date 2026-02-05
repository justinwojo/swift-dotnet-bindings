// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Escaping Closure Free Functions

/// Calls an escaping closure with an Int32 value.
public func callWithInt32(_ callback: @escaping (Int32) -> Int32) -> Int32 {
    return callback(42)
}

/// Calls an escaping void closure.
public func callVoidCallback(_ callback: @escaping () -> Void) {
    callback()
}

/// Calls an escaping closure with multiple arguments.
public func callMultiArg(_ callback: @escaping (Int32, Int32) -> Int32) -> Int32 {
    return callback(10, 20)
}

/// Calls an escaping closure with a Bool argument.
public func callBoolCallback(_ callback: @escaping (Bool) -> Bool) -> Bool {
    return callback(true)
}

/// Calls an escaping closure with a FrozenPoint argument.
public func callWithFrozenStruct(_ callback: @escaping (FrozenPoint) -> Double) -> Double {
    let point = FrozenPoint(x: 3.0, y: 4.0)
    return callback(point)
}

/// Calls a closure with a Double argument.
public func callDoubleCallback(_ callback: @escaping (Double) -> Double) -> Double {
    return callback(3.14159)
}

// MARK: - Struct with Closure Methods

/// A frozen struct with instance and static methods accepting closures.
@frozen
public struct ClosureConsumer {
    public let multiplier: Int32

    public init(multiplier: Int32) {
        self.multiplier = multiplier
    }

    /// Instance method that accepts a closure.
    public func applyToValue(_ value: Int32, using transform: @escaping (Int32) -> Int32) -> Int32 {
        return transform(value * multiplier)
    }

    /// Static method that accepts a closure.
    public static func processWithClosure(_ value: Int32, closure: @escaping (Int32) -> Int32) -> Int32 {
        return closure(value)
    }
}

/// Calls the closure multiple times and sums the results.
public func callMultipleTimes(_ callback: @escaping (Int32) -> Int32, times: Int32) -> Int32 {
    var sum: Int32 = 0
    for i in 1...times {
        sum += callback(Int32(i))
    }
    return sum
}

// MARK: - Throwing Closures
// Tests: @escaping closures that throw
// Expected C#: Delegate with exception propagation
// Limitation: Throwing closures are not yet supported by the generator

/// Error type for throwing closure tests.
public enum ClosureError: Error {
    case invalidInput
    case overflow
}

/// Accepts a throwing closure and calls it with try.
public func callThrowingClosure(_ callback: @escaping (Int32) throws -> Int32) throws -> Int32 {
    return try callback(42)
}

/// Accepts a throwing closure and catches errors, returning a default.
public func callThrowingClosureWithFallback(
    _ callback: @escaping (Int32) throws -> Int32,
    fallback: Int32
) -> Int32 {
    do {
        return try callback(42)
    } catch {
        return fallback
    }
}

/// Accepts a throwing void closure.
public func callThrowingVoidClosure(_ callback: @escaping () throws -> Void) throws {
    try callback()
}

/// Accepts a throwing closure returning String.
public func callThrowingStringClosure(_ callback: @escaping (String) throws -> String) throws -> String {
    return try callback("test")
}
