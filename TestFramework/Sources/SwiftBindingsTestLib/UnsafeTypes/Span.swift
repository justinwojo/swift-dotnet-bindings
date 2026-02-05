// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Span Types (Swift 6.2)
// Tests: Span<T> and RawSpan safe buffer views
// Expected C#: Span<T> / ReadOnlySpan<T> mapping
// Limitation: Span types are not yet supported by the generator
// Note: Span is non-escapable; may have library-evolution limitations

#if swift(>=6.2)

/// Sums the elements of an Int32 Span.
public func sumSpan(_ span: Span<Int32>) -> Int32 {
    var total: Int32 = 0
    for element in span {
        total += element
    }
    return total
}

/// Returns the byte count of a RawSpan.
public func rawSpanByteCount(_ span: RawSpan) -> Int {
    return span.byteCount
}

#else

// Span and RawSpan require Swift 6.2+.
// This file is intentionally empty on earlier compiler versions.

#endif
