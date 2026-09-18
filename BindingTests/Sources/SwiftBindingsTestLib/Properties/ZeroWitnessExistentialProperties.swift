// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Zero-witness existential stored properties on an @_cdecl-wrapped type
//
// A stored property typed as bare `Any` or as a marker-only composition (`any Sendable`) carries
// no witness table: its container is the four-word ExistentialContainer0 and its public C# type is
// `object`. Every accessor of an NSObject subclass routes through an @_cdecl wrapper, so the setter
// has to box the assigned C# value into a container of its own, hand Swift a pointer to it, and
// destroy that container once Swift has copied the value into the property; the getter receives a
// container Swift copied out at +1, unboxes it, and destroys it. The optional forms are refused
// today (their inner existential has no TypeDatabase entry) and stay here as the refusal's witness.
//
// Each `describe…` method reports what the property holds as `<dynamic type>:<value>`, so a
// boxing mistake shows up as the wrong type rather than as a matching string.

public class ZeroWitnessPropertyHolder: NSObject {
    public var anyValue: Any = 0
    public var sendableValue: any Sendable = 0
    public var optionalAny: Any?
    public var optionalSendable: (any Sendable)?

    public override init() {
        super.init()
    }

    public func describeAnyValue() -> String {
        "\(type(of: anyValue)):\(anyValue)"
    }

    public func describeSendableValue() -> String {
        "\(type(of: sendableValue)):\(sendableValue)"
    }

    public func describeOptionalAny() -> String {
        guard let value = optionalAny else { return "nil" }
        return "\(type(of: value)):\(value)"
    }

    public func describeOptionalSendable() -> String {
        guard let value = optionalSendable else { return "nil" }
        return "\(type(of: value)):\(value)"
    }

    /// Drops every stored value, so anything the setter boxed is released by Swift alone.
    public func reset() {
        anyValue = 0
        sendableValue = 0
        optionalAny = nil
        optionalSendable = nil
    }
}
