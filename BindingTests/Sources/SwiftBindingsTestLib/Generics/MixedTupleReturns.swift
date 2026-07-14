// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Mixed generic tuple returns (fail-closed skip matrix)
//
// A generic tuple return that mixes bare generic parameters with concrete or
// bound-generic elements lowers element-wise in Swift's ABI: address-only
// elements become leading indirect-result pointers (x0, x1, ...) while loadable
// elements return direct in result registers. The generator cannot express that
// split through a direct-symbol P/Invoke, so these members must be SKIPPED
// fail-closed (never emitted with the single-x8 indirect-result fallback, whose
// register assignment is wrong for every one of these shapes). The runtime test
// asserts the members are absent from the binding; the bare-only control below
// must keep emitting and round-tripping through the uniform multi-@out branch.

/// Mixed: bare T + trailing loadable Int → SIL (@out T, Int).
public func mixedReturnPairTI<T>(_ value: T) -> (T, Int) {
    return (value, 42)
}

/// Mixed: leading loadable Int + bare T → SIL (Int, @out T).
public func mixedReturnPairIT<T>(_ value: T) -> (Int, T) {
    return (7, value)
}

/// Mixed: bare T + bound generic Array<T> (loadable, returns direct as one ref).
public func mixedReturnPairTArray<T>(_ value: T) -> (T, [T]) {
    return (value, [value, value])
}

/// Mixed: bound generic Array<T> first, bare T second.
public func mixedReturnPairArrayT<T>(_ value: T) -> ([T], T) {
    return ([value], value)
}

/// Mixed: bare T + Optional<T> (both address-only, but not uniformly bare).
public func mixedReturnPairTOptional<T>(_ value: T) -> (T, T?) {
    return (value, value)
}

/// Three-element mix: @out T + direct Array ref + direct Int → SIL { ptr, i64 } direct pair.
public func mixedReturnTriple<T>(_ value: T) -> (T, [T], Int) {
    return (value, [value], 1)
}

/// Throwing variant — the error convention must not re-admit the mixed shape.
public func mixedReturnThrowing<T>(_ value: T, shouldFail: Bool) throws -> (T, Int) {
    if shouldFail { throw MixedTupleError.failed }
    return (value, 13)
}

public enum MixedTupleError: Error {
    case failed
}

// MARK: - Mixed shapes on type members

/// Generic method on a non-generic struct: same direct-symbol path as free functions.
public struct MixedTupleHost {
    public var tag: Int32

    public init(tag: Int32) {
        self.tag = tag
    }

    /// Control member: must keep emitting (no tuple involved).
    public func getTag() -> Int32 {
        return tag
    }

    /// Mixed instance method — must be skipped.
    public func mixedMemberPair<T>(_ value: T) -> (T, Int) {
        return (value, Int(tag))
    }

    /// Mixed static method — must be skipped.
    public static func mixedStaticPair<T>(_ value: T) -> (T, Int) {
        return (value, 3)
    }
}

// MARK: - Bare-only control

/// Control: uniformly bare generic tuple, returned via one @out register per
/// element. Must keep emitting and round-tripping (same branch as `pair`).
public func mixedControlBarePair<T, U>(_ first: T, _ second: U) -> (T, U) {
    return (first, second)
}
