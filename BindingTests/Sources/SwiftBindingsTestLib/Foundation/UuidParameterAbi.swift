// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Foundation.UUID as an @_cdecl parameter

// `Foundation.UUID` is a frozen 16-byte Swift struct, but it is also `_ObjectiveCBridgeable`
// (to `NSUUID`). Under `@_cdecl` an ObjC-bridgeable struct parameter does NOT lower to its 16
// value bytes — Swift lowers it to a single `NSUUID` object pointer and bridges on entry. A
// caller that hands over the raw 16 bytes therefore has its first 8 bytes read as an object
// pointer, and every later argument is shifted by a slot. The value side and the parameter
// side must agree: the return side reinterprets the 16 UUID bytes verbatim, so the parameter
// side has to be the exact inverse of that.
//
// The functions below pin that agreement at each boundary shape the generator emits for a
// parameter: initializer, instance method, static method, free function, property setter,
// optional parameter, and a parameter pushed past the integer-register boundary.

/// Returns every byte of `value` in Swift's declared `uuid` tuple order.
private func uuidBytes(_ value: UUID) -> [UInt8] {
    let u = value.uuid
    return [u.0, u.1, u.2, u.3, u.4, u.5, u.6, u.7,
            u.8, u.9, u.10, u.11, u.12, u.13, u.14, u.15]
}

/// Sum of every byte of `value`, widened so no realistic UUID overflows it.
private func uuidByteSum(_ value: UUID) -> Int64 {
    uuidBytes(value).reduce(Int64(0)) { $0 &+ Int64($1) }
}

/// Holds a `UUID` handed in through an `@_cdecl` initializer and exposes it back through a
/// getter, a settable property, and instance/static methods that consume further `UUID` values.
public class DeviceRegistration {
    /// Stored from the initializer parameter — a round-trip of this against the value the
    /// caller passed is the identity check for the parameter lowering.
    public let identifier: UUID

    /// Settable `UUID` property: the setter is emitted as its own `@_cdecl` wrapper whose sole
    /// parameter is the new value, which is a different lowering site from a method parameter.
    public var replacementIdentifier: UUID

    public init(identifier: UUID) {
        self.identifier = identifier
        self.replacementIdentifier = identifier
    }

    /// Instance method taking a `UUID`: returns a value derived from the argument's bytes, so a
    /// mis-lowered argument cannot accidentally produce the expected answer.
    public func byteSum(of value: UUID) -> Int64 {
        uuidByteSum(value)
    }

    /// Instance method proving the stored initializer argument survived, byte for byte.
    public func storedByteSum() -> Int64 {
        uuidByteSum(identifier)
    }

    /// Instance method reading back the settable property's bytes.
    public func replacementByteSum() -> Int64 {
        uuidByteSum(replacementIdentifier)
    }

    /// Static method taking a `UUID` — a distinct wrapper shape (no `self` receiver).
    public static func firstByte(of value: UUID) -> Int32 {
        Int32(uuidBytes(value)[0])
    }
}

/// Free-function wrapper taking a `UUID` by value.
public func uuidByteSumOf(_ value: UUID) -> Int64 {
    uuidByteSum(value)
}

/// Swift's own textual rendering of a `UUID` parameter. Swift prints the 16 bytes in declaration
/// order; the .NET `Guid` textual form reverses the first three groups on a little-endian host, so
/// this lets a caller assert the byte-order convention explicitly instead of assuming it.
public func uuidTextOf(_ value: UUID) -> String {
    value.uuidString
}

/// Optional `UUID` parameter: `nil` must arrive as `nil` and a value must arrive intact.
/// Returns -1 for `nil`, otherwise the byte sum.
public func optionalUuidByteSum(_ value: UUID?) -> Int64 {
    guard let value else { return -1 }
    return uuidByteSum(value)
}

/// Seven leading `Int64` arguments fill the integer argument registers, so `value` starts at or
/// beyond the last one. A lowering that disagrees with the caller about how many words a `UUID`
/// occupies corrupts the payload here even when it happens to work in the first argument slot.
public func uuidPastRegisterBoundary(
    a0: Int64, a1: Int64, a2: Int64, a3: Int64, a4: Int64, a5: Int64, a6: Int64, value: UUID
) -> Int64 {
    let argSum = a0 &+ a1 &+ a2 &+ a3 &+ a4 &+ a5 &+ a6
    return argSum &+ uuidByteSum(value)
}

/// Two `UUID` parameters in a row, so a lowering that consumes the wrong number of argument slots
/// for the first one reads the second from the wrong place.
public func uuidPairByteSums(_ first: UUID, _ second: UUID) -> Int64 {
    (uuidByteSum(first) &* 1000) &+ uuidByteSum(second)
}

/// Builds a `UUID` from 16 explicit bytes so a caller can construct the exact same value on both
/// sides of the boundary without depending on any textual convention.
public func makeUuidFromBytes(
    _ b0: UInt8, _ b1: UInt8, _ b2: UInt8, _ b3: UInt8,
    _ b4: UInt8, _ b5: UInt8, _ b6: UInt8, _ b7: UInt8,
    _ b8: UInt8, _ b9: UInt8, _ b10: UInt8, _ b11: UInt8,
    _ b12: UInt8, _ b13: UInt8, _ b14: UInt8, _ b15: UInt8
) -> UUID {
    UUID(uuid: (b0, b1, b2, b3, b4, b5, b6, b7, b8, b9, b10, b11, b12, b13, b14, b15))
}
