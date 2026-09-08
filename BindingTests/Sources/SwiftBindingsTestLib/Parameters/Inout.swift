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

// MARK: - Reference cell versus value storage

public final class InoutReferenceItem {
    public var number: Int32
    public init(_ number: Int32) { self.number = number }
}

/// Refused: native inout expects a mutable reference cell, not an object handle.
public func replaceInoutReferenceItem(_ value: inout InoutReferenceItem) {
    value = InoutReferenceItem(99)
}

/// Even a read-only body takes the same reference-cell ABI.
public func inspectInoutReferenceItem(_ value: inout InoutReferenceItem) -> Int32 {
    value.number
}

/// The same class remains usable through ordinary by-value parameters.
public func readInoutReferenceItem(_ value: InoutReferenceItem) -> Int32 { value.number }

public struct InoutResilientItem {
    public var number: Int32
    public init(_ number: Int32) { self.number = number }
}

/// A resilient value's payload is already value storage; do not apply the class refusal.
public func replaceInoutResilientItem(_ value: inout InoutResilientItem) {
    value = InoutResilientItem(77)
}

public func readInoutResilientItem(_ value: InoutResilientItem) -> Int32 { value.number }

public func echoInoutNeighborText(_ text: String) -> String { text }

// MARK: - Projected String writeback

public func expandInoutText(_ text: inout String) {
    text = "replacement text larger than the inline small-string representation"
}

public func shrinkInoutText(_ text: inout String) { text = "small" }

public func expandOptionalInoutText(_ text: inout String?) {
    text = "optional replacement larger than the inline String representation"
}

public func clearOptionalInoutText(_ text: inout String?) { text = nil }

public func replaceOptionalInoutTextThenThrow(_ text: inout String?) throws {
    text = "optional mutation survives the Swift error"
    throw InoutTextError.rejected
}

public func replaceTwoInoutTexts(_ first: inout String, _ second: inout String) {
    first = "first replacement larger than the inline String representation"
    second = "second"
}

public enum InoutTextError: Error { case rejected }

public func replaceInoutTextThenThrow(_ text: inout String) throws {
    text = "mutation survives the Swift error"
    throw InoutTextError.rejected
}

public func replaceInoutTextAndReturnItem(_ text: inout String) -> InoutReferenceItem {
    text = "mutation alongside an owned class result"
    return InoutReferenceItem(61)
}

public struct InoutTextInitializer {
    public let number: Int32
    public init?(_ text: inout String, succeed: Bool) {
        text = "mutation survives both failable initializer outcomes"
        guard succeed else { return nil }
        number = 71
    }
}
