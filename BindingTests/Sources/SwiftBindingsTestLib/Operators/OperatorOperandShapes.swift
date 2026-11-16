// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Operators whose operands are not all the parent type, beside positive controls that are.
//
// A non-frozen struct or class operator calls Swift directly. An Optional wider than one word
// (Int?, Double?, String?) crosses such a call in a by-value carrier, and the operator body has no
// conversion to or from it, so those operators are refused. The `*` with a plain Int operand on the
// same types still binds.
//
// A frozen struct's operators go through a @_cdecl wrapper written for an operator over the parent
// type alone: it reads every operand as the parent and names the result without generic arguments.
// The heterogeneous overload `+ (OperandShapeFrozen, Int)` beside the homogeneous one would compile
// into a call of the homogeneous overload on an Int's bytes; it is refused, with the wide-optional
// ones, and the homogeneous `+` and `==` bind.
//
// C# also needs the partner of a comparison operator, which the generator synthesizes from an
// emitted one. OperandShapeValue and OperandShapeFrozen declare a refused `< (_, Int?)` before the
// homogeneous `<`; the synthesized `>` must come from the homogeneous overload, not the refused one.

public struct OperandShapeValue {
    public var base: Int
    public init(base: Int) { self.base = base }
    public static func + (a: OperandShapeValue, b: Int?) -> Int { a.base + (b ?? -1) }
    public static func - (a: OperandShapeValue, b: String?) -> Int { a.base - (b?.count ?? -1000) }
    public static func / (a: OperandShapeValue, b: Int) -> Int? { b == 0 ? nil : a.base / b }
    public static func * (a: OperandShapeValue, b: Int) -> Int { a.base * b }
    public static func < (a: OperandShapeValue, b: Int?) -> Bool { a.base < (b ?? 0) }
    public static func < (a: OperandShapeValue, b: OperandShapeValue) -> Bool { a.base < b.base }
}

public final class OperandShapeBox {
    public var base: Int
    public init(base: Int) { self.base = base }
    public static func + (a: OperandShapeBox, b: Double?) -> Double { Double(a.base) + (b ?? -1) }
    public static func * (a: OperandShapeBox, b: Int) -> Int { a.base * b }
}

@frozen public struct OperandShapeFrozen: Equatable {
    public var base: Int
    public init(base: Int) { self.base = base }
    public static func + (a: OperandShapeFrozen, b: OperandShapeFrozen) -> OperandShapeFrozen {
        OperandShapeFrozen(base: a.base + b.base)
    }
    public static func + (a: OperandShapeFrozen, b: Int) -> OperandShapeFrozen {
        OperandShapeFrozen(base: a.base * 1000 + b)
    }
    public static func - (a: OperandShapeFrozen, b: Int?) -> Int { a.base - (b ?? -1) }
    public static func / (a: OperandShapeFrozen, b: OperandShapeFrozen) -> Int? {
        b.base == 0 ? nil : a.base / b.base
    }
    public static func < (a: OperandShapeFrozen, b: Int?) -> Bool { a.base < (b ?? 0) }
    public static func < (a: OperandShapeFrozen, b: OperandShapeFrozen) -> Bool { a.base < b.base }
    public static func == (a: OperandShapeFrozen, b: OperandShapeFrozen) -> Bool { a.base == b.base }
}

// Several overloads of one operator symbol over different operands. C# pairs operators per operand
// list, so each `==` needs its own `!=` and each `<` its own `>`. The heterogeneous `<` pair is
// declared in both directions, which is what a swapped `>` over either operand order calls.
// OperandShapeOverloads is not Equatable: its `==` overloads are the whole equality surface.
public struct OperandShapeOverloads {
    public var base: Int
    public init(base: Int) { self.base = base }
    public static func == (a: OperandShapeOverloads, b: OperandShapeOverloads) -> Bool { a.base == b.base }
    public static func == (a: OperandShapeOverloads, b: Int) -> Bool { a.base == b }
    public static func < (a: OperandShapeOverloads, b: OperandShapeOverloads) -> Bool { a.base < b.base }
    public static func < (a: OperandShapeOverloads, b: Int) -> Bool { a.base < b }
    public static func < (a: Int, b: OperandShapeOverloads) -> Bool { a < b.base }
}
