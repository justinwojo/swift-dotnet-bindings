// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if canImport(StoreKit)
import StoreKit
import Foundation

// MARK: - Member references a framework type absent from the available C# bindings
//
// `StoreKit.Transaction` (StoreKit 2) is a Swift struct whose name carries no "SK"
// prefix and which is NOT listed in StoreKit's `valueTypes` entry, so the generator's
// loose ObjC-module test treats it as an Objective-C-bridged type even though no
// matching C# binding exists in Microsoft.iOS's `StoreKit` namespace. Left unguarded,
// the ObjC-bridging fallback synthesizes a class-shaped record for it and the member is
// emitted referencing `StoreKit.Transaction`, producing uncompilable C# (`CS0234:
// 'Transaction' does not exist in the namespace 'StoreKit'`).
//
// The discriminator is the type's USR mangling-suffix letter: a StoreKit.Transaction
// reference carries `s:8StoreKit11TransactionV` (trailing `V` = value-type struct), which
// the ObjC bridge synthesizes as a class — a precise, zero-false-positive signal that the
// bridged record is wrong and no real binding exists. Every member shape that references
// such a type must be skipped with a SWIFTBIND warning, leaving the rest of the type
// bindable. All four shapes below exercise the guard: a bare stored property, an
// `Optional<>` of the type, a method that both takes and returns it, and the constructor
// parameter. (The Optional shape additionally exercises the parser fix that threads the
// inner generic-argument USR through `Optional<StoreKit.Transaction>`; without it the
// inner reference is string-parsed from the printed name and the guard never sees the USR.)
// The benign sibling members confirm the type still emits its compilable surface after
// the absent-type members are skipped.

public final class IAPTransactionLike {
    // The absent-type member that must be skipped (not emitted as `StoreKit.Transaction`).
    public let transaction: StoreKit.Transaction

    // A method that takes and returns the absent type — both positions must be skipped.
    public func refresh(with other: StoreKit.Transaction) -> StoreKit.Transaction { other }

    // An Optional-of-absent-type property — the inner USR must thread through Optional<>.
    public var pendingTransaction: StoreKit.Transaction?

    // Benign members: must still emit and round-trip after the absent-type member is skipped.
    public let identifier: Int64
    public func summary() -> Int32 { 1 }

    public init(transaction: StoreKit.Transaction, identifier: Int64) {
        self.transaction = transaction
        self.pendingTransaction = nil
        self.identifier = identifier
    }
}
#endif
