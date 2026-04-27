// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Buffer-Pointer Closure Parameters
//
// Reproduces the RealityFoundation.LowLevelBuffer pattern: an instance method
// that accepts a non-escaping closure whose single argument is an
// (UnsafeRawBufferPointer) / (UnsafeMutableRawBufferPointer). The Swift cdecl
// wrapper has to decompose the buffer pointer into (baseAddress, count) at the
// @convention(c) callback boundary AND reconcile the non-escaping attribute,
// because the let-bound Swift adapter is implicitly @escaping.

/// Holds a fixed byte payload exposed via withUnsafeBytes-style accessors.
public final class BufferProvider {
    private var bytes: [UInt8]

    public init(bytes: [UInt8]) {
        self.bytes = bytes
    }

    /// Non-escaping read-only buffer-pointer closure (the Bug #4 shape).
    public func withUnsafeBytes(_ body: (UnsafeRawBufferPointer) -> Void) {
        bytes.withUnsafeBytes { raw in
            body(raw)
        }
    }

    /// Non-escaping mutable buffer-pointer closure. Bytes mutated by the
    /// closure are observable in subsequent calls.
    public func withUnsafeMutableBytes(_ body: (UnsafeMutableRawBufferPointer) -> Void) {
        bytes.withUnsafeMutableBytes { raw in
            body(raw)
        }
    }

    /// Returns the current byte at the given index — used to verify writes.
    public func byte(at index: Int32) -> UInt8 {
        return bytes[Int(index)]
    }

    /// Returns the total payload count.
    public func count() -> Int32 {
        return Int32(bytes.count)
    }

    /// Throwing buffer-pointer closure — exercises the throwing emitter path.
    public func withUnsafeBytesThrowing(_ body: (UnsafeRawBufferPointer) throws -> Void) throws {
        try bytes.withUnsafeBytes { raw in
            try body(raw)
        }
    }

    /// Buffer-pointer closure with non-frozen struct return — exercises the indirect-return
    /// emitter path (NonFrozenPoint requires indirect return marshalling, so the wrapper has
    /// to thread the indirect-result pointer through the @convention(c) callback while also
    /// splitting the UnsafeRawBufferPointer arg into (baseAddress, count)).
    public func withUnsafeBytesIndirectReturn(_ body: (UnsafeRawBufferPointer) -> NonFrozenPoint) -> NonFrozenPoint {
        return bytes.withUnsafeBytes { raw in
            return body(raw)
        }
    }

    /// Returns the bit pattern of a retained `BufferProviderError.forced` boxed as
    /// `Swift.Error`. Tests use this to construct a non-zero `SwiftError` via the
    /// public `SwiftError(void*)` ctor and feed it into a `FromFailure(...)` result
    /// from a throwing closure. The retain is balanced by the throwing-method
    /// machinery's `SBW_ReleaseError` once the error surfaces back to C#.
    public static func makeRetainedTestErrorPtr() -> Int64 {
        let err: any Error = BufferProviderError.forced
        let raw = Unmanaged.passRetained(err as AnyObject).toOpaque()
        return Int64(Int(bitPattern: raw))
    }
}

public enum BufferProviderError: Error {
    case forced
}
