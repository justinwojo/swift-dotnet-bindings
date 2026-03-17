// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Pointer Read (Mutable and Immutable)
// Both UnsafePointer<T> and UnsafeMutablePointer<T> map to IntPtr in generated bindings.

/// Reads the Int32 value at the given mutable pointer.
public func readPointerValue(_ ptr: UnsafeMutablePointer<Int32>) -> Int32 {
    return ptr.pointee
}

/// Reads the Int32 value at the given immutable pointer.
public func readPointerValueImmutable(_ ptr: UnsafePointer<Int32>) -> Int32 {
    return ptr.pointee
}

// MARK: - UnsafeMutablePointer (Read-Write)

/// Writes a value to the given mutable pointer.
public func writePointerValue(_ ptr: UnsafeMutablePointer<Int32>, value: Int32) {
    ptr.pointee = value
}

// MARK: - Pointer as Return

// Intentionally never freed — process-lifetime allocation for stable test pointer.
private let staticBufferPtr: UnsafeMutablePointer<Int32> = {
    let ptr = UnsafeMutablePointer<Int32>.allocate(capacity: 3)
    ptr[0] = 42; ptr[1] = 84; ptr[2] = 126
    return ptr
}()

/// Returns a pointer to a statically allocated Int32 buffer.
public func getStaticBuffer() -> UnsafeMutablePointer<Int32> {
    return staticBufferPtr
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
    public func describeValue(_ ptr: UnsafeMutablePointer<Int32>) -> String {
        return "\(label): \(ptr.pointee)"
    }

    /// Doubles the value at the mutable pointer.
    public func doubleValue(_ ptr: UnsafeMutablePointer<Int32>) {
        ptr.pointee *= 2
    }
}
