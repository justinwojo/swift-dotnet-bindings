// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - inout parameters on initializers and generic members
//
// An initializer's `inout` argument is written back exactly like a method's. On a generic parent
// the argument may be a concrete type or the parent's own generic parameter; either way the caller
// must observe the write.

public protocol InoutShape {
    var sides: Int { get }
}

public struct InoutSquare: InoutShape {
    public init() {}

    public var sides: Int { 4 }
}

public final class InoutTally {
    public var total: Int

    /// Reads `seed`, then advances it.
    public init(seed: inout Int) {
        total = seed
        seed += 1
    }

    public func measure<S: InoutShape>(_ shape: S, seen: inout Bool) -> Int {
        seen = true
        return shape.sides
    }
}

public struct InoutBox<T> {
    public var count: Int

    /// Reads `count`, then doubles it.
    public init(count: inout Int) {
        self.count = count
        count *= 2
    }

    public func fold(_ accumulator: inout Int) -> Int {
        accumulator += count
        return accumulator
    }
}

public struct InoutCell<T> {
    public var stored: T

    /// Stores `value` and replaces it with `replacement`.
    public init(swapping value: inout T, with replacement: T) {
        stored = value
        value = replacement
    }

    public mutating func exchange(_ other: inout T) {
        let previous = stored
        stored = other
        other = previous
    }
}
