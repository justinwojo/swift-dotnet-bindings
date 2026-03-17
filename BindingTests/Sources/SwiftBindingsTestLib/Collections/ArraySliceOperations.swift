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
}

// MARK: - Throwing with ArraySlice

/// Throws if the slice is empty, otherwise returns the first element.
public func firstOrThrow(_ slice: ArraySlice<UInt8>) throws -> UInt8 {
    guard let first = slice.first else {
        throw NSError(domain: "SliceError", code: 1, userInfo: nil)
    }
    return first
}
