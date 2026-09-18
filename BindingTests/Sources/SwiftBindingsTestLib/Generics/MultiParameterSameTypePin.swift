// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Same-type pins on a multi-parameter generic parent
//
// A constrained extension that pins one generic parameter of a two-parameter type to a concrete
// type (`where Lo: Fine, Hi == Coarse`) or pins both (`where Lo == Fine, Hi == Coarse`). The
// closed-extension route spells its receiver as `Parent<Concrete>`, which names an instantiation
// only when the pinned parameter is the parent's only one. Here it rendered `PinRange<PinCoarse>`
// — "generic type specialized with too few type parameters" — and the open instance/static
// wrappers that follow could not infer the other parameter, so the wrapper never converged.
//
// The pinned members bind on the closed instantiations their `where` clause admits, re-surfaced as
// extension methods on `PinRange<PinFine, PinCoarse>` / `PinRange<PinCoarse, PinCoarse>` — the
// open-generic class cannot carry them, so a marker there records where they went. A pinned *static*
// method (`coarse`) is the one shape still without a spelling: C# has no static extension members.

public protocol PinGranularity {}
public protocol PinFineGranularity: PinGranularity {}

// Deliberately structs, not caseless enums. A caseless enum used the same way — as a phantom tag
// that only ever appears as a type argument — projects as a static class or an empty C# enum and
// satisfies neither `ISwiftObject` nor the constraint's interface, which is a separate projection
// defect. Structs keep this fixture measuring the pin and nothing else.
public struct PinCoarse: PinFineGranularity { public init() {} }
public struct PinFine: PinFineGranularity { public init() {} }

public struct PinRange<Lo: PinGranularity, Hi: PinGranularity> {
    public let raw: Int
    public init(raw: Int) { self.raw = raw }

    public var rawPlusOne: Int { raw + 1 }
}

// One parameter pinned, the other left open behind a protocol constraint.
extension PinRange where Lo: PinFineGranularity, Hi == PinCoarse {
    public var coarseUnits: Int { raw * 1000 }
    public func coarseUnitsTwice() -> Int { raw * 2000 }
    public static func coarse(_ value: Int) -> PinRange { PinRange(raw: value) }
}

// Every parameter pinned.
extension PinRange where Lo == PinFine, Hi == PinCoarse {
    public var finePerCoarse: Int { raw + 7 }
    public func finePerCoarseTwice() -> Int { (raw + 7) * 2 }
}

public func makeFineCoarsePinRange(_ raw: Int) -> PinRange<PinFine, PinCoarse> {
    PinRange(raw: raw)
}

// Control: a pin on a single-parameter parent closes it and is re-surfaced as an extension.
public struct PinSingle<T> {
    public let raw: Int
    public init(raw: Int) { self.raw = raw }
}

extension PinSingle where T == PinCoarse {
    public var doubled: Int { raw * 2 }
}

public func makeCoarsePinSingle(_ raw: Int) -> PinSingle<PinCoarse> {
    PinSingle(raw: raw)
}
