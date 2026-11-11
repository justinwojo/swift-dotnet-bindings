// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// A generic struct constrained `Codable & Equatable & Sendable`, bound to stdlib
// scalar arguments. The generator proves `Swift.Float : Swift.Decodable` and the
// like from its stdlib conformance table, so every scalar the table covers must
// carry the Codable pair there; a missing scalar silently drops every member typed
// with the bound generic.

import Foundation

public struct CodableScalarBox<V: Codable & Equatable & Sendable>: Equatable, Sendable {
    public let value: V

    public init(value: V) {
        self.value = value
    }
}

public struct CodableScalarReadings {
    public let gain: CodableScalarBox<Float>
    public let offset: CodableScalarBox<Double>
    public let count: CodableScalarBox<Int32>
    public let level: CodableScalarBox<UInt8>
    public let enabled: CodableScalarBox<Bool>
    public let label: CodableScalarBox<String>

    public init(gain: Float, offset: Double, count: Int32, level: UInt8, enabled: Bool, label: String) {
        self.gain = CodableScalarBox(value: gain)
        self.offset = CodableScalarBox(value: offset)
        self.count = CodableScalarBox(value: count)
        self.level = CodableScalarBox(value: level)
        self.enabled = CodableScalarBox(value: enabled)
        self.label = CodableScalarBox(value: label)
    }
}

public func makeCodableFloatBox(_ value: Float) -> CodableScalarBox<Float> {
    return CodableScalarBox(value: value)
}
