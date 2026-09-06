// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// A pointer-width integer that arrives through an initializer and leaves through a property of
// the same type. The two halves are emitted by different paths — the constructor keeps the raw
// native-int parameter unless a convenience overload is emitted for it, while the property is
// narrowed to a 32-bit C# type — so a value the caller can construct with must stay a value the
// caller can read back, and a value that does not fit the narrowed property must say so instead
// of wrapping.
public final class ResourceBudget {
    public let sizeLimit: UInt
    public let offset: Int

    /// Settable, so both halves of the width story can be round-tripped: the narrowed accessor
    /// widens on the way in, and the native-width companion carries a value the narrowed one
    /// cannot represent.
    public var cursor: Int

    public init(sizeLimit: UInt, offset: Int) {
        self.sizeLimit = sizeLimit
        self.offset = offset
        self.cursor = 0
    }

    // The signed twin of the same shape, taken through a second initializer so the unsigned and
    // signed convenience overloads are distinct emissions rather than one shared one.
    public init(offset: Int) {
        self.sizeLimit = 0
        self.offset = offset
        self.cursor = 0
    }

    public var doubledOffset: Int { offset &* 2 }
}

public func makeResourceBudget(sizeLimit: UInt, offset: Int) -> ResourceBudget {
    return ResourceBudget(sizeLimit: sizeLimit, offset: offset)
}

/// Builds a budget whose `sizeLimit` is 2^32 — beyond the range of the narrowed 32-bit property,
/// so reading it back must fail loudly rather than report a wrapped value.
public func makeOversizedResourceBudget() -> ResourceBudget {
    return ResourceBudget(sizeLimit: UInt(UInt32.max) + 1, offset: 0)
}

/// Builds a budget whose `offset` is 2^31 — one past the signed narrowed range.
public func makeOverSignedResourceBudget() -> ResourceBudget {
    return ResourceBudget(offset: Int(Int32.max) + 1)
}

/// Reads the limit back on the Swift side, so a test can prove the value really crossed intact
/// even when the narrowed C# property refuses to surface it.
public func readSizeLimitAsString(_ budget: ResourceBudget) -> String {
    return String(budget.sizeLimit)
}

public func readOffsetAsString(_ budget: ResourceBudget) -> String {
    return String(budget.offset)
}

/// The same Swift-side read for the settable property, so a value written through the
/// native-width companion can be shown to have reached Swift at its full width.
public func readCursorAsString(_ budget: ResourceBudget) -> String {
    return String(budget.cursor)
}

/// A pointer-width value returned from a *method* rather than read from a property. Method
/// returns are deliberately left at their native width, so this must arrive whole.
public func hugeNativeCount() -> Int {
    return Int(Int32.max) + 5
}

/// Three initializer lanes over the same pointer-width parameter shape. `init(_:)` and
/// `init(threshold:)` project to the same C# constructor signature, so the labelled one is
/// recovered as a static factory instead of being dropped, and `init?(ceiling:)` emits as a
/// failable static factory. A caller who can reach one lane with an idiomatic 32-bit argument
/// should be able to reach all three the same way.
public final class WidthGate {
    public let capacity: Int
    public let threshold: Int
    public let ceiling: UInt

    public init(_ capacity: Int) {
        self.capacity = capacity
        self.threshold = 0
        self.ceiling = 0
    }

    public init(threshold: Int) {
        self.capacity = 0
        self.threshold = threshold
        self.ceiling = 0
    }

    public init?(ceiling: UInt) {
        if ceiling == 0 {
            return nil
        }
        self.capacity = 0
        self.threshold = 0
        self.ceiling = ceiling
    }

    /// A method return over the same Swift type as the narrowed properties above, kept at its
    /// native width so an `int` overload cannot silently claim a 64-bit result.
    public func widenedCapacity() -> Int {
        return capacity &* 2
    }
}
