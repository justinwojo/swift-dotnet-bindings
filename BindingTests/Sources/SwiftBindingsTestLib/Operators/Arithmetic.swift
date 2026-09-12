// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Arithmetic Operators

/// Frozen struct for testing arithmetic operator emission.
@frozen
public struct ArithmeticValue {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public static func + (lhs: ArithmeticValue, rhs: ArithmeticValue) -> ArithmeticValue {
        return ArithmeticValue(value: lhs.value + rhs.value)
    }

    public static func - (lhs: ArithmeticValue, rhs: ArithmeticValue) -> ArithmeticValue {
        return ArithmeticValue(value: lhs.value - rhs.value)
    }

    public static func * (lhs: ArithmeticValue, rhs: ArithmeticValue) -> ArithmeticValue {
        return ArithmeticValue(value: lhs.value * rhs.value)
    }

    public static func / (lhs: ArithmeticValue, rhs: ArithmeticValue) -> ArithmeticValue {
        return ArithmeticValue(value: lhs.value / rhs.value)
    }

    public static func % (lhs: ArithmeticValue, rhs: ArithmeticValue) -> ArithmeticValue {
        return ArithmeticValue(value: lhs.value % rhs.value)
    }
}

// MARK: - Class-Returning Arithmetic Operators
//
// Regression coverage for the "operator-return CS0029" family (Macaw.Size, SwiftDate.TimePeriod):
// an `open class` whose arithmetic operators return the class itself. A class instance comes back
// in x0 on the direct-call branch (no indirect result), so the raw pointer the P/Invoke returns
// MUST be marshalled via SwiftMarshal.MarshalFromSwift<T>. Without that, the generated operator
// body emits `return {pinvoke}(...)`, assigning an IntPtr to the projected class, and fails to
// compile with CS0029. All existing operator fixtures are frozen structs, so this shape was a
// blind spot.
open class Vector2D {
    public let x: Double
    public let y: Double

    public init(x: Double, y: Double) {
        self.x = x
        self.y = y
    }

    public static func + (lhs: Vector2D, rhs: Vector2D) -> Vector2D {
        return Vector2D(x: lhs.x + rhs.x, y: lhs.y + rhs.y)
    }

    public static func - (lhs: Vector2D, rhs: Vector2D) -> Vector2D {
        return Vector2D(x: lhs.x - rhs.x, y: lhs.y - rhs.y)
    }

    // Scalar right-hand side mirrors SwiftDate's `TimePeriod + Double` shape: still a
    // class-returning operator, but with a non-class operand.
    public static func * (lhs: Vector2D, rhs: Double) -> Vector2D {
        return Vector2D(x: lhs.x * rhs, y: lhs.y * rhs)
    }
}

// MARK: - Class Operator Controls

/// Homogeneous class-operator control. Operator operand names are derived from the operand types,
/// so the source labels below intentionally do not create a marshalling-base collision.
public final class NumberBox {
    public let value: Int32

    public init(_ value: Int32) {
        self.value = value
    }

    public static func + (_ value: NumberBox, _ valueOther: NumberBox) -> NumberBox {
        NumberBox(value.value + valueOther.value)
    }
}

/// Right-hand operand for the heterogeneous alias-collision operator below.
public final class OperatorValueOther {
    public let value: Int32

    public init(_ value: Int32) {
        self.value = value
    }
}

/// The projected operand names are `operatorValue` and `operatorValueOther`. The second name
/// occupies the scratch-name family derived from the first, forcing its internal marshalling base
/// to `__operatorValue`. Class-parent operators stay on their non-cdecl path, so their body must
/// declare that alias before the P/Invoke arguments read `__operatorValue.Payload`.
public final class OperatorValue {
    public let value: Int32

    public init(_ value: Int32) {
        self.value = value
    }

    public static func + (_ value: OperatorValue,
                          _ valueOther: OperatorValueOther) -> OperatorValue {
        OperatorValue(value.value + valueOther.value)
    }
}

/// ObjC-rooted companion for the `.Handle` operator-argument lane. The heterogeneous operand
/// names force the same public/internal alias collision as `OperatorValue`, while NSObject
/// inheritance makes strict generated-C# compilation exercise handle extraction rather than
/// the pure-Swift `.Payload` path.
public final class ObjCOperatorValueOther: NSObject {
    public let value: Int32

    public init(_ value: Int32) {
        self.value = value
    }
}

public final class ObjCOperatorValue: NSObject {
    public let value: Int32

    public init(_ value: Int32) {
        self.value = value
    }

    public static func + (_ value: ObjCOperatorValue,
                          _ valueOther: ObjCOperatorValueOther) -> ObjCOperatorValue {
        ObjCOperatorValue(value.value + valueOther.value)
    }
}
