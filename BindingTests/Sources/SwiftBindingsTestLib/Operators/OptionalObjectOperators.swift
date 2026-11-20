// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Operators with an Optional object result or operand

/// A class whose operators return an Optional object (an ObjC class, `NSObject` itself, and a Swift
/// class) or take an Optional ObjC operand. An operator body writes its own call, so each of these
/// shapes has to be read or converted exactly as an ordinary member with the same signature does:
/// the Optional ObjC result adopted from its raw handle (nil as null), the Optional operand passed
/// as its live handle (nil as a null pointer). Every operator returns nil for one input and an
/// object for another.
public final class OptionalObjectOperatorClass {
    public let value: Int32
    public let attachment: NSObject?

    public init(_ value: Int32, attachment: NSObject?) {
        self.value = value
        self.attachment = attachment
    }

    /// The sum as an `NSNumber`, or nil when it is zero.
    public static func + (lhs: OptionalObjectOperatorClass, rhs: OptionalObjectOperatorClass) -> NSNumber? {
        let sum = lhs.value + rhs.value
        return sum == 0 ? nil : NSNumber(value: sum)
    }

    /// The left operand's own attachment, or nil when the right operand's value is negative. The
    /// object comes back as the same instance the caller attached.
    public static func & (lhs: OptionalObjectOperatorClass, rhs: OptionalObjectOperatorClass) -> NSObject? {
        rhs.value < 0 ? nil : lhs.attachment
    }

    /// The negated value as an `NSNumber`, or nil when the value is zero.
    public static prefix func ~ (operand: OptionalObjectOperatorClass) -> NSNumber? {
        operand.value == 0 ? nil : NSNumber(value: -operand.value)
    }

    /// A new instance holding the difference, or nil when it is zero.
    public static func - (lhs: OptionalObjectOperatorClass, rhs: OptionalObjectOperatorClass) -> OptionalObjectOperatorClass? {
        let difference = lhs.value - rhs.value
        return difference == 0 ? nil : OptionalObjectOperatorClass(difference, attachment: nil)
    }

    /// The value times the operand's, or -1 when the operand is nil.
    public static func * (lhs: OptionalObjectOperatorClass, rhs: NSNumber?) -> Int32 {
        guard let rhs else { return -1 }
        return lhs.value * rhs.int32Value
    }
}

/// The struct counterpart of `OptionalObjectOperatorClass`: a resilient struct, so its operators call
/// Swift directly with the value's payload, through the same body as the class's.
public struct OptionalObjectOperatorValue {
    public let value: Int32

    public init(_ value: Int32) {
        self.value = value
    }

    /// The sum as an `NSNumber`, or nil when it is zero.
    public static func + (lhs: OptionalObjectOperatorValue, rhs: OptionalObjectOperatorValue) -> NSNumber? {
        let sum = lhs.value + rhs.value
        return sum == 0 ? nil : NSNumber(value: sum)
    }

    /// A class instance holding the difference, or nil when it is zero.
    public static func - (lhs: OptionalObjectOperatorValue, rhs: OptionalObjectOperatorValue) -> OptionalObjectOperatorClass? {
        let difference = lhs.value - rhs.value
        return difference == 0 ? nil : OptionalObjectOperatorClass(difference, attachment: nil)
    }

    /// The value times the operand's, or -1 when the operand is nil.
    public static func * (lhs: OptionalObjectOperatorValue, rhs: NSNumber?) -> Int32 {
        guard let rhs else { return -1 }
        return lhs.value * rhs.int32Value
    }
}
