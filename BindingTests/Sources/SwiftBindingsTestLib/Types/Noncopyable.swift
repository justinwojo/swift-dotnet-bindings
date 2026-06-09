// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Noncopyable Types (Swift 6.0)
// Tests: ~Copyable structs, consuming/borrowing ownership modifiers, deinit
// Expected C#: Class wrapper with SafeHandle; borrowing pointer semantics in @_cdecl wrappers
// The generator emits inline UnsafePointer<T>.pointee borrows (no let binding = no copy)

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

// MARK: - Consuming non-copyable deinit-runs-exactly-once probe

/// A noncopyable resource that feeds the shared allocation counters (see
/// `Lifetime/OwnershipTests.swift`) so a C# test can assert its `deinit` runs EXACTLY once when the
/// value is handed to a `consuming` function.
///
/// A `~Copyable` value is address-only: the generator lowers it to an indirect (by-buffer-pointer)
/// parameter and routes it through the `@_cdecl` wrapper, which `move`s the value into the Swift call.
/// Before that routing existed, the C# SafeHandle destroyed the value a SECOND time after Swift had
/// already consumed it — a double-free (SIGABRT), or a `deinit` count of 2. The fix marks the C# handle
/// consumed so a later `Dispose()` is a no-op.
public struct TrackedResource: ~Copyable {
    public let id: Int32

    public init(id: Int32) {
        self.id = id
        recordTrackedAllocation()
    }

    /// Borrowing read — does not consume.
    public borrowing func peek() -> Int32 {
        return id
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// Takes ownership of a `TrackedResource` (`consuming`) and returns its id. Swift consumes — and so
/// `deinit`s — the value exactly once inside this call; the caller's handle is left invalid.
public func consumeTrackedResource(_ resource: consuming TrackedResource) -> Int32 {
    return resource.peek()
}

/// Creates a `TrackedResource` with the given id (bumps the live-object counter).
public func createTrackedResource(id: Int32) -> TrackedResource {
    return TrackedResource(id: id)
}

// MARK: - Throwing: consuming non-copyable on a throwing function

public enum TrackedResourceError: Error {
    case rejected
}

/// Takes ownership of a `TrackedResource` (`consuming`) and THROWS when `id` is negative.
///
/// Swift owns a `consuming` parameter regardless of control flow: whether this returns normally or
/// throws, the value's `deinit` runs exactly once inside the call (the throw unwinds through the
/// callee's end-of-scope cleanup). The generated C# wrapper therefore marks the handle consumed
/// BEFORE it rethrows the Swift error — otherwise the SafeHandle would run a second value-witness
/// destroy on the already-consumed buffer on the throw path (a double-free the non-throwing
/// `consumeTrackedResource` test cannot reach).
public func consumeTrackedResourceOrThrow(_ resource: consuming TrackedResource) throws -> Int32 {
    let value = resource.peek()
    if value < 0 {
        throw TrackedResourceError.rejected
    }
    return value
}
