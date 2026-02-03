// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Basic Tuple Functions

/// Returns a 2-element tuple.
public func makePair(_ a: Int32, _ b: Int32) -> (Int32, Int32) {
    return (a, b)
}

/// Returns a 3-element tuple.
public func makeTriple(_ a: Int32, _ b: Int32, _ c: Int32) -> (Int32, Int32, Int32) {
    return (a, b, c)
}

/// Returns a 7-element tuple (maximum for C# ValueTuple without nesting).
public func makeSeptuple(
    _ a: Int32, _ b: Int32, _ c: Int32, _ d: Int32,
    _ e: Int32, _ f: Int32, _ g: Int32
) -> (Int32, Int32, Int32, Int32, Int32, Int32, Int32) {
    return (a, b, c, d, e, f, g)
}

/// Accepts a tuple parameter and returns a scalar.
public func sumPair(_ pair: (Int32, Int32)) -> Int32 {
    return pair.0 + pair.1
}

/// Returns a tuple of mixed types.
public func makeMixedPair(_ intVal: Int32, _ boolVal: Bool) -> (Int32, Bool) {
    return (intVal, boolVal)
}
