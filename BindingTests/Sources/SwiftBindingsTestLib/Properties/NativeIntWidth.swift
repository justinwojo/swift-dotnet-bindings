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

    public init(sizeLimit: UInt, offset: Int) {
        self.sizeLimit = sizeLimit
        self.offset = offset
    }

    // The signed twin of the same shape, taken through a second initializer so the unsigned and
    // signed convenience overloads are distinct emissions rather than one shared one.
    public init(offset: Int) {
        self.sizeLimit = 0
        self.offset = offset
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
