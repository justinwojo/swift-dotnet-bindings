// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Regression fixtures for generic types whose single generic parameter is
// constrained by a Swift stdlib protocol that previously was missing from
// SwiftDatabase.xml (Equatable / Decodable / Encodable). Without the XML
// entries the runtime-metadata PWT slot could not be resolved and the
// enclosing type silently tombstoned — same shape as WeatherKit's
// `Forecast<TElement>`. Keep these fixtures minimal — the surface that must
// emit is the constrained generic itself and a factory that returns a
// concrete specialization over a conformer. Both a user-defined struct
// conformer (`EquatableTicket`) and a stdlib PRIMITIVE conformer (`Int`,
// projected as the C# `nint` value type) are covered. The primitive case is
// the load-bearing one: a Swift struct conformer always projects to a C# type
// that implements `ISwiftObject`, so it satisfies the historical seed
// regardless — only a primitive (which does NOT implement `ISwiftObject`)
// exercises the generator dropping the `ISwiftObject` seed for a
// descriptor-path-safe Self-requirement protocol like `Equatable`.

import Foundation

// MARK: - Concrete conformer used by all fixtures

public struct EquatableTicket: Equatable, Codable {
    public let id: Int32
    public init(id: Int32) {
        self.id = id
    }
}

// MARK: - Equatable-constrained generic (hasSelfRequirement = true)

public struct EquatableContainer<T: Equatable> {
    public let item: T

    public init(item: T) {
        self.item = item
    }

    public func matches(_ other: T) -> Bool {
        return item == other
    }
}

/// Concrete specialization — exercises the Equatable PWT slot.
public func makeEquatableContainer(id: Int32) -> EquatableContainer<EquatableTicket> {
    return EquatableContainer(item: EquatableTicket(id: id))
}

/// Primitive specialization. `Int` is already `Equatable` in the stdlib and
/// projects to the C# `nint` value type, which does NOT implement
/// `ISwiftObject`. `EquatableContainer<Int>` therefore only type-checks once
/// the generator drops the `ISwiftObject` seed — which it does because
/// `Equatable` is a Self-requirement (descriptor-path-safe) protocol whose PWT
/// arg flows through the unconstrained `TypeMetadata.GetTypeMetadataOrThrow<T>()`
/// path. The construct-via-factory + read-back-`item` round-trip on the C# side
/// is the durable gate for that seed drop.
public func makeIntEquatableContainer(value: Int) -> EquatableContainer<Int> {
    return EquatableContainer(item: value)
}

// MARK: - Decodable & Encodable-constrained generic (hasAssociatedTypes = true, x2)

public struct CodableContainer<T: Decodable & Encodable> {
    public let item: T

    public init(item: T) {
        self.item = item
    }
}

/// Concrete specialization — exercises the Decodable + Encodable PWT slots.
public func makeCodableContainer(id: Int32) -> CodableContainer<EquatableTicket> {
    return CodableContainer(item: EquatableTicket(id: id))
}
