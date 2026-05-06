// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Equatable Enum-as-Class

/// Equatable enum with associated values. Lowers to a C# class because
/// payload cases prevent the C# enum value-type projection. Without the
/// EnumEqualityMethodsWriter bridge, the synthesized class would inherit
/// reference equality from System.Object — silently dropping Swift's
/// per-case `==` semantics. Matches the WeatherKit/Foundation pattern.
public enum EquatablePayloadEnum: Equatable {
    case empty
    case integer(Int32)
    case labelled(name: String, count: Int32)
}

/// Free-function constructors and helpers exercise the round-trip from C#:
/// build the enum on the Swift side, hand it back across the boundary, and
/// assert the Equatable semantics survive.
public func equatablePayloadInteger(_ value: Int32) -> EquatablePayloadEnum {
    return .integer(value)
}

public func equatablePayloadLabelled(name: String, count: Int32) -> EquatablePayloadEnum {
    return .labelled(name: name, count: count)
}

public func equatablePayloadEmpty() -> EquatablePayloadEnum {
    return .empty
}

/// Cross-check: hand both values to Swift and rely on its synthesized `==`.
/// If the C# generator's class-shape equality is correct, the .NET path and
/// this Swift path must agree byte-for-byte.
public func equatablePayloadEnumSwiftEquals(_ lhs: EquatablePayloadEnum, _ rhs: EquatablePayloadEnum) -> Bool {
    return lhs == rhs
}
