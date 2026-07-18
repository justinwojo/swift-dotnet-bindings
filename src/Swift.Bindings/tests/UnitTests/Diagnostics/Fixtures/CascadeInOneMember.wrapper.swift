// Wrapper-shaped fixture: a single @_cdecl entry point that provokes several diagnostics at once,
// all inside the one block. Attribution must collapse the cascade to exactly one culprit unit.
import Foundation

@_cdecl("SBW_Timer_fire")
public func SBW_Timer_fire(_ handle: UnsafeMutableRawPointer) {
    let first: UndefinedAlpha = handle.load(as: UndefinedAlpha.self)
    let second = first.combine(with: UndefinedBeta.shared)
    dispatchOut(second, undefinedGamma)
}
