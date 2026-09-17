// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Typed buffer pointer parameters
//
// UnsafeBufferPointer<T> and UnsafeMutableBufferPointer<T> are two-word values: an optional base
// address and a count. Every member here takes one, on each kind of receiver, and returns
// something computed from the elements, so a value that crosses as anything other than
// (base address, count) is observable. The mutable members write through the buffer, so the
// caller can see that Swift reached the caller's memory rather than a copy.

public enum TypedBufferError: Error {
    case empty
}

public final class TypedBufferDocument {
    public let byteCount: Int
    public let checksum: Int

    init(byteCount: Int, checksum: Int) {
        self.byteCount = byteCount
        self.checksum = checksum
    }
}

public class TypedBufferReader {
    public init() {}

    /// A static throwing parser taking bytes plus a byte array, the shape HTML parsers expose.
    public static func parse(_ html: UnsafeBufferPointer<UInt8>, _ baseUri: [UInt8]) throws -> TypedBufferDocument {
        guard !html.isEmpty else { throw TypedBufferError.empty }
        let checksum = html.reduce(0) { $0 &+ Int($1) } &+ baseUri.reduce(0) { $0 &+ Int($1) }
        return TypedBufferDocument(byteCount: html.count + baseUri.count, checksum: checksum)
    }

    public func sum(_ bytes: UnsafeBufferPointer<UInt8>) -> Int {
        return bytes.reduce(0) { $0 &+ Int($1) }
    }

    /// Reports -1 for a buffer with no base address, so an empty buffer is distinguishable.
    public func firstOrMinusOne(_ values: UnsafeBufferPointer<Int32>) -> Int32 {
        guard values.baseAddress != nil, let first = values.first else { return -1 }
        return first
    }

    public func fill(_ values: UnsafeMutableBufferPointer<Int32>, with value: Int32) {
        for index in values.indices { values[index] = value &+ Int32(index) }
    }

    public static func doubleInPlace(_ values: UnsafeMutableBufferPointer<Int64>) throws -> Int {
        guard !values.isEmpty else { throw TypedBufferError.empty }
        for index in values.indices { values[index] = values[index] &* 2 }
        return values.count
    }

    public func sumAsync(_ values: UnsafeBufferPointer<Int32>) async -> Int {
        return values.reduce(0) { $0 &+ Int($1) }
    }
}

public struct TypedBufferStats {
    public init() {}

    public func mean(_ values: UnsafeBufferPointer<Double>) -> Double {
        guard !values.isEmpty else { return 0 }
        return values.reduce(0, +) / Double(values.count)
    }
}

public func typedBufferTotal(_ values: UnsafeBufferPointer<Int64>) -> Int64 {
    return values.reduce(0, &+)
}
