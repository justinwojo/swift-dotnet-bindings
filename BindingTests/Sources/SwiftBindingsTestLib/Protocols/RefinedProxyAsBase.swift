// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - A refined protocol's existential passed where its base is expected
//
// `RPARefinedOpaque: RPABaseOpaque` projects to `IRPARefinedOpaque : IRPABaseOpaque`, so C# lets a
// refined proxy go wherever the base interface is asked for. The proxy's container holds the
// refined protocol's witness table; Swift reads an `any RPABaseOpaque` through the base table.
// Entry 1 of a refined table is its base-conformance entry, not the base's first requirement, so
// a container passed unchanged calls data as code. Each consumer below reads the base requirement
// of what it is given.
//
// Three pairs: opaque refines opaque, class-bound refines class-bound, and a class-bound protocol
// refining an opaque one, whose proxy holds the two-word class layout while the base needs the
// five-word opaque one. The opaque pair has an inline conformer and a boxed one (four words, too
// big for the inline buffer).

public protocol RPABaseOpaque {
    func baseValue() -> Int32
}

public protocol RPARefinedOpaque: RPABaseOpaque {
    func refinedValue() -> Int32
}

public protocol RPABaseBound: AnyObject {
    func baseValue() -> Int32
}

public protocol RPARefinedBound: RPABaseBound {
    func refinedValue() -> Int32
}

public protocol RPABoundRefinesOpaque: AnyObject, RPABaseOpaque {
    func refinedValue() -> Int32
}

public struct RPAInlineValue: RPARefinedOpaque {
    public let seed: Int32
    public init(seed: Int32) { self.seed = seed }
    public func baseValue() -> Int32 { seed * 2 + 1 }
    public func refinedValue() -> Int32 { seed + 1000 }
}

public struct RPABoxedValue: RPARefinedOpaque {
    public let seed: Int32
    public let padding0: Int64
    public let padding1: Int64
    public let padding2: Int64
    public init(seed: Int32) {
        self.seed = seed
        padding0 = 0x0101; padding1 = 0x0202; padding2 = 0x0303
    }
    public func baseValue() -> Int32 { seed * 2 + 1 + Int32(truncatingIfNeeded: padding2 - 0x0303) }
    public func refinedValue() -> Int32 { seed + 1000 }
}

public final class RPAObject: RPARefinedBound, RPABoundRefinesOpaque {
    public let seed: Int32
    public init(seed: Int32) { self.seed = seed }
    public func baseValue() -> Int32 { seed * 2 + 1 }
    public func refinedValue() -> Int32 { seed + 1000 }
}

public func rpaMakeRefinedInline(seed: Int32) -> any RPARefinedOpaque { RPAInlineValue(seed: seed) }
public func rpaMakeRefinedBoxed(seed: Int32) -> any RPARefinedOpaque { RPABoxedValue(seed: seed) }
public func rpaMakeRefinedBound(seed: Int32) -> any RPARefinedBound { RPAObject(seed: seed) }
public func rpaMakeBoundRefinesOpaque(seed: Int32) -> any RPABoundRefinesOpaque { RPAObject(seed: seed) }

/// The base requirement, read through the base protocol's witness table.
public func rpaReadBaseOpaque(_ value: any RPABaseOpaque) -> Int32 { value.baseValue() }
public func rpaReadBaseBound(_ value: any RPABaseBound) -> Int32 { value.baseValue() }

/// The refined requirement, read through the refined protocol's own witness table.
public func rpaReadRefinedOpaque(_ value: any RPARefinedOpaque) -> Int32 { value.refinedValue() }
public func rpaReadRefinedBound(_ value: any RPARefinedBound) -> Int32 { value.refinedValue() }

/// The base requirement of an optional base existential, or -1 for nil.
public func rpaReadOptionalBaseBound(_ value: (any RPABaseBound)?) -> Int32 { value?.baseValue() ?? -1 }
