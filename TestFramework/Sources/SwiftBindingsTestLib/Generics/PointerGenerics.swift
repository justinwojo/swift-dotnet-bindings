// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Generic Types with Pointer Parameters (Phase 61 Regression Test)

/// Generic container that can hold a pointer type.
/// Phase 61 fixed IntPtr<T> generic emission bugs where T was a pointer type.
public struct PointerContainer<T> {
    public let pointer: T

    public init(pointer: T) {
        self.pointer = pointer
    }

    public func getPointer() -> T {
        return pointer
    }
}

/// Generic optional wrapper for pointer types.
public struct OptionalPointer<T> {
    private var _value: T?

    public init(value: T?) {
        self._value = value
    }

    public var value: T? {
        return _value
    }

    public var hasValue: Bool {
        return _value != nil
    }
}

// MARK: - Functions Returning Generic Pointer Containers

/// Creates a PointerContainer holding an UnsafePointer<Int32>.
public func createInt32PointerContainer(_ ptr: UnsafePointer<Int32>) -> PointerContainer<UnsafePointer<Int32>> {
    return PointerContainer(pointer: ptr)
}

/// Creates a PointerContainer holding an UnsafeMutablePointer<Int32>.
public func createMutableInt32PointerContainer(_ ptr: UnsafeMutablePointer<Int32>) -> PointerContainer<UnsafeMutablePointer<Int32>> {
    return PointerContainer(pointer: ptr)
}

/// Creates a PointerContainer holding an OpaquePointer.
public func createOpaquePointerContainer(_ ptr: OpaquePointer) -> PointerContainer<OpaquePointer> {
    return PointerContainer(pointer: ptr)
}

/// Creates a PointerContainer holding an UnsafeRawPointer.
public func createRawPointerContainer(_ ptr: UnsafeRawPointer) -> PointerContainer<UnsafeRawPointer> {
    return PointerContainer(pointer: ptr)
}

/// Creates a PointerContainer holding an UnsafeMutableRawPointer.
public func createMutableRawPointerContainer(_ ptr: UnsafeMutableRawPointer) -> PointerContainer<UnsafeMutableRawPointer> {
    return PointerContainer(pointer: ptr)
}

// MARK: - Functions Accepting Generic Pointer Containers

/// Reads the value from a PointerContainer<UnsafePointer<Int32>>.
public func readFromPointerContainer(_ container: PointerContainer<UnsafePointer<Int32>>) -> Int32 {
    return container.pointer.pointee
}

/// Writes to a PointerContainer<UnsafeMutablePointer<Int32>>.
public func writeToPointerContainer(_ container: PointerContainer<UnsafeMutablePointer<Int32>>, value: Int32) {
    container.pointer.pointee = value
}

// MARK: - Optional Pointer Containers

/// Creates an OptionalPointer with an UnsafePointer<Int32>.
public func createOptionalPointer(_ ptr: UnsafePointer<Int32>?) -> OptionalPointer<UnsafePointer<Int32>> {
    return OptionalPointer(value: ptr)
}

/// Creates an empty OptionalPointer for UnsafePointer<Int32>.
public func createEmptyOptionalPointer() -> OptionalPointer<UnsafePointer<Int32>> {
    return OptionalPointer(value: nil)
}

// MARK: - Nested Generic Pointer Types

/// A result type that can contain a pointer container on success.
public enum PointerResult<T> {
    case success(PointerContainer<T>)
    case failure(String)

    public var isSuccess: Bool {
        switch self {
        case .success: return true
        case .failure: return false
        }
    }
}

/// Creates a PointerResult with an UnsafePointer<Int32> container.
public func createPointerResult(_ ptr: UnsafePointer<Int32>?, errorMessage: String) -> PointerResult<UnsafePointer<Int32>> {
    if let ptr = ptr {
        return .success(PointerContainer(pointer: ptr))
    } else {
        return .failure(errorMessage)
    }
}

// MARK: - Struct with Pointer Generic Properties

/// Struct that holds multiple pointer containers of different types.
public struct PointerHolder {
    public let label: String

    public init(label: String) {
        self.label = label
    }

    /// Method returning a generic pointer container.
    public func wrapPointer(_ ptr: UnsafePointer<Int32>) -> PointerContainer<UnsafePointer<Int32>> {
        return PointerContainer(pointer: ptr)
    }

    /// Method returning a generic mutable pointer container.
    public func wrapMutablePointer(_ ptr: UnsafeMutablePointer<Int32>) -> PointerContainer<UnsafeMutablePointer<Int32>> {
        return PointerContainer(pointer: ptr)
    }

    /// Method accepting a pointer container and returning the value.
    public func readContainer(_ container: PointerContainer<UnsafePointer<Int32>>) -> Int32 {
        return container.pointer.pointee
    }
}

// MARK: - Generic Functions with Pointer Constraints

/// A generic pair where one element can be a pointer.
public struct PointerPair<P, V> {
    public let pointer: P
    public let value: V

    public init(pointer: P, value: V) {
        self.pointer = pointer
        self.value = value
    }
}

/// Creates a pair of an UnsafePointer<Int32> and an Int32 value.
public func createPointerValuePair(_ ptr: UnsafePointer<Int32>, value: Int32) -> PointerPair<UnsafePointer<Int32>, Int32> {
    return PointerPair(pointer: ptr, value: value)
}

/// Creates a pair of an OpaquePointer and a String label.
public func createLabeledPointer(_ ptr: OpaquePointer, label: String) -> PointerPair<OpaquePointer, String> {
    return PointerPair(pointer: ptr, value: label)
}

// MARK: - Buffer Pointer Generics

/// Container for buffer pointers.
public struct BufferContainer<T> {
    public let buffer: UnsafeBufferPointer<T>

    public init(buffer: UnsafeBufferPointer<T>) {
        self.buffer = buffer
    }

    public var count: Int {
        return buffer.count
    }
}

/// Creates a BufferContainer from an array.
/// Note: The buffer is only valid for the duration of the closure.
public func withBufferContainer<T>(_ array: [T], body: (BufferContainer<T>) -> Int32) -> Int32 {
    return array.withUnsafeBufferPointer { buffer in
        body(BufferContainer(buffer: buffer))
    }
}
