// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import SwiftBindingsTestLibDependency

// MARK: - Existentials whose layout depends on which protocols are markers
//
// Whether a protocol is a marker is a fact of its declaration, not its name. The dependency's own
// `Sendable` (SwiftBindingsTestLibDependency/DependencyNamedSendable.swift) is an ordinary
// protocol: its existential carries a witness table, 40 bytes. `LedgerTransferTag` is this
// library's own `@_marker` protocol: it adds no witness table, so `any LedgerCoded &
// LedgerLabelled & LedgerTransferTag` is the two-table container, 48 bytes, not 56. Each shape is
// vended as an array, both from a per-conformer specialization of a `some LedgerOption` builder
// (OpaqueOptionBuilder.swift) and from a plain function, and as a single value, and each comes
// back through a function that reads it through its witness tables. An element read at the wrong
// stride, or a value passed in the wrong container, answers with a wrong code or crashes.

/// A marker protocol of this library's own. It has no requirements and no witness table.
@_marker public protocol LedgerTransferTag {}

extension LedgerCodedEntry: LedgerTransferTag {}

extension LedgerCodedEntry: SwiftBindingsTestLibDependency.Sendable {
    public var dependencySendableCode: Int32 { ledgerCode + 10_000 }
}

public struct LedgerMarkerIdentityArrayBuilder {
    /// Two existentials of the dependency's `Sendable`, valued with the option's code and the code
    /// plus three.
    public static func dependencySendables(_ option: some LedgerOption) -> [any SwiftBindingsTestLibDependency.Sendable] {
        [LedgerCodedEntry(option.optionCode), LedgerCodedEntry(option.optionCode + 3)]
    }

    /// Two tagged compositions, valued with the option's code and the code plus four.
    public static func tagged(_ option: some LedgerOption) -> [any LedgerCoded & LedgerLabelled & LedgerTransferTag] {
        [LedgerCodedEntry(option.optionCode), LedgerCodedEntry(option.optionCode + 4)]
    }
}

/// Three existentials of the dependency's `Sendable`, valued `base`, `base + 1` and `base + 2`.
public func makeDependencySendables(_ base: Int32) -> [any SwiftBindingsTestLibDependency.Sendable] {
    [LedgerCodedEntry(base), LedgerCodedEntry(base + 1), LedgerCodedEntry(base + 2)]
}

/// Three tagged compositions, valued `base`, `base + 1` and `base + 2`.
public func makeTaggedLedgerEntries(_ base: Int32) -> [any LedgerCoded & LedgerLabelled & LedgerTransferTag] {
    [LedgerCodedEntry(base), LedgerCodedEntry(base + 1), LedgerCodedEntry(base + 2)]
}

/// One existential of the dependency's `Sendable`.
public func makeDependencySendable(_ code: Int32) -> any SwiftBindingsTestLibDependency.Sendable {
    LedgerCodedEntry(code)
}

/// One tagged composition.
public func makeTaggedLedgerEntry(_ code: Int32) -> any LedgerCoded & LedgerLabelled & LedgerTransferTag {
    LedgerCodedEntry(code)
}

/// Reads an existential of the dependency's `Sendable` back through its witness table.
public func ledgerCodeOfDependencySendable(_ value: any SwiftBindingsTestLibDependency.Sendable) -> Int32 {
    value.dependencySendableCode
}

/// Reads a tagged composition's code back through its first witness table.
public func ledgerCodeOfTagged(_ value: any LedgerCoded & LedgerLabelled & LedgerTransferTag) -> Int32 {
    value.ledgerCode
}

/// Reads a tagged composition's label back through its second witness table.
public func ledgerLabelOfTagged(_ value: any LedgerCoded & LedgerLabelled & LedgerTransferTag) -> String {
    value.ledgerLabel
}

/// Sums the dependency-`Sendable` codes of an array handed in from the caller.
public func sumDependencySendableCodes(_ values: [any SwiftBindingsTestLibDependency.Sendable]) -> Int32 {
    values.reduce(0) { $0 + $1.dependencySendableCode }
}

/// Sums the codes of an array of tagged compositions handed in from the caller.
public func sumTaggedLedgerCodes(_ values: [any LedgerCoded & LedgerLabelled & LedgerTransferTag]) -> Int32 {
    values.reduce(0) { $0 + $1.ledgerCode }
}
