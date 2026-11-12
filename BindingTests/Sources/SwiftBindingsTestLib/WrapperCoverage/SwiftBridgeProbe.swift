// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Native Swift bridge probe

/// Instance members the binding calls through a direct Swift-convention import: the
/// `@convention(c)` parameter keeps each one off the `@_cdecl` wrapper, so with lowering evidence
/// every one reaches Swift through a native bridge that loads self, the error slot and the indirect
/// result into their Swift registers. Between them they cover the register shapes a bridge moves:
/// self with an error and a scalar result, self with an indirect result and ordinary arguments, and
/// self with an owned class argument and result. The mapping callback lets a test interleave
/// managed code inside the call, so the callee-saved registers the bridge borrows are live on both
/// sides of it.
public final class SwiftBridgeProbe {
    public private(set) var total: Int64

    public init(total: Int64) {
        self.total = total
    }

    /// Adds the mapped delta to the running total and returns it. A negative mapped delta throws
    /// and leaves the total as it was, so a later call shows whether the receiver survived the error.
    public func accumulate(_ delta: Int64, through map: @convention(c) (Int64) -> Int64) throws -> Int64 {
        let mapped = map(delta)
        if mapped < 0 {
            throw SwiftBridgeProbeError.negative(mapped)
        }
        total += mapped
        return total
    }

    /// The 40-byte indirect result built from self and two ordinary arguments: the shape of a
    /// mutating method that returns a large struct, with self in a class instead of a buffer.
    public func combine(_ x: Int64, _ y: Int64, through map: @convention(c) (Int64) -> Int64) -> LargeScalarStruct {
        LargeScalarStruct(a: total &+ map(x), b: total &+ map(y), c: map(x &+ y), d: x, e: y)
    }

    /// A new tracked object derived from the one passed in: an owned class reference crosses the
    /// bridge in each direction, so the allocation counters show whether either side lost or kept
    /// a reference it should not have.
    public func derive(_ object: TrackedObject, through map: @convention(c) (Int64) -> Int64) -> TrackedObject {
        TrackedObject(objectId: Int32(truncatingIfNeeded: map(Int64(object.objectId))), label: object.label + "+")
    }
}

public enum SwiftBridgeProbeError: Error {
    case negative(Int64)
}
