// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Noncopyable Types (Swift 6.0)
// Tests: ~Copyable structs, consuming/borrowing ownership modifiers, deinit
// Expected C#: Move semantics instead of copy/ARC; different value witness table
// Limitation: Noncopyable types are not yet supported by the generator

/// A noncopyable resource with unique ownership semantics.
///
/// The `~Copyable` constraint prevents implicit copying. The binding generator
/// must detect the inverse conformance requirement in ABI JSON and emit
/// move semantics rather than copy/ARC patterns.
public struct UniqueResource: ~Copyable {
    public let id: Int32

    public init(id: Int32) {
        self.id = id
    }

    /// Consuming method — takes ownership and invalidates the caller's copy.
    public consuming func consume() -> Int32 {
        return id
    }

    /// Borrowing method — read-only access without taking ownership.
    public borrowing func inspect() -> Int32 {
        return id
    }

    deinit {
        // Cleanup — deinit on noncopyable types is called deterministically
        // when the value goes out of scope (not via ARC).
    }
}

// MARK: - Noncopyable File Handle

/// A more realistic noncopyable type representing an exclusive resource.
public struct FileHandle: ~Copyable {
    private var descriptor: Int32
    private var isClosed: Bool

    public init(descriptor: Int32) {
        self.descriptor = descriptor
        self.isClosed = false
    }

    /// Returns the file descriptor value.
    public borrowing func getDescriptor() -> Int32 {
        return descriptor
    }

    /// Returns whether the handle is still open.
    public borrowing func isOpen() -> Bool {
        return !isClosed
    }

    /// Closes the handle, consuming ownership.
    ///
    /// Sets `isClosed` so that `deinit` knows the handle was explicitly closed.
    public consuming func close() -> Int32 {
        isClosed = true
        let fd = descriptor
        // In a real implementation, this would call close(2)
        return fd
    }

    deinit {
        if !isClosed {
            // Auto-close if not explicitly closed
        }
    }
}

// MARK: - Free Functions with Ownership Modifiers

/// Takes ownership of a UniqueResource and returns its id.
public func transferOwnership(_ resource: consuming UniqueResource) -> Int32 {
    return resource.consume()
}

/// Borrows a UniqueResource to inspect it without taking ownership.
public func borrowResource(_ resource: borrowing UniqueResource) -> Int32 {
    return resource.inspect()
}

// MARK: - Free Functions (Creation Helpers)

/// Creates a UniqueResource with the given id.
public func createUniqueResource(id: Int32) -> UniqueResource {
    return UniqueResource(id: id)
}

/// Creates a FileHandle with the given descriptor.
public func createFileHandle(descriptor: Int32) -> FileHandle {
    return FileHandle(descriptor: descriptor)
}
