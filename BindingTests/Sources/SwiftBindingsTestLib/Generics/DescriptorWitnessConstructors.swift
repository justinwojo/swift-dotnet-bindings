// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Generic structs whose constraint sets mix Self-requirement / associated-type protocols
// (Decodable, Encodable, Equatable) with the Sendable marker. The metadata plus three witness
// tables exceed the slots the constructor wrapper threads, so each initializer below binds as a
// direct Swift-convention call against the allocating init. That callee takes one witness table
// per conformance after the generic metadata: a call that passes fewer leaves the init reading
// whatever happens to sit in those argument registers.
//
// Each init body reads every one of its witness tables, so a call that drops them faults or
// returns wrong values instead of passing by luck the way an empty init would.

import Foundation

/// Payload with a fixed JSON encoding (`{"code":N}`), used as the generic argument.
public struct WitnessPayload: Codable, Equatable, Sendable {
    public let code: Int32
    public init(code: Int32) { self.code = code }
}

/// The initializer takes a value of the generic parameter (the `CodableScalarBox` shape).
public struct WitnessReadingBox<V: Codable & Equatable & Sendable> {
    public let value: V
    /// `value == probe`, through the Equatable witness table.
    public let matchesProbe: Bool
    /// Byte count of the JSON encoding of `value`, through the Encodable witness table; -1 on failure.
    public let encodedLength: Int
    /// Whether that encoding decodes back to `value`, through the Decodable witness table.
    public let roundTrips: Bool

    public init(value: V, probe: V) {
        self.value = value
        self.matchesProbe = value == probe
        let encoded = try? JSONEncoder().encode(value)
        self.encodedLength = encoded?.count ?? -1
        if let encoded, let decoded = try? JSONDecoder().decode(V.self, from: encoded) {
            self.roundTrips = decoded == value
        } else {
            self.roundTrips = false
        }
    }
}

/// The initializer's signature never mentions the generic parameter (the `PatBoundedStatsQuery`
/// shape), so nothing about its parameters marks it as generic-sensitive.
public struct WitnessReadingQuery<T: Decodable & Encodable & Equatable & Sendable> {
    /// Whether `json` decodes as a `T`, through the Decodable witness table.
    public let decoded: Bool
    /// Whether the decoded value re-encodes and decodes to an equal value, through the Encodable
    /// and Equatable witness tables.
    public let roundTrips: Bool

    public init(json: String) {
        guard let first = try? JSONDecoder().decode(T.self, from: Data(json.utf8)) else {
            self.decoded = false
            self.roundTrips = false
            return
        }
        self.decoded = true
        if let encoded = try? JSONEncoder().encode(first),
           let second = try? JSONDecoder().decode(T.self, from: encoded) {
            self.roundTrips = first == second
        } else {
            self.roundTrips = false
        }
    }
}
