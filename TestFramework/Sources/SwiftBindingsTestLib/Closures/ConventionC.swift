// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - @convention(c) Closures

/// Calls a C-convention function pointer with an Int32.
public func callCFunction(_ fn: @convention(c) (Int32) -> Int32) -> Int32 {
    return fn(42)
}

/// Calls a C-convention void function pointer.
public func callCVoidFunction(_ fn: @convention(c) () -> Void) {
    fn()
}

/// Calls a C-convention function with two arguments.
public func callCBinaryFunction(_ fn: @convention(c) (Int32, Int32) -> Int32) -> Int32 {
    return fn(10, 20)
}

/// Calls a C-convention predicate.
public func callCPredicate(_ fn: @convention(c) (Int32) -> Bool, value: Int32) -> Bool {
    return fn(value)
}
