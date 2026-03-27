// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Throwing Closure Patterns (GRDB, Stripe)

/// Error type for throwing closure tests.
public enum ClosureError: Error {
    case invalid
    case timeout
}

/// Accepts a throwing closure that returns Int32.
/// Tests basic throwing closure emission path (ClosureEmitter.Throwing.cs).
public func callThrowingClosure(_ callback: @escaping () throws -> Int32) -> Int32 {
    do {
        return try callback()
    } catch {
        return -1
    }
}

/// Accepts a throwing closure with a parameter.
/// Tests throwing closure with arguments — should still be supported
/// (only async+throwing with params is B13-blocked).
public func callThrowingWithParam(_ callback: @escaping (Int32) throws -> String) -> String {
    do {
        return try callback(42)
    } catch {
        return "error"
    }
}

/// Accepts a throwing void closure (no return value).
/// Tests throwing closure with void return path.
public func callThrowingVoid(_ callback: @escaping () throws -> Void) -> Bool {
    do {
        try callback()
        return true
    } catch {
        return false
    }
}

/// Accepts a throwing closure returning Bool.
/// Tests the special bool marshalling path in throwing callbacks.
public func callThrowingBool(_ callback: @escaping (Int32) throws -> Bool) -> Bool {
    do {
        return try callback(10)
    } catch {
        return false
    }
}
