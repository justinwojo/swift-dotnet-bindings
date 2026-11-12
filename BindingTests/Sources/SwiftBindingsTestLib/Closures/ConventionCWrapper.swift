// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - @convention(c) callbacks through the @_cdecl wrapper

public enum CCallbackLedgerError: Error {
    case negative(Int64)
}

/// Instance members taking `@convention(c)` callbacks. A C function pointer is one word with no
/// context, so the `@_cdecl` wrapper declares the parameter with its own `@convention(c)` type and
/// hands it to the member unchanged, while self arrives as an ordinary pointer. Between them the
/// members cover what the wrapper has to get right around a C callback: an error beside a scalar
/// result, a 40-byte frozen result written through the result buffer, an owned class reference in
/// each direction, a Swift closure beside a C callback, an optional C callback, and a callback that
/// re-enters the member it was handed to.
public final class CCallbackLedger {
    public private(set) var total: Int64

    public init(total: Int64) {
        self.total = total
    }

    /// Adds the mapped delta to the total and returns it. A negative mapped delta throws and leaves
    /// the total as it was, so the next call shows whether the receiver survived the error.
    public func accumulate(_ delta: Int64, through map: @convention(c) (Int64) -> Int64) throws -> Int64 {
        let mapped = map(delta)
        if mapped < 0 {
            throw CCallbackLedgerError.negative(mapped)
        }
        total += mapped
        return total
    }

    /// A 40-byte frozen result built from self, two arguments and the callback. Every field reads
    /// back a distinct input, so a result written to the wrong place cannot come back right.
    public func spread(_ x: Int64, _ y: Int64, through map: @convention(c) (Int64) -> Int64) -> LargeScalarStruct {
        LargeScalarStruct(a: total &+ map(x), b: total &+ map(y), c: map(x &+ y), d: x, e: y)
    }

    /// A new tracked object derived from the one passed in, so an owned class reference crosses
    /// the wrapper in each direction.
    public func derive(_ object: TrackedObject, through map: @convention(c) (Int64) -> Int64) -> TrackedObject {
        TrackedObject(objectId: Int32(truncatingIfNeeded: map(Int64(object.objectId))), label: object.label + "+")
    }

    /// A Swift closure and a C callback in one signature: the Swift closure is two words and needs
    /// the wrapper's adapter, the C callback is one and needs none. Returns
    /// `adjust(x) * 1000 + map(x)` so each callback's result is told apart.
    public func blend(_ x: Int64, adjust: (Int64) -> Int64, through map: @convention(c) (Int64) -> Int64) -> Int64 {
        adjust(x) &* 1000 &+ map(x)
    }

    /// An optional C callback: the mapped value when one is passed, `-1` when none is.
    public func mapIfPresent(_ x: Int64, through map: (@convention(c) (Int64) -> Int64)?) -> Int64 {
        guard let map else { return -1 }
        return map(x)
    }

    /// Calls the callback twice and returns the sum. A callback that calls `applyTwice` again on
    /// the same receiver while the outer call is still running checks that the outer call's second
    /// invocation still reaches the outer callback.
    public func applyTwice(_ x: Int64, through map: @convention(c) (Int64) -> Int64) -> Int64 {
        let first = map(x)
        let second = map(x)
        return first &+ second
    }
}

/// A class initializer taking a C callback: the constructor wrapper passes the function pointer
/// through the same way the method wrapper does.
public final class CCallbackSeeded {
    public let value: Int64

    public init(seed: Int64, through map: @convention(c) (Int64) -> Int64) {
        value = map(seed)
    }
}

/// A frozen struct initializer taking a C callback, whose value comes back through the result
/// buffer rather than as a returned reference.
@frozen
public struct CCallbackSeed {
    public var value: Int64

    public init(seed: Int64, through map: @convention(c) (Int64) -> Int64) {
        value = map(seed)
    }
}

/// A resilient struct whose members take C callbacks: an initializer, an instance member that reads
/// self through the pointer the wrapper receives, a static member with no receiver, and a member with
/// its own generic parameter, whose metadata travels beside the C callback.
public struct CCallbackTally {
    public var count: Int64

    public init(seed: Int64, through map: @convention(c) (Int64) -> Int64) {
        count = map(seed)
    }

    /// `count * map(x)`.
    public func scaled(_ x: Int64, through map: @convention(c) (Int64) -> Int64) -> Int64 {
        count &* map(x)
    }

    /// `map(x, y)`.
    public static func combine(_ x: Int64, _ y: Int64, through map: @convention(c) (Int64, Int64) -> Int64) -> Int64 {
        map(x, y)
    }

    /// `size(T) * 1000 + count + map(x)`: the size reads back which type's metadata arrived.
    public func weigh<T>(_ value: T, _ x: Int64, through map: @convention(c) (Int64) -> Int64) -> Int64 {
        Int64(MemoryLayout<T>.size) &* 1000 &+ count &+ map(x)
    }
}
