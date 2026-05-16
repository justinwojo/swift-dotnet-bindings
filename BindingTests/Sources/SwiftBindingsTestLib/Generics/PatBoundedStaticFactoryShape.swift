// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Regression fixture — WeatherKit.DailyWeatherStatisticsQuery<T> static-factory
// shape. A generic struct constrained by a Self-requirement protocol set
// (Decodable & Encodable & Equatable & Sendable) declares STATIC properties
// returning a different *closed* bound generic of itself
// (`Query<T>.preset: Query<ConcretePayload>`).
//
// Pre-fix MemberEmissionValidator (parent-baseline PAT accessor check at
// MemberEmissionValidator.cs:404-434) rejects these accessors with
// `GenericProtocolConstraint`, even though the accessor signature does NOT
// leak the parent's open generic parameter `T` — return type is fully closed,
// no params, and `T`'s identity is irrelevant to the call (Swift dispatches a
// single symbol that takes T's metadata + PWTs as ignored hidden args).
//
// The fix admits static accessors whose signature does not reference the
// parent's open T and whose return type is fully closed; the wrapper
// hard-codes the concrete `T'` from the declared return type so the Swift
// compiler resolves all metadata + PWTs at compile time. C# users invoke
// `PatBoundedStatsQuery<Whatever>.PresetA` (type arg ignored; return type
// always the declared closed instantiation).

import Foundation

public struct StatPayloadA: Decodable, Encodable, Equatable, Sendable {
    public let label: String
    public init(label: String) { self.label = label }
}

public struct StatPayloadB: Decodable, Encodable, Equatable, Sendable {
    public let value: Int32
    public init(value: Int32) { self.value = value }
}

/// Mirrors `WeatherKit.DailyWeatherStatisticsQuery<T>` shape — parent generic
/// struct with a 4-protocol PAT/Self-requirement constraint set, exposing
/// static-only factory properties that return closed instantiations.
public struct PatBoundedStatsQuery<T: Decodable & Encodable & Equatable & Sendable> {
    public init() {}

    /// Closed-instantiation static factory — return type does not reference T.
    public static var presetA: PatBoundedStatsQuery<StatPayloadA> {
        return PatBoundedStatsQuery<StatPayloadA>()
    }

    /// Second static factory in same shape, distinguishes from presetA so the
    /// per-accessor symbol path is exercised (one symbol per accessor, not
    /// shared).
    public static var presetB: PatBoundedStatsQuery<StatPayloadB> {
        return PatBoundedStatsQuery<StatPayloadB>()
    }
}
