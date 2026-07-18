// Wrapper-shaped fixture: two @_cdecl entry points, one of which references a type that does not
// exist. The error must attribute to the SBW_Gadget_rotate block alone; SBW_Gadget_scale is clean.
// Names are generic (Gadget/rotate/scale) — no third-party library is reproduced here.
import Foundation

@_cdecl("SBW_Gadget_rotate")
public func SBW_Gadget_rotate(_ handle: UnsafeMutableRawPointer, _ degrees: Int) {
    let gadget = handle.load(as: MissingGadgetType.self)
    gadget.rotate(by: degrees)
}

@_cdecl("SBW_Gadget_scale")
public func SBW_Gadget_scale(_ handle: UnsafeMutableRawPointer, _ factor: Double) {
    let value = handle.load(as: Double.self)
    _ = value * factor
}
