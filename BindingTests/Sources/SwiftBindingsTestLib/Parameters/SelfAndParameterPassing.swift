// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Self convention and parameter passing, per (member, type)
//
// How Swift takes self and each argument depends on the member as well as the type. The same
// small frozen struct reaches a getter by value, a setter or mutating method through the address
// of the caller's storage, and a consuming method as a +1 value. A frozen struct of more than four
// register pieces reaches even a nonmutating member through an address. A struct holding a class
// reference is passed guaranteed, owned or inout, and the class's deinit counts every destruction
// per instance, so a missing or an extra release on any of those paths shows up as a count.

/// Two words: Swift passes it to a nonmutating member as lowered pieces.
@frozen
public struct PassingPair {
    public var first: Int
    public var second: Int

    public init(first: Int, second: Int) {
        self.first = first
        self.second = second
    }

    /// Getter: self by value.
    public var weighted: Int { first &* 1000 &+ second }

    /// Nonmutating method: self by value, after an ordinary argument.
    public func weightedPlus(_ k: Int) -> Int { weighted &+ k }

    /// Setter: self inout, `newValue` owned.
    public var total: Int {
        get { first &+ second }
        set { second = newValue &- first }
    }

    /// Mutating method: self inout.
    public mutating func shift(by k: Int) {
        first &+= k
        second &-= k
    }

    /// Consuming method: self owned.
    public consuming func drain(_ k: Int) -> Int { first &* 1000 &+ second &+ k }
}

/// Five words: more than four pieces, so Swift passes self through an address even to a
/// nonmutating member.
@frozen
public struct PassingWide {
    public var a: Int
    public var b: Int
    public var c: Int
    public var d: Int
    public var e: Int

    public init(a: Int, b: Int, c: Int, d: Int, e: Int) {
        self.a = a
        self.b = b
        self.c = c
        self.d = d
        self.e = e
    }

    public var checksum: Int { a &+ b &* 10 &+ c &* 100 &+ d &* 1000 &+ e &* 10000 }

    public func checksumPlus(_ k: Int) -> Int { checksum &+ k }

    public var head: Int {
        get { a }
        set { a = newValue }
    }

    public mutating func scale(by k: Int) {
        a &*= k
        b &*= k
        c &*= k
        d &*= k
        e &*= k
    }

    /// Consuming method: self owned, through an address.
    public consuming func drain(_ k: Int) -> Int { checksum &+ k }
}

/// Five floating-point pieces in 24 bytes: indirect by piece count, not by size.
@frozen
public struct PassingMixedFloats {
    public var x: Float
    public var y: Float
    public var z: Float
    public var w: Float
    public var t: Double

    public init(x: Float, y: Float, z: Float, w: Float, t: Double) {
        self.x = x
        self.y = y
        self.z = z
        self.w = w
        self.t = t
    }

    public var sum: Double { Double(x) + Double(y) * 10 + Double(z) * 100 + Double(w) * 1000 + t * 10000 }

    public mutating func bump() {
        x += 1
        y += 1
        z += 1
        w += 1
        t += 1
    }
}

// MARK: Non-trivial arguments with destruction counts

private nonisolated(unsafe) var passingTokenDeaths: [Int: Int] = [:]

/// How many times the token with this id has been destroyed. Ids are chosen per test.
public func passingTokenDeathCount(_ id: Int) -> Int { passingTokenDeaths[id, default: 0] }

/// A class whose deinit counts per instance id.
public final class PassingToken {
    public let id: Int
    public init(id: Int) { self.id = id }
    deinit { passingTokenDeaths[id, default: 0] += 1 }
}

/// Two words, one a class reference: loadable and non-trivial.
@frozen
public struct PassingCarrier {
    public var token: PassingToken
    public var tag: Int

    /// Initializer parameters are owned.
    public init(token: PassingToken, tag: Int) {
        self.token = token
        self.tag = tag
    }

    /// Nonmutating: self borrowed.
    public func describe() -> Int { token.id &* 1000 &+ tag }

    /// Mutating: self inout.
    public mutating func retag(_ newTag: Int) { tag = newTag }

    /// Mutating, replacing the reference: the old token loses this copy's reference.
    public mutating func swapToken(_ replacement: PassingToken) { token = replacement }

    /// Consuming: self owned, destroyed by the callee.
    public consuming func finish() -> Int { token.id &* 1000 &+ tag }
}

/// Guaranteed by-value argument: the callee borrows it.
public func passingCarrierBorrow(_ carrier: PassingCarrier) -> Int {
    carrier.token.id &* 1000 &+ carrier.tag
}

/// Explicitly owned by-value argument: the callee consumes its +1 copy.
public func passingCarrierConsume(_ carrier: __owned PassingCarrier) -> Int {
    carrier.token.id &* 1000 &+ carrier.tag
}

/// Inout argument: the mutation is visible to the caller.
public func passingCarrierRetag(_ carrier: inout PassingCarrier, _ tag: Int) {
    carrier.tag = tag
}

/// Inout argument whose reference is replaced.
public func passingCarrierSwapToken(_ carrier: inout PassingCarrier, _ replacement: PassingToken) {
    carrier.token = replacement
}

/// Holds a carrier: its initializer parameter and its setter's `newValue` are owned.
public final class PassingCarrierHolder {
    public var carrier: PassingCarrier

    public init(carrier: PassingCarrier) {
        self.carrier = carrier
    }

    public var carrierTokenID: Int { carrier.token.id }
}

/// A generic class whose getter returns an array of its parameter by value: the caller owns the
/// returned array's +1, and the array keeps every token alive until that reference is gone.
public final class PassingTokenBox<T> {
    public var items: [T]

    public init(_ item: T) {
        items = [item]
    }
}
