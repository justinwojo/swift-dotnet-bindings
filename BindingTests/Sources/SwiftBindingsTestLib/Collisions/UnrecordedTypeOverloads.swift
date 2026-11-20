// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Overloads that differ only at a closure or existential position
// Pattern: two overloads with the same labels whose only difference is a parameter the type
// database has no record for: an existential (`any P`) in one and a closure in the other. Both
// once keyed as the same unrecorded type, so the second was dropped as a duplicate although the C#
// signatures (`IOverloadMeasure` and `Func<nint>`) do not collide. Their internal parameter names
// also differ, and the label-only interface key once gave the first overload the second's names.

/// A protocol the existential overloads take.
public protocol OverloadMeasure {
    func measure() -> Int
}

/// A Swift conformer of `OverloadMeasure`.
public final class OverloadRuler: OverloadMeasure {
    public let length: Int

    public init(length: Int) {
        self.length = length
    }

    public func measure() -> Int {
        length
    }
}

/// Two `combine(_:_:)` overloads that share their labels.
public final class OverloadCombiner {
    public init() {}

    /// The measure of `shape` scaled by `factor`.
    public func combine(_ factor: Int, _ shape: any OverloadMeasure) -> Int {
        factor * shape.measure()
    }

    /// `offset` added to what `source` returns.
    public func combine(_ offset: Int, _ source: @escaping () -> Int) -> Int {
        offset + source()
    }
}

/// The free-function counterparts of `OverloadCombiner.combine(_:_:)`.
public func overloadCombine(_ factor: Int, _ shape: any OverloadMeasure) -> Int {
    factor * shape.measure()
}

public func overloadCombine(_ offset: Int, _ source: @escaping () -> Int) -> Int {
    offset + source()
}
