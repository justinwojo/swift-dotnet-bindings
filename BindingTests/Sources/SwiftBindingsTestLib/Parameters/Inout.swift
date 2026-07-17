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

// MARK: - inout combined with a large-Optional trigger
// Both functions carry a large-Optional return (String?), but their inout parameter diverges.
// stepAndLabel's `inout Int32` is ABI-safe: MethodWrapperEmitter's cdecl path claims the method
// and forwards the inout correctly (an UnsafeMutableRawPointer param + `&` call arg + a deferred
// pointee write-back), so it MUST emit and round-trip. appendPathElement's `inout IndexPath` is
// ObjC-bridged and cannot round-trip a single C-ABI pointer: MethodWrapper rejects it and the
// OptionalPointer routing declines it (that emitter has no inout awareness), so the only remaining
// path — the raw CallConvSwift P/Invoke — would silently drop the inout. It MUST therefore be a
// clean member skip (MemberValidationPipeline Gate 5c) rather than a broken wrapper / ref-mismatched
// P/Invoke.

/// ABI-safe inout (Int32) alongside a large-Optional (String?) return.
public func stepAndLabel(_ value: inout Int32) -> String? {
    value += 1
    return value > 0 ? "positive:\(value)" : nil
}

/// ObjC-bridgeable inout (IndexPath) alongside a large-Optional (String?) return.
public func appendPathElement(_ path: inout IndexPath, _ element: Int) -> String? {
    path.append(element)
    return path.isEmpty ? nil : "count:\(path.count)"
}
