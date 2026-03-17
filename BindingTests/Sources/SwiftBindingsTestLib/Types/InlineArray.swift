// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - InlineArray (Swift 6.2)
// Tests: InlineArray fixed-size inline type in function signatures
// Expected C#: Fixed-size buffer or ValueTuple mapping
// Limitation: InlineArray is not yet supported by the generator
// Note: Requires Swift 6.2+ and may need -enable-experimental-feature flag

#if swift(>=6.2)

/// Returns an InlineArray of 3 Int32 values.
public func makeThreeInts() -> InlineArray<3, Int32> {
    var result = InlineArray<3, Int32>(repeating: 0)
    result[0] = 10
    result[1] = 20
    result[2] = 30
    return result
}

/// Sums the elements of a 4-element InlineArray.
public func sumFourInts(_ values: InlineArray<4, Int32>) -> Int32 {
    var total: Int32 = 0
    for i in 0..<4 {
        total += values[i]
    }
    return total
}

/// A struct containing an InlineArray property.
public struct FixedBuffer {
    public var elements: InlineArray<4, Int32>

    public init() {
        self.elements = InlineArray<4, Int32>(repeating: 0)
    }

    /// Returns the sum of all elements.
    public func sum() -> Int32 {
        var total: Int32 = 0
        for i in 0..<4 {
            total += elements[i]
        }
        return total
    }
}

/// Creates a FixedBuffer with default values.
public func createFixedBuffer() -> FixedBuffer {
    return FixedBuffer()
}

#else

// InlineArray requires Swift 6.2+.
// This file is intentionally empty on earlier compiler versions.

#endif
