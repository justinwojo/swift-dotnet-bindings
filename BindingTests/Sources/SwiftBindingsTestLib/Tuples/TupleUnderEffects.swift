// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Tuple returns under effects (ABI Coverage Grid — tuple corner)
//
// The existing tuple fixtures cover plain and (non-throwing) async tuple returns. These close
// the confirmed gaps the grid corner enumerates: a tuple return under `throws`, under
// `async throws`, and a tuple whose element list contains an Optional. Each carries a
// value-round-trip oracle so the grid asserts behaviour, not just "didn't crash".

public enum TupleEffectError: Error {
    case divideByZero
}

/// Sync throwing function returning an all-primitive tuple. Throws on divide-by-zero,
/// otherwise returns (quotient, remainder). Exercises the throws × tuple-return cell.
public func divmodThrowing(a: Int32, b: Int32) throws -> (quotient: Int32, remainder: Int32) {
    if b == 0 { throw TupleEffectError.divideByZero }
    return (quotient: a / b, remainder: a % b)
}

/// Async throwing function returning an all-primitive tuple. Same oracle as the sync variant,
/// but exercises the async × throws × tuple-return triple.
public func divmodThrowingAsync(a: Int32, b: Int32) async throws -> (quotient: Int32, remainder: Int32) {
    if b == 0 { throw TupleEffectError.divideByZero }
    return (quotient: a / b, remainder: a % b)
}

/// Returns a tuple whose second element is Optional — `(lower, upper?)`. For a non-empty
/// span returns (lo, hi); for an empty span (lo == hi) returns (lo, nil). Exercises the
/// contains-optional element-mix cell on a plain (non-effectful) tuple return.
public func spanBounds(lo: Int32, hi: Int32) -> (lower: Int32, upper: Int32?) {
    if lo == hi {
        return (lower: lo, upper: nil)
    }
    return (lower: lo, upper: hi)
}

/// Returns a tuple whose first element is a frozen blittable struct — `(FrozenPoint, Int32)`.
/// Exercises the mixed primitive+struct element-mix cell: a value-type struct embedded in a
/// tuple return must marshal back field-by-field alongside the trailing primitive.
public func makePointWithTag(x: Double, y: Double, tag: Int32) -> (point: FrozenPoint, tag: Int32) {
    return (point: FrozenPoint(x: x, y: y), tag: tag)
}
