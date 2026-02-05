// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - OpaquePointer

/// Returns true if the opaque pointer is non-null (always true for non-optional OpaquePointer).
public func opaquePointerIsValid(_ ptr: OpaquePointer) -> Bool {
    return true
}

// MARK: - Optional OpaquePointer

/// Returns true if the optional opaque pointer is non-nil.
public func optionalOpaquePointer(_ ptr: OpaquePointer?) -> Bool {
    return ptr != nil
}

// MARK: - Struct with OpaquePointer Method

/// A wrapper around an opaque handle for testing OpaquePointer in struct methods.
public struct HandleWrapper {
    public var name: String

    public init(name: String) {
        self.name = name
    }

    /// Returns a description of the handle wrapper with the pointer's validity.
    public func describe(_ ptr: OpaquePointer) -> String {
        return "HandleWrapper(\(name)): valid"
    }
}
