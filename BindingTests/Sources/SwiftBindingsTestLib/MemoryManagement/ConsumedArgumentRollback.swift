// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A consumed argument's +1 is rolled back when a later argument fails
//
// An initializer consumes its arguments, so the binding retains each one for the callee before the
// call. When a later argument cannot be converted (here, an `any Error` whose C# wrapper is already
// disposed), the call never happens and nothing consumes the retains taken for the earlier
// arguments: they have to be given back, or each earlier argument leaks.
//
// The initializers are failable inits of non-frozen structs, which keeps them on the direct
// Swift-convention call where the binding hands each argument over itself (a class's failable init
// goes through the C-convention wrapper, where Swift takes the retains). Every value here feeds the
// shared allocation counters, so a leaked +1 shows up as an instance that never deinitializes.

/// Takes two consumed errors.
public struct ConsumedErrorPair {
    public let total: Int32

    public init?(first: any Error, second: any Error) {
        let a = (first as? TrackedError)?.tag ?? 0
        let b = (second as? TrackedError)?.tag ?? 0
        if a < 0 || b < 0 { return nil }
        total = a + b
    }
}

/// Takes a consumed NSObject-rooted object, then a consumed error.
public struct ConsumedObjCAndError {
    public let total: Int32

    public init?(shape: TrackedObjCShapeThing, error: any Error) {
        let b = (error as? TrackedError)?.tag ?? 0
        if b < 0 { return nil }
        total = shape.tag + b
    }
}

// MARK: - A consumed existential minted for the callee is rolled back when a later argument fails
//
// The binding mints a +1 for an existential a consuming initializer takes: a fresh box for a
// Swift-vended class conformer, an independent copy of a proxy's container. When converting a
// later argument throws (here, an existential whose proxy is already disposed), the call never
// happens, and that mint has to be destroyed, or the conformer it holds never deinitializes.

/// Takes two consumed single-protocol existentials.
public struct ConsumedCarrierPair {
    public let total: Int32

    public init?(first: any ConsumedCarrier, second: any ConsumedCarrier) {
        let a = first.consumedCarrierTag()
        let b = second.consumedCarrierTag()
        if a < 0 || b < 0 { return nil }
        total = a + b
    }
}

/// Returns a Swift-vended conformer as the existential, so C# holds it through its proxy.
public func makeConsumedCarrier(tag: Int32) -> any ConsumedCarrier {
    ConsumedCarrierCounterCell(tag: tag)
}

/// The second protocol of the composition below.
public protocol ConsumedCarrierLabel {
    func consumedCarrierLabel() -> Int32
}

/// Conforms to both protocols, and feeds the shared allocation counters.
public final class ConsumedCompositeCell: ConsumedCarrier, ConsumedCarrierLabel {
    private let stored: Int32
    private let ref: TrackedRef

    public init(tag: Int32) {
        self.stored = tag
        self.ref = TrackedRef(tag: tag, category: "ConsumedCompositeCell")
    }

    public func consumedCarrierTag() -> Int32 { stored }
    public func consumedCarrierLabel() -> Int32 { stored * 10 }
}

/// Returns a composition existential, which C# holds through the composition's proxy.
public func makeConsumedComposite(tag: Int32) -> any ConsumedCarrier & ConsumedCarrierLabel {
    ConsumedCompositeCell(tag: tag)
}

/// Takes two consumed composition existentials.
public struct ConsumedCompositePair {
    public let total: Int32

    public init?(first: any ConsumedCarrier & ConsumedCarrierLabel, second: any ConsumedCarrier & ConsumedCarrierLabel) {
        let a = first.consumedCarrierTag()
        let b = second.consumedCarrierLabel()
        if a < 0 || b < 0 { return nil }
        total = a + b
    }
}

/// Reads an array of compositions. The array the binding builds owns each element, so each
/// element is an independent copy of its proxy's container rather than the proxy's own.
public func sumConsumedComposites(_ values: [any ConsumedCarrier & ConsumedCarrierLabel]) -> Int32 {
    values.reduce(0) { $0 + $1.consumedCarrierTag() + $1.consumedCarrierLabel() }
}

/// Reads an optional composition through an owning optional carrier.
public func labelOfOptionalComposite(_ value: (any ConsumedCarrier & ConsumedCarrierLabel)?) -> Int32 {
    value?.consumedCarrierLabel() ?? -1
}

/// Reads one composition back from Swift, so a container that lost its count is observable.
public func tagOfConsumedComposite(_ value: any ConsumedCarrier & ConsumedCarrierLabel) -> Int32 {
    value.consumedCarrierTag()
}
