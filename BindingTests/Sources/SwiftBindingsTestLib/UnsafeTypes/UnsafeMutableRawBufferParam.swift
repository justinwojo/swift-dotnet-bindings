// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - UnsafeMutableRawBufferParam
//
// Exercises UnsafeMutableRawBufferPointer parameter marshalling. Mirrors the
// read-only UnsafeRawBufferPointer fixture (UnsafeRawBufferParam.swift) bit
// for bit at the C ABI boundary — the generator splits the parameter into
// (ptr, len) at the @_cdecl edge regardless of mutability — but exposes the
// writable Span<byte> on the C# side. Tests in
// RuntimeTestsApp/EdgeCases/UnsafeMutableRawBufferPointerTests.cs assert that
// Swift-side mutations are visible to the C# caller after the synchronous
// call returns, that empty spans pin to a null-but-safe pointer, and that
// aliased buffers reach the same backing memory rather than silently being
// copied.

/// Struct exercising UnsafeMutableRawBufferPointer parameter round-trips.
/// Methods cover the four observables: length probe (empty-span handling),
/// pure write-back, mixed read+write round-trip, and two-buffer aliasing.
public struct UnsafeMutableRawBufferHolder {
    public let fillByte: UInt8
    public let delta: UInt8

    public init(fillByte: UInt8, delta: UInt8) {
        self.fillByte = fillByte
        self.delta = delta
    }

    /// Unrelated method — keeps coverage on the enclosing type alongside the
    /// raw-buffer methods so a regression that drops the type is still caught.
    public func multiplier(_ value: Int32) -> Int32 {
        return value &* Int32(fillByte)
    }

    /// Returns the byte count of the incoming buffer without mutating it.
    /// Proves the length half of the split (ptr, len) ABI round-trips,
    /// including the empty-span case where ptr is null.
    public func writeLength(_ buffer: UnsafeMutableRawBufferPointer) -> Int32 {
        return Int32(buffer.count)
    }

    /// Pure write-back: fills every byte of the pinned C# memory with
    /// `fillByte`. Returns the count written. The C# caller observes the new
    /// bytes after the synchronous call returns because the Swift side writes
    /// directly through the pinned address — no copy.
    public func fillBuffer(_ buffer: UnsafeMutableRawBufferPointer) -> Int32 {
        for i in 0..<buffer.count {
            buffer[i] = fillByte
        }
        return Int32(buffer.count)
    }

    /// Mixed read+write round-trip: adds `delta` to every byte (using
    /// wrapping addition so the test can assert exact post-mutation values
    /// without worrying about overflow traps), and returns the post-mutation
    /// sum. Verifies that Swift sees the same bytes the C# caller wrote AND
    /// that C# observes the new bytes after the call returns.
    public func incrementAndSum(_ buffer: UnsafeMutableRawBufferPointer) -> Int32 {
        var total: Int32 = 0
        for i in 0..<buffer.count {
            buffer[i] = buffer[i] &+ delta
            total &+= Int32(buffer[i])
        }
        return total
    }

    /// Two-buffer aliasing: writes 0x11 across `a`, then 0x22 across `b`. If
    /// the two parameters point at overlapping ranges of the same backing,
    /// the second write deterministically wins on the overlap. Returns the
    /// total byte count touched (a.count + b.count) so the caller can verify
    /// the (ptr, len) split landed correct lengths on both pins.
    public func writeAliasedSentinels(
        _ a: UnsafeMutableRawBufferPointer,
        _ b: UnsafeMutableRawBufferPointer
    ) -> Int32 {
        for i in 0..<a.count { a[i] = 0x11 }
        for i in 0..<b.count { b[i] = 0x22 }
        return Int32(a.count &+ b.count)
    }
}

/// Companion fixture covering the *constructor* wrapper path for an
/// UnsafeMutableRawBufferPointer parameter. Constructors take a different
/// emission route than instance methods (own @_cdecl wrapper, indirect-result
/// allocation, distinct parameter ordering for ObjC-rooted/static-helper
/// variants), so even with full instance-method coverage the init path can
/// regress independently. The captured values let C# tests assert that Swift
/// saw the expected bytes and length, and the writeback proves the pinned
/// buffer was the same one Swift mutated.
public struct UnsafeMutableRawBufferCtorHolder {
    public let bufferCount: Int32
    public let firstByteSeen: UInt8
    public let lastByteSeen: UInt8

    /// Snapshot the buffer's count and edge bytes, then fill the buffer with
    /// the sentinel 0x77. The constructor's writeback is observable to the
    /// C# caller because the sentinel is written through the pinned address
    /// before the synchronous call returns. The empty-span case stores a
    /// distinguishable 0xFE for the edge bytes — picking those out of the
    /// constructed value asserts Swift accepted count=0 cleanly without
    /// indexing past a null pointer.
    public init(_ buffer: UnsafeMutableRawBufferPointer) {
        self.bufferCount = Int32(buffer.count)
        self.firstByteSeen = buffer.count > 0 ? buffer[0] : 0xFE
        self.lastByteSeen = buffer.count > 0 ? buffer[buffer.count - 1] : 0xFE
        for i in 0..<buffer.count {
            buffer[i] = 0x77
        }
    }
}
