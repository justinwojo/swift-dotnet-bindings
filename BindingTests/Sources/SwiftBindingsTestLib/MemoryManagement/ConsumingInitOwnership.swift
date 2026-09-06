// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Existential handed to a consuming initializer

/// Proxy-emittable protocol: no `init()` requirement, no associated type and no `Self`
/// requirement, so the generator emits `ConsumedCarrierProxy` and a C#-authored conformer
/// reaches Swift as a proxy whose construction `+1` is the conformer box's ONLY reference.
///
/// That is what makes the ownership question observable here and not in the proxy-suppressed
/// siblings elsewhere in the corpus: a Swift-vended conformer boxes itself fresh, so a callee
/// that consumes the container merely consumes that fresh box, while a proxy-backed container
/// aliases a reference someone else still owns.
public protocol ConsumedCarrier {
    func consumedCarrierTag() -> Int32
}

/// Initializer that CONSUMES its existential and keeps it: the value must outlive the call for
/// exactly as long as the resulting struct does.
public struct ConsumedCarrierCell {
    private let tag: Int32
    private let held: any ConsumedCarrier

    /// Failable, so the member lands on the direct `CallConvSwift` / `SwiftIndirectResult` arm
    /// where the C# call site renders the container inline as a call argument.
    public init?(optional carrier: any ConsumedCarrier) {
        let t = carrier.consumedCarrierTag()
        if t < 0 { return nil }
        self.tag = t
        self.held = carrier
    }

    /// Non-failable sibling taking the identical parameter through the `@_cdecl` wrapper arm —
    /// the parity control for the arm above.
    public init(_ carrier: any ConsumedCarrier) {
        self.tag = carrier.consumedCarrierTag()
        self.held = carrier
    }

    public func storedTag() -> Int32 { tag }

    /// Reverse-dispatches into the retained conformer, so a carrier that lost a count is
    /// observable from Swift rather than only through managed bookkeeping.
    public func reread() -> Int32 { held.consumedCarrierTag() }
}

/// Initializer that CONSUMES its existential and drops it before returning: the release lands
/// inside the call, so an under-retained carrier dies while the caller still holds it.
public struct ConsumedCarrierProbe {
    private let tag: Int32

    public init?(reading carrier: any ConsumedCarrier) {
        let t = carrier.consumedCarrierTag()
        if t < 0 { return nil }
        self.tag = t
    }

    public func storedTag() -> Int32 { tag }
}

public enum ConsumedCarrierError: Error {
    case rejected
}

/// Throwing consuming initializer: the exceptional exit still consumes the argument, so a
/// mint made before the call must not be released twice on the way out.
public struct ConsumedCarrierThrowingCell {
    private let tag: Int32

    public init(throwingOn carrier: any ConsumedCarrier) throws {
        let t = carrier.consumedCarrierTag()
        if t < 0 { throw ConsumedCarrierError.rejected }
        self.tag = t
    }

    public func storedTag() -> Int32 { tag }
}

/// Swift-vended conformer: boxes itself fresh through the boxable path, so it exercises the
/// same members from the arm where the container is already owned.
public final class ConsumedCarrierCounterCell: ConsumedCarrier {
    private let stored: Int32
    private let ref: TrackedRef

    public init(tag: Int32) {
        self.stored = tag
        self.ref = TrackedRef(tag: tag, category: "ConsumedCarrierCounterCell")
    }

    public func consumedCarrierTag() -> Int32 { stored }
}

// MARK: - Class-constrained existential handed to a consuming initializer

/// Class-constrained sibling of `ConsumedCarrier`. Its existential is the compact
/// `[classRef][witnessTable]` value rather than an opaque payload, so the carrier the call site
/// hands over carries its whole reference on the first word and nothing the opaque value witness
/// would read as an inline payload.
public protocol ClassBoundConsumedCarrier: AnyObject {
    func classBoundCarrierTag() -> Int32
}

/// Consuming initializer over the class-constrained existential, failable so the member lands on
/// the direct arm where the container is rendered inline as a call argument.
public struct ClassBoundConsumedCarrierCell {
    private let tag: Int32
    private let held: any ClassBoundConsumedCarrier

    public init?(optional carrier: any ClassBoundConsumedCarrier) {
        let t = carrier.classBoundCarrierTag()
        if t < 0 { return nil }
        self.tag = t
        self.held = carrier
    }

    public func storedTag() -> Int32 { tag }

    /// Reverse-dispatches into the retained conformer, so a carrier that lost its count is
    /// observable from Swift rather than only through managed bookkeeping.
    public func reread() -> Int32 { held.classBoundCarrierTag() }
}

/// Swift-vended class-constrained conformer, feeding the shared allocation counters so the
/// conformer's lifetime is observable across the hand-over.
public final class ClassBoundConsumedCarrierCounterCell: ClassBoundConsumedCarrier {
    private let stored: Int32
    private let ref: TrackedRef

    public init(tag: Int32) {
        self.stored = tag
        self.ref = TrackedRef(tag: tag, category: "ClassBoundConsumedCarrierCounterCell")
    }

    public func classBoundCarrierTag() -> Int32 { stored }
}

// MARK: - Resilient struct with a reference field in a consuming initializer's slot

/// Consuming initializer over a NON-frozen struct carrying a class reference. The value is
/// address-only across the ABI, so it travels as a pointer into a callee-owned slot: the callee
/// takes over the contents at the caller's address, which is a borrow unless the caller mints
/// the count first.
public struct ResilientRefConsumerCell {
    private let held: TrackedRefStruct

    /// Failable so the member takes the direct arm, where the parameter is passed as the raw
    /// pointer read off the managed wrapper's own payload handle.
    public init?(optional box: TrackedRefStruct) {
        if box.value < 0 { return nil }
        self.held = box
    }

    public func heldValue() -> Int32 { held.value }

    /// Reads through the retained reference field, so a field released early is observable from
    /// Swift.
    public func heldRefTag() -> Int32 { held.ref.tag }
}
