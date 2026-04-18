// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - UnsafeRawBufferParam
//
// Exercises UnsafeRawBufferPointer parameter marshalling. The generator splits
// the Swift parameter into (ptr, len) at the @_cdecl C ABI boundary and
// bridges to ReadOnlySpan<byte> on the C# side (see CdeclParamMapper.Map and
// MarshalledType.RawBufferPtr/RawBufferLen). An empty span pins to a null
// pointer, so the Swift ptr parameter is UnsafeRawPointer?; the
// UnsafeRawBufferPointer(start:count:) initializer accepts that directly.

/// Struct exercising UnsafeRawBufferPointer parameter round-trips. Two methods
/// cover the main observables: the buffer length (for empty-span handling) and
/// the byte contents (for pointer correctness).
public struct UnsafeRawBufferHolder {
    public let scale: Int32

    public init(scale: Int32) {
        self.scale = scale
    }

    /// Unrelated method — keeps coverage on the enclosing type alongside the
    /// raw-buffer methods so a regression that drops the type is still caught.
    public func multiplier(_ value: Int32) -> Int32 {
        return value * scale
    }

    /// Returns the byte count of the incoming buffer. Proves the length half
    /// of the split (ptr, len) ABI round-trips, including the empty-span case
    /// where ptr is null.
    public func readBuffer(_ buffer: UnsafeRawBufferPointer) -> Int32 {
        return Int32(buffer.count)
    }

    /// Sums the bytes of the incoming buffer as Int32. Proves the pointer half
    /// round-trips correctly — the Swift side must dereference the same bytes
    /// the C# caller pinned. Uses Int32 accumulation so callers can verify the
    /// exact byte pattern they sent.
    public func sumBytes(_ buffer: UnsafeRawBufferPointer) -> Int32 {
        var total: Int32 = 0
        for b in buffer {
            total &+= Int32(b)
        }
        return total
    }
}
