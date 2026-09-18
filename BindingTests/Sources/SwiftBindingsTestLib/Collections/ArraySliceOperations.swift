// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - ArraySlice as Parameter (Normalization Target)

/// Sums elements in an ArraySlice of Int32.
/// Generator should emit a Swift wrapper accepting Array<Int32> with ArraySlice() conversion.
public func sumArraySlice(_ slice: ArraySlice<Int32>) -> Int32 {
    return slice.reduce(0, +)
}

/// Returns the count of elements in an ArraySlice.
public func arraySliceCount(_ slice: ArraySlice<UInt8>) -> Int32 {
    return Int32(slice.count)
}

/// Returns true if the ArraySlice is empty.
public func isEmptyArraySlice(_ slice: ArraySlice<UInt8>) -> Bool {
    return slice.isEmpty
}

// MARK: - Multiple ArraySlice Parameters

/// Concatenates two ArraySlice<UInt8> values and returns the combined count.
public func combinedSliceCount(_ first: ArraySlice<UInt8>, _ second: ArraySlice<UInt8>) -> Int32 {
    return Int32(first.count + second.count)
}

// MARK: - ArraySlice on Class Method

/// A simple processor class with ArraySlice methods.
public class SliceProcessor {
    private let offset: Int32

    public init(offset: Int32) {
        self.offset = offset
    }

    /// Instance method taking ArraySlice parameter.
    public func processSlice(_ data: ArraySlice<Int32>) -> Int32 {
        return data.reduce(offset, +)
    }

    /// Static method taking ArraySlice parameter.
    public static func totalSlice(_ data: ArraySlice<Int32>) -> Int32 {
        return data.reduce(0, +)
    }

    /// Array overload paired with the ArraySlice overload below. Both project to
    /// IEnumerable<UInt8>, so the generated names must retain their Swift collection kind.
    public static func overloadDispatch(_ data: Array<UInt8>) -> Int32 {
        return 1_000 + Int32(data.reduce(0) { $0 + Int($1) })
    }

    /// ArraySlice half of the projection-collision pair. The normalization bridge accepts
    /// Array<UInt8> at its wrapper boundary but must still dispatch to this overload.
    public static func overloadDispatch(_ data: ArraySlice<UInt8>) -> Int32 {
        return 2_000 + Int32(data.reduce(0) { $0 + Int($1) })
    }
}

// MARK: - Throwing with ArraySlice

/// Throws if the slice is empty, otherwise returns the first element.
public func firstOrThrow(_ slice: ArraySlice<UInt8>) throws -> UInt8 {
    guard let first = slice.first else {
        throw NSError(domain: "SliceError", code: 1, userInfo: nil)
    }
    return first
}

// MARK: - ArraySlice beside optional parameters

/// Sums the slice, starting from the bias when one is given. An optional small primitive is
/// read by value from its tag-byte buffer before the call.
public func sumArraySliceWithBias(_ slice: ArraySlice<Int32>, bias: Int32?) -> Int32 {
    return slice.reduce(bias ?? 0, +)
}

/// Sums the slice, then applies the adjuster when one is given. An optional existential is
/// passed to the wrapper by address.
public func sumArraySliceAdjusted(_ slice: ArraySlice<Int32>, adjuster: (any DefaultOptionalAdjuster)?) -> Int32 {
    let sum = slice.reduce(0, +)
    return adjuster?.adjust(sum) ?? sum
}
