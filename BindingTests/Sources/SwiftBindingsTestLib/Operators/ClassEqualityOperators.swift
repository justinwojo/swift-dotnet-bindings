// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Classes that declare == without conforming to Equatable

/// A root class that declares `==` for itself without conforming to `Equatable`.
public class OperatorEqualityRoot {
    public let key: Int32
    public init(key: Int32) { self.key = key }

    public static func == (lhs: OperatorEqualityRoot, rhs: OperatorEqualityRoot) -> Bool {
        lhs.key == rhs.key
    }
}

/// A base class with no equality of its own.
open class OperatorEqualityBase {
    public let key: Int32
    public init(key: Int32) { self.key = key }
}

/// A derived class that declares `==` for itself only. C# requires a type that defines `==` to
/// override `Equals` and `GetHashCode` as well.
public class OperatorEqualityDerived: OperatorEqualityBase {
    public static func == (lhs: OperatorEqualityDerived, rhs: OperatorEqualityDerived) -> Bool {
        lhs.key == rhs.key
    }
}

/// An NSObject-rooted base: NSObject already overrides `Equals` and `GetHashCode` in C#.
open class OperatorEqualityObjCBase: NSObject {
    public let key: Int32
    public init(key: Int32) {
        self.key = key
        super.init()
    }
}

/// An NSObject-rooted class that declares `==` for itself.
public class OperatorEqualityObjCDerived: OperatorEqualityObjCBase {
    public static func == (lhs: OperatorEqualityObjCDerived, rhs: OperatorEqualityObjCDerived) -> Bool {
        lhs.key == rhs.key
    }
}

// MARK: - A subclass `==` beside an inherited Equatable conformance

/// A Swift base whose `Equatable` and `Hashable` conformance compare `key` only.
open class OperatorEquatableBase: Hashable {
    public let key: Int32
    public let detail: Int32
    public init(key: Int32, detail: Int32) {
        self.key = key
        self.detail = detail
    }

    public static func == (lhs: OperatorEquatableBase, rhs: OperatorEquatableBase) -> Bool {
        lhs.key == rhs.key
    }

    public func hash(into hasher: inout Hasher) { hasher.combine(key) }
}

/// Declares a stricter `==` for itself. Swift's `Equatable` and `Hashable` witnesses stay the
/// base's, so `Set` and `Dictionary` compare `key` only while a direct `==` compares both fields.
public class OperatorEquatableDerived: OperatorEquatableBase {
    public static func == (lhs: OperatorEquatableDerived, rhs: OperatorEquatableDerived) -> Bool {
        lhs.key == rhs.key && lhs.detail == rhs.detail
    }
}

/// Whether Swift's own `Hashable` treats the two values as one: the reference the binding's
/// `Equals` and `GetHashCode` must agree with.
public func operatorEquatableSetCount(_ a: OperatorEquatableDerived, _ b: OperatorEquatableDerived) -> Int32 {
    Int32(Set<OperatorEquatableBase>([a, b]).count)
}

/// The same question for an NSObject subclass, whose `Hashable` is NSObject's `isEqual:`/`hash`.
public func operatorObjCSetCount(_ a: OperatorEqualityObjCDerived, _ b: OperatorEqualityObjCDerived) -> Int32 {
    Int32(Set<NSObject>([a, b]).count)
}
