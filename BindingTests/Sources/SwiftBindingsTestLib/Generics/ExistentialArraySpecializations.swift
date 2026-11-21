// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Existential-array results of per-conformer specializations
//
// Each builder is a static method over `some LedgerOption` (OpaqueOptionBuilder.swift), bound once
// per conformer as a concrete overload through its own wrapper. The results are arrays of the
// existential shapes other than a single Swift protocol: a composition of two protocols (an opaque
// box and two witness tables), `Any` and `any Sendable` (an opaque box and no witness table) and
// an @objc protocol (one object pointer). Each element answers with the option's code, so a result
// read at the wrong stride or through the wrong carrier comes back as a wrong value or a crash.

/// One half of the composition the builder returns.
public protocol LedgerCoded {
    var ledgerCode: Int32 { get }
}

/// The other half of the composition the builder returns.
public protocol LedgerLabelled {
    var ledgerLabel: String { get }
}

/// A resilient value conforming to both halves.
public struct LedgerCodedEntry: LedgerCoded, LedgerLabelled {
    public let ledgerCode: Int32
    public init(_ code: Int32) { self.ledgerCode = code }
    public var ledgerLabel: String { "entry-\(ledgerCode)" }
}

/// An @objc protocol whose existential is one object pointer.
@objc public protocol LedgerObjCCoded {
    func objcLedgerCode() -> Int32
}

public final class LedgerObjCEntry: NSObject, LedgerObjCCoded {
    public let code: Int32
    public init(code: Int32) { self.code = code }
    public func objcLedgerCode() -> Int32 { code }
}

public struct LedgerExistentialArrayBuilder {
    /// Two composed existentials, valued with the option's code and the code plus one.
    public static func composed(_ option: some LedgerOption) -> [any LedgerCoded & LedgerLabelled] {
        [LedgerCodedEntry(option.optionCode), LedgerCodedEntry(option.optionCode + 1)]
    }

    /// The option's code as an `Int32`, then its label as a `String`, as `Any`.
    public static func anything(_ option: some LedgerOption) -> [Any] {
        [option.optionCode, "option-\(option.optionCode)"]
    }

    /// The option's code as an `Int32`, as `any Sendable`.
    public static func sendables(_ option: some LedgerOption) -> [any Sendable] {
        [option.optionCode, option.optionCode + 2]
    }

    /// Two @objc existentials, valued with the option's code and the code plus one.
    public static func objcCoded(_ option: some LedgerOption) -> [any LedgerObjCCoded] {
        [LedgerObjCEntry(code: option.optionCode), LedgerObjCEntry(code: option.optionCode + 1)]
    }
}

/// Reads the code of an `Any` element as the builder vends it, or -1 for any other element.
public func ledgerCodeOfAny(_ value: Any) -> Int32 {
    (value as? Int32) ?? -1
}

/// Reads the label of an `Any` element as the builder vends it, or "" for any other element.
public func ledgerLabelOfAny(_ value: Any) -> String {
    (value as? String) ?? ""
}

/// Reads the code of an `any Sendable` element as the builder vends it, or -1 otherwise.
public func ledgerCodeOfSendable(_ value: any Sendable) -> Int32 {
    (value as? Int32) ?? -1
}

/// Reads the code of a composed element back through its first witness table.
public func ledgerCodeOfComposed(_ value: any LedgerCoded & LedgerLabelled) -> Int32 {
    value.ledgerCode
}

/// Reads the label of a composed element back through its second witness table.
public func ledgerLabelOfComposed(_ value: any LedgerCoded & LedgerLabelled) -> String {
    value.ledgerLabel
}
