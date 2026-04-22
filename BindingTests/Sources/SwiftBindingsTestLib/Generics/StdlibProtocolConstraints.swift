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
// concrete specialization over a user-defined Equatable/Codable conformer
// (primitive generics like `Int32` bring their own unrelated ISwiftObject
// constraint issue, out of scope for this regression).

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
