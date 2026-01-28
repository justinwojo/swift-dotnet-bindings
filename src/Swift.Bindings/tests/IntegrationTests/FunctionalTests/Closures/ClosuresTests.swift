// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// NOTE: @convention(c) closures are NOT tested here because they require
// different marshalling (no context parameter). The current implementation
// treats all closures as escaping closures with context parameters.
// TODO: Add @convention(c) support with proper detection from mangled names.

// MARK: - @escaping Closures (Primitive Types)

/// Calls an escaping closure with an Int32 and returns the result
public func callWithInt32(_ callback: @escaping (Int32) -> Int32) -> Int32 {
    return callback(42)
}

/// Calls an escaping void closure
public func callVoidCallback(_ callback: @escaping () -> Void) {
    callback()
}

/// Calls an escaping closure with multiple Int32 arguments
public func callMultiArg(_ callback: @escaping (Int32, Int32) -> Int32) -> Int32 {
    return callback(10, 20)
}

/// Calls an escaping closure with a Bool argument
public func callBoolCallback(_ callback: @escaping (Bool) -> Bool) -> Bool {
    return callback(true)
}

/// Calls a closure that returns a Double
public func callDoubleCallback(_ callback: @escaping (Double) -> Double) -> Double {
    return callback(3.14159)
}

// MARK: - Struct with Closure Methods

@frozen
public struct ClosureConsumer {
    public let multiplier: Int32

    public init(multiplier: Int32) {
        self.multiplier = multiplier
    }

    /// Instance method that accepts a closure
    public func applyToValue(_ value: Int32, using transform: @escaping (Int32) -> Int32) -> Int32 {
        return transform(value * multiplier)
    }

    /// Static method that accepts a closure
    public static func processWithClosure(_ value: Int32, closure: @escaping (Int32) -> Int32) -> Int32 {
        return closure(value)
    }
}

// MARK: - Closure Called Multiple Times

/// Calls the closure multiple times and sums the results
public func callMultipleTimes(_ callback: @escaping (Int32) -> Int32, times: Int32) -> Int32 {
    var sum: Int32 = 0
    for i in 1...times {
        sum += callback(Int32(i))
    }
    return sum
}
