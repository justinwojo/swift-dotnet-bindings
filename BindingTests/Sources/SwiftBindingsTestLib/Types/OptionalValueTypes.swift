// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional Frozen Struct

/// Optional frozen struct parameter — value type uses LargeOptionalPointer or FullSwiftOptional path.
public func acceptOptionalFrozenPoint(_ point: FrozenPoint?) -> String {
    guard let p = point else { return "nil" }
    return "(\(p.x), \(p.y))"
}

/// Returns an optional frozen struct — tests Optional<FrozenStruct> return marshalling.
public func makeOptionalFrozenPoint(_ x: Double, _ y: Double, returnNil: Bool) -> FrozenPoint? {
    if returnNil { return nil }
    return FrozenPoint(x: x, y: y)
}

// MARK: - Optional Non-Frozen Struct

/// Optional non-frozen struct parameter — uses DecomposedBuffers strategy.
public func acceptOptionalNonFrozenPoint(_ point: NonFrozenPoint?) -> String {
    guard let p = point else { return "nil" }
    return "(\(p.x), \(p.y))"
}

// MARK: - Optional Enum

/// Optional frozen enum parameter — value type with known raw representation.
public func acceptOptionalColor(_ color: Color?) -> String {
    guard let c = color else { return "nil" }
    switch c {
    case .red: return "red"
    case .green: return "green"
    case .blue: return "blue"
    case .alpha: return "alpha"
    @unknown default: return "unknown"
    }
}

/// Returns an optional enum — tests Optional<Enum> return marshalling.
public func makeOptionalColor(_ raw: Int32, returnNil: Bool) -> Color? {
    if returnNil { return nil }
    return Color(rawValue: raw)
}

// MARK: - Optional Bool

/// Optional Bool parameter — Bool uses extra inhabitants, requires FullSwiftOptional path.
public func acceptOptionalBool(_ flag: Bool?) -> String {
    guard let f = flag else { return "nil" }
    return f ? "true" : "false"
}

/// Returns an optional Bool.
public func makeOptionalBool(_ value: Bool, returnNil: Bool) -> Bool? {
    if returnNil { return nil }
    return value
}
