// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Generic Types with Pointer Parameters

/// Generic container that can hold a pointer type.
/// Regression guard for IntPtr<T> generic emission bugs where T was a pointer type.
/// Must emit IntPtr, NOT IntPtr<T>.
public struct PointerContainer<T> {
    public let pointer: T

    public init(pointer: T) {
        self.pointer = pointer
    }

    public func getPointer() -> T {
        return pointer
    }
}

// MARK: - Functions Returning Generic Pointer Containers

/// Creates a PointerContainer holding an UnsafePointer<Int32>.
public func createInt32PointerContainer(_ ptr: UnsafePointer<Int32>) -> PointerContainer<UnsafePointer<Int32>> {
    return PointerContainer(pointer: ptr)
}

/// Creates a PointerContainer holding an OpaquePointer.
public func createOpaquePointerContainer(_ ptr: OpaquePointer) -> PointerContainer<OpaquePointer> {
    return PointerContainer(pointer: ptr)
}

// MARK: - Functions Accepting Generic Pointer Containers

/// Reads the value from a PointerContainer<UnsafePointer<Int32>>.
public func readFromPointerContainer(_ container: PointerContainer<UnsafePointer<Int32>>) -> Int32 {
    return container.pointer.pointee
}
