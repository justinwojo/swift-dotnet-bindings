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

// MARK: - B7 Closure Return Tests (Optional<String> and [String])

/// Calls an escaping closure that returns Optional<String>.
/// Used to test B7 gate lift for String in container return types.
public func callWithOptionalStringReturn(_ handler: @escaping (Int32) -> String?) -> String? {
    return handler(42)
}

/// Calls an escaping closure that returns [String].
/// Used to test B7 gate lift for String in array return types.
public func callWithStringArrayReturn(_ handler: @escaping (Int32) -> [String]) -> [String] {
    return handler(3)
}

// MARK: - P1: Nullable Closure Property (Lottie AnimationLoaded pattern)

/// Class with an optional closure stored property.
/// Tests nullable closure marshalling: setter handles both non-null and null.
public class ClosureHolder {
    public var onValueChanged: ((Int32) -> Void)?

    public init() {
        self.onValueChanged = nil
    }

    public func triggerChange(value: Int32) {
        onValueChanged?(value)
    }
}

// MARK: - P2: Static Closure Property (NVActivityIndicatorView pattern)

/// Class with a static optional closure property.
/// Combines static property access with closure marshalling.
public class LogRouter {
    public static var logHandler: ((String) -> Void)?

    public static func route(message: String) {
        logHandler?(message)
    }
}

// MARK: - P3: Optional Closure Parameter (Mixpanel Flush pattern)

/// Free function taking an optional closure parameter.
/// Tests Optional<Closure> parameter marshalling.
public func executeIfPresent(action: (() -> Void)?, fallbackValue: Int32) -> Int32 {
    if let action = action {
        action()
        return 1
    }
    return fallbackValue
}

// MARK: - X2: Method with Multiple Closure Parameters (StripePayments pattern)

/// Free function with two closure parameters (one void, one with arg).
public func executeWithCallbacks(
    onStart: @escaping () -> Void,
    onComplete: @escaping (Int32) -> Void) {
    onStart()
    onComplete(42)
}

// MARK: - Throwing Closures (REMOVED)
// Throwing closures cause emission errors (SwiftString→void* return mismatch in thunks).
// Known generator limitation. ClosureError enum also removed to avoid orphan type.
