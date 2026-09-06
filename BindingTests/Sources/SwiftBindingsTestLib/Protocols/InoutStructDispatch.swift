// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Inout Non-Frozen Struct Protocol Dispatch

/// Protocol whose requirement takes an `inout` non-frozen struct parameter.
///
/// Mirrors the GRDB shape (a row/statement writer that mutates a non-frozen value in place):
/// the generated C# interface must declare the parameter with `ref` so that BOTH a generated
/// concrete conformer AND a C# reverse-dispatch conformer can satisfy the contract. The
/// regression this guards: the interface omitted `ref` while concrete conformers emitted it,
/// so every conformer failed to implement the interface member → CS0535.
public protocol PointMutator: AnyObject {
    func mutate(_ point: inout NonFrozenPoint)
}

/// Concrete Swift conformer for forward dispatch (C# calls Swift through the interface).
/// Shifts the point in place by a fixed delta.
public final class OriginShifter: PointMutator {
    public let dx: Double
    public let dy: Double

    public init(dx: Double, dy: Double) {
        self.dx = dx
        self.dy = dy
    }

    public func mutate(_ point: inout NonFrozenPoint) {
        point.x += dx
        point.y += dy
    }
}

/// Drives a (possibly C#-implemented) mutator over a Swift-owned non-frozen point for reverse
/// dispatch (Swift calls back into a C# conformer through the proxy). Returns the mutated point
/// so the caller can verify the conformer's in-place writeback survived the inout round-trip.
public func driveMutator(_ mutator: any PointMutator, startX: Double, startY: Double) -> NonFrozenPoint {
    var point = NonFrozenPoint(x: startX, y: startY)
    mutator.mutate(&point)
    return point
}

// MARK: - Inout native-int requirement beside a narrowable sibling
//
// A protocol requirement that pairs an `inout Int` with a plain `Int` is where the interface's
// convenience overload has to make a distinction the concrete-type path already makes: the plain
// parameter may be offered at 32 bits, the `inout` one may not. It is both input and output, so a
// narrowed view would truncate the value on the way back out, and the forwarder cannot pass a cast
// rvalue by reference at all. Getting that wrong yields a default implementation that drops `ref`
// against a `ref`-taking requirement, and the whole binding stops compiling.

/// Advances a caller-owned cursor by a step — the `inout Int` + `Int` pairing.
public protocol CursorAdvancer: AnyObject {
    func advance(_ cursor: inout Int, by step: Int)
}

/// Swift conformer for forward dispatch: adds the step to the cursor in place.
public final class SteppingAdvancer: CursorAdvancer {
    public init() {}

    public func advance(_ cursor: inout Int, by step: Int) {
        cursor += step
    }
}
