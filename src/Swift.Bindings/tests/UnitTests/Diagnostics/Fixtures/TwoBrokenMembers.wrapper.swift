// Wrapper-shaped fixture: two independent @_cdecl entry points, each with its own error, plus one
// clean entry point. Attribution must report two distinct culprit units — not one, not three.
import Foundation

@_cdecl("SBW_Ledger_credit")
public func SBW_Ledger_credit(_ handle: UnsafeMutableRawPointer, _ amount: Int) {
    let entry = handle.load(as: MissingLedgerEntry.self)
    entry.credit(amount)
}

@_cdecl("SBW_Ledger_balance")
public func SBW_Ledger_balance(_ handle: UnsafeMutableRawPointer) -> Int {
    let value = handle.load(as: Int.self)
    return value
}

@_cdecl("SBW_Ledger_debit")
public func SBW_Ledger_debit(_ handle: UnsafeMutableRawPointer, _ amount: Int) {
    let entry = handle.load(as: MissingLedgerEntry.self)
    entry.debit(by: amountUnknown)
}
