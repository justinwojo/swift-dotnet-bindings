// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Inout Parameters (Tier 2)

/// Increments an Int32 value in-place.
public func incrementValue(_ value: inout Int32) {
    value += 1
}

/// Swaps two Int32 values in-place.
public func swapValues(_ a: inout Int32, _ b: inout Int32) {
    let temp = a
    a = b
    b = temp
}

/// Increments a FrozenPoint's x and y in-place.
public func incrementPoint(_ point: inout FrozenPoint) {
    point.x += 1.0
    point.y += 1.0
}

/// Doubles a value in-place and returns the old value.
public func doubleInPlace(_ value: inout Int32) -> Int32 {
    let old = value
    value *= 2
    return old
}
