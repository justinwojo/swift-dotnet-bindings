// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Buffer-mode type-metadata accessor coverage
//
// Swift's type-metadata accessor (`Ma` symbol) switches to the indirect-buffer
// ABI when the total number of register-passed metadata + protocol-witness-table
// arguments exceeds three. Below that
// threshold the accessor takes `(request, arg0, ..., argN)` in registers.
// Above the threshold it takes `(request, const void * const * parameters)` —
// a single pointer to a contiguous buffer of `IntPtr`-sized slots.
//
// These fixtures exercise both dimensions that can trip the threshold:
//  - 4 unconstrained generic parameters → 4 metadata args, 0 PWTs
//  - 2 generic parameters each constrained on Describable → 2 metadata + 2 PWTs
// Both cross the 3-arg boundary. A thin-mode mismatch here PAC-traps on
// arm64e at first use, so the paired C# runtime tests call
// `SwiftObjectHelper<...>.GetTypeMetadata()` on a closed specialization to
// force the accessor to fire end-to-end.
//
// Canonical upstream example for this pattern: MusicKit's generic library
// view types whose metadata accessor takes more than three args.

import Foundation

// MARK: - 4 unconstrained generic params → 4 metadata args (buffer mode)

public struct BufferModeQuad<A, B, C, D> {
    public let first: A
    public let second: B
    public let third: C
    public let fourth: D

    public init(first: A, second: B, third: C, fourth: D) {
        self.first = first
        self.second = second
        self.third = third
        self.fourth = fourth
    }
}

// MARK: - 2 Describable-constrained params → 2 metadata + 2 PWTs (buffer mode)
//
// Total = 2 metadata + 2 PWTs = 4 > 3 → indirect-buffer ABI. Exercises the
// PWT side of the accessor packing path in addition to metadata.

public struct BufferModeDescribablePair<K: Describable, V: Describable> {
    public let first: K
    public let second: V

    public init(first: K, second: V) {
        self.first = first
        self.second = second
    }

    public func combinedDescription() -> String {
        return "\(first.describe()) | \(second.describe())"
    }
}
