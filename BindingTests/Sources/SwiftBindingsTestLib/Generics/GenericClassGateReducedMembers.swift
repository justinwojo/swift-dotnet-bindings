// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import SwiftUI

// Exercises the pre-gate trailing-default rescue on members of a GENERIC class.
//
// Each member below has an unbindable trailing defaulted parameter, so the member gate drops the
// full member and the rescue emits a reduced overload whose wrapper calls the declaration with the
// kept arguments, letting Swift fill in the rest.
//
// A generic class's wrapper dispatches through a private protocol the class is extended to
// conform to. That protocol used to declare the REDUCED signature as its requirement, which the
// full declaration cannot witness (Swift does not use default arguments to satisfy a
// requirement), so the conformance failed and the wrapper was withdrawn. The class now implements
// a requirement of its own that forwards to the declaration.
public class GateReducedTally<Item> {
    public private(set) var total: Int32

    public init(start: Int32) {
        total = start
    }

    /// Reduced to `add(_:)`: the dropped `edges` defaults to empty, contributing nothing.
    public func add(_ amount: Int32, edges: [Edge] = []) -> Int32 {
        total += amount + Int32(edges.count)
        return total
    }

    /// The defaulted-flag-plus-defaulted-completion shape, reduced past the completion.
    public func reset(to value: Int32, animated: Bool = true, completion: @escaping () -> Void = {}) {
        total = animated ? value : -value
        completion()
    }
}

public enum GateReducedSeedError: Error {
    case negative
}

// Constructors of a FINAL generic class dispatch through a protocol on the class metatype. Its
// requirement cannot be the reduced `init` either, so it is a factory the class implements by
// calling the full initializer.
public final class GateReducedSeed<Item> {
    public let value: Int64

    /// Reduced to `init(value:)`.
    public init(value: Int32, edges: [Edge] = []) {
        self.value = Int64(value) + Int64(edges.count)
    }

    /// Throwing, reduced to `init(checked:)`.
    public init(checked: Int64, edges: [Edge] = []) throws {
        if checked < 0 { throw GateReducedSeedError.negative }
        value = checked + Int64(edges.count)
    }
}
