// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - UnsafeRawPointer

/// Reads an Int32 from a raw pointer (assumes proper alignment).
public func rawPointerToInt32(_ ptr: UnsafeRawPointer) -> Int32 {
    return ptr.load(as: Int32.self)
}

// MARK: - UnsafeMutableRawPointer

/// Stores an Int32 value at the raw mutable pointer.
public func storeInt32(_ ptr: UnsafeMutableRawPointer, value: Int32) {
    ptr.storeBytes(of: value, as: Int32.self)
}

// MARK: - Both Raw Pointer Types

/// Copies count bytes from source to destination.
public func copyRawBytes(from source: UnsafeRawPointer, to dest: UnsafeMutableRawPointer, count: Int32) {
    dest.copyMemory(from: source, byteCount: Int(count))
}

// MARK: - Struct with Raw Pointer Method

/// A worker that performs operations via raw (untyped) pointers.
public struct RawMemoryWorker {
    public var alignment: Int32

    public init(alignment: Int32) {
        self.alignment = alignment
    }

    /// Reads an Int32 from the raw pointer and returns a description.
    public func readInt32(_ ptr: UnsafeRawPointer) -> String {
        let value = ptr.load(as: Int32.self)
        return "value=\(value), alignment=\(alignment)"
    }
}
