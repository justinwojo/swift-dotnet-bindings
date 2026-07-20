// Wrapper-shaped fixture: a file-scope declaration references a type and a helper that do not exist, so
// swiftc reports the errors OUTSIDE any @_cdecl block. The symbol/anchor step has no entry-point symbol
// to charge them to, and they are not a missing-module (global) classification, so the real attributor
// leaves them unplaceable — HasUnattributedError. Three healthy entry points below give a bounded
// bisection a candidate pool to search over. Names are generic (Probe/alpha/bravo/charlie) — no
// third-party library is reproduced here.
import Foundation

private let sharedScratch: MissingScratchType = makeScratch()

@_cdecl("SBW_Probe_alpha")
public func SBW_Probe_alpha(_ handle: UnsafeMutableRawPointer) {
}

@_cdecl("SBW_Probe_bravo")
public func SBW_Probe_bravo(_ handle: UnsafeMutableRawPointer) {
}

@_cdecl("SBW_Probe_charlie")
public func SBW_Probe_charlie(_ handle: UnsafeMutableRawPointer) {
}
