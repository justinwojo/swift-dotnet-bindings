// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Multi-Type-Argument Bound Generics

/// Bound generic struct with two type parameters.
/// Tests BoundGenericsHandler with multiple type arguments — both resolved at call site.
public struct Pair<A, B> {
    public let first: A
    public let second: B
    public init(first: A, second: B) {
        self.first = first
        self.second = second
    }
}

// MARK: - Class Types for ISwiftObject-Compatible Bound Generics

/// Class types that the generator emits as ISwiftObject-conforming C# classes.
/// Using classes instead of primitives avoids the ISwiftObject constraint violation
/// in the generated Pair<A, B> C# type.
public class CoordinateRef {
    public let x: Int32
    public let y: Int32
    public init(x: Int32, y: Int32) { self.x = x; self.y = y }
}

public class LabelRef {
    public let text: String
    public init(text: String) { self.text = text }
}

// MARK: - Functions Using Pair with Concrete Class Types

/// Returns a Pair with two different class type arguments.
/// Tests multi-type-arg bound generic resolution at call site.
public func makeRefPair(_ coord: CoordinateRef, _ label: LabelRef) -> Pair<CoordinateRef, LabelRef> {
    return Pair(first: coord, second: label)
}

/// Method-level generic — may not be supported by the generator.
/// Included to verify graceful skip behavior for unresolved generic params.
public func makePairDescription<A, B>(_ pair: Pair<A, B>) -> String {
    return "Pair(\(pair.first), \(pair.second))"
}
