// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A frozen struct whose zeroed form is NOT a value of the type
//
// The degradation lane for a consumer-owned carrier answers a collected implementation with the
// return type's identity value instead of killing the process. That trade is only sound when the
// synthesized value is genuinely inhabitable: blittability says how a value CROSSES the boundary,
// never that all-zero is one of its values.
//
// `PointerHolder` is the counterexample. It is frozen and reference-free, so it travels inline as
// bytes — but its stored `UnsafeRawPointer` excludes null from its inhabitants, and the host below
// loads through that field the instant it receives the struct. A zeroed answer would turn a
// lifetime mistake into a null dereference inside Swift, which is strictly worse than saying so.
//
// `ExtentHolder` is the positive control that keeps the refusal from over-widening: every field is
// a numeric scalar whose zero is the language's own zero, so the all-zero aggregate really is a
// value of the type and the consumer-owned lane can still degrade to it.

/// Frozen, reference-free, and holds a pointer Swift is entitled to dereference.
@frozen
public struct PointerHolder {
    public var pointer: UnsafeRawPointer

    public init(pointer: UnsafeRawPointer) {
        self.pointer = pointer
    }
}

/// Same transport class (frozen, blittable), numeric fields only.
@frozen
public struct ExtentHolder {
    public var offset: Int
    public var scale: Double

    public init(offset: Int, scale: Double) {
        self.offset = offset
        self.scale = scale
    }
}

/// The delegate whose getter hands a `PointerHolder` back across the boundary. `AnyObject`-bound
/// and held weakly by the host below, which is what puts its C# conformer on the consumer-owned
/// carrier lane — the lane that degrades rather than fail-fasting.
public protocol PointerCarryingDelegate: AnyObject {
    var holder: PointerHolder { get }
    var extent: ExtentHolder { get }
}

/// The framework-shaped host. Holds the delegate weakly, exactly as a real callback source does.
public final class PointerCarryingHost {
    public weak var delegate: PointerCarryingDelegate?

    public init() {}

    /// `true` while the weak slot still resolves, so a test can tell "the delegate went away" from
    /// "the callback marshalled wrong".
    public var hasDelegate: Bool {
        return delegate != nil
    }

    /// The live round trip, and the reason a zeroed answer is unacceptable: the host takes the
    /// struct the C# getter produced and LOADS THROUGH the pointer inside it. A holder that
    /// arrived intact yields the byte the consumer stored; a zeroed one would fault here.
    public func readHeldByte() -> UInt8 {
        guard let delegate = delegate else { return 0 }
        return delegate.holder.pointer.load(as: UInt8.self)
    }

    /// The pointer identity itself, so a test can assert the exact address survived the round trip
    /// rather than only that *some* readable address did.
    public func readHeldAddress() -> Int {
        guard let delegate = delegate else { return 0 }
        return Int(bitPattern: delegate.holder.pointer)
    }

    /// The numeric-only control's round trip, rendered so both fields have to be intact.
    public func readExtentDescription() -> String {
        guard let delegate = delegate else { return "" }
        let extent = delegate.extent
        return "\(extent.offset)/\(extent.scale)"
    }
}

/// Allocates one byte holding `value` and hands back its address, so the C# side can populate a
/// `PointerHolder` with an address Swift itself minted (and can therefore safely dereference).
public func allocateProbeByte(_ value: UInt8) -> UnsafeMutableRawPointer {
    let raw = UnsafeMutableRawPointer.allocate(byteCount: 1, alignment: 1)
    raw.storeBytes(of: value, as: UInt8.self)
    return raw
}

/// Releases a byte from `allocateProbeByte`.
public func freeProbeByte(_ raw: UnsafeMutableRawPointer) {
    raw.deallocate()
}
