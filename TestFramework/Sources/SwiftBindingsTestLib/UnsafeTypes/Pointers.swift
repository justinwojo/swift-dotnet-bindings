// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - UnsafePointer (Read-Only)

/// Reads the Int32 value at the given pointer.
public func readPointerValue(_ ptr: UnsafePointer<Int32>) -> Int32 {
    return ptr.pointee
}

// MARK: - UnsafeMutablePointer (Read-Write)

/// Writes a value to the given mutable pointer.
public func writePointerValue(_ ptr: UnsafeMutablePointer<Int32>, value: Int32) {
    ptr.pointee = value
}

// MARK: - Pointer as Return

/// A static buffer for testing pointer returns.
private var staticBuffer: [Int32] = [42, 84, 126]

/// Returns a pointer to a statically allocated Int32 buffer.
public func getStaticBuffer() -> UnsafePointer<Int32> {
    return staticBuffer.withUnsafeBufferPointer { $0.baseAddress! }
}

// MARK: - Mutable Pointer Operations

/// Increments the value at the pointer by the given amount.
public func incrementPointer(_ ptr: UnsafeMutablePointer<Int32>, by amount: Int32) {
    ptr.pointee += amount
}

/// Fills a buffer with a repeated value.
public func fillBuffer(_ ptr: UnsafeMutablePointer<Int32>, count: Int32, value: Int32) {
    for i in 0..<Int(count) {
        ptr.advanced(by: i).pointee = value
    }
}

// MARK: - Struct with Pointer Methods

/// A worker that performs operations via typed pointers.
public struct PointerWorker {
    public var label: String

    public init(label: String) {
        self.label = label
    }

    /// Reads the value at the pointer and returns a description.
    public func describeValue(_ ptr: UnsafePointer<Int32>) -> String {
        return "\(label): \(ptr.pointee)"
    }

    /// Doubles the value at the mutable pointer.
    public func doubleValue(_ ptr: UnsafeMutablePointer<Int32>) {
        ptr.pointee *= 2
    }
}
