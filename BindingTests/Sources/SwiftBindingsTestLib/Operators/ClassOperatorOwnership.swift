// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Class operators that consume an operand or throw

private nonisolated(unsafe) var operatorTokenDeinits: Int32 = 0

/// How many `OperatorToken` instances have been deinitialized. A consuming operator that is handed
/// an operand the caller still owns destroys it during the call, which shows up here immediately.
public func getOperatorTokenDeinitCount() -> Int32 { operatorTokenDeinits }

/// Error a throwing class operator raises. It holds a `LifetimeTracker`-counted ref, so an error box
/// the binding never releases keeps the live count up after the caught exception is dropped.
public final class OperatorTokenFailure: Error {
    public let code: Int32
    private let ref: TrackedRef

    public init(code: Int32) {
        self.code = code
        self.ref = TrackedRef(tag: code, category: "OperatorTokenFailure")
    }
}

/// A class whose operators take an operand `consuming` or are declared `throws`. Class operators are
/// static members, so both shapes reach the same call route as the class's other static members and
/// need the same ownership hand-over and error check an ordinary method gets.
public final class OperatorToken {
    public let value: Int32
    private let ref: TrackedRef

    public init(_ value: Int32) {
        self.value = value
        self.ref = TrackedRef(tag: value, category: "OperatorToken")
    }

    deinit { operatorTokenDeinits += 1 }

    /// Consumes the left operand and returns a scalar.
    public static func + (lhs: consuming OperatorToken, rhs: OperatorToken) -> Int32 {
        lhs.value + rhs.value
    }

    /// Consumes the left operand and returns a new instance of the class.
    public static func - (lhs: consuming OperatorToken, rhs: OperatorToken) -> OperatorToken {
        OperatorToken(lhs.value - rhs.value)
    }

    /// Throws when the right operand is negative; otherwise returns a scalar.
    public static func * (lhs: OperatorToken, rhs: OperatorToken) throws -> Int32 {
        if rhs.value < 0 { throw OperatorTokenFailure(code: rhs.value) }
        return lhs.value * rhs.value
    }

    /// Throws when the right operand is zero; otherwise returns a new instance of the class.
    public static func / (lhs: OperatorToken, rhs: OperatorToken) throws -> OperatorToken {
        if rhs.value == 0 { throw OperatorTokenFailure(code: 0) }
        return OperatorToken(lhs.value / rhs.value)
    }

    /// Ordinary throwing static member with the same failure as the operators above, so a test can
    /// compare the exception an operator surfaces with the one an ordinary member surfaces.
    public static func checkedDivide(_ lhs: OperatorToken, _ rhs: OperatorToken) throws -> OperatorToken {
        try lhs / rhs
    }
}

private nonisolated(unsafe) var objCOperatorTokenDeinits: Int32 = 0

/// How many `ObjCOperatorToken` instances have been deinitialized.
public func getObjCOperatorTokenDeinitCount() -> Int32 { objCOperatorTokenDeinits }

/// NSObject-rooted counterpart of `OperatorToken`: its operands cross as object handles rather than
/// payloads, and a consuming one needs the same +1.
public final class ObjCOperatorToken: NSObject {
    public let value: Int32

    public init(_ value: Int32) {
        self.value = value
    }

    deinit { objCOperatorTokenDeinits += 1 }

    /// Consumes the left operand and returns a scalar.
    public static func + (lhs: consuming ObjCOperatorToken, rhs: ObjCOperatorToken) -> Int32 {
        lhs.value + rhs.value
    }
}
