// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Enum payloads typed as a zero-witness existential: bare `Any` and marker-only
// compositions like `any Sendable`. Both share the ExistentialContainer0 layout (no
// witness table, since marker protocols have none) and project to `object` through
// ExistentialContainer0.Box/Unbox, the same as a method parameter or return of that type.
//
// The enum payload paths used to treat `any Sendable` as an ordinary protocol existential:
// the TryGet body constructed a `SendableProxy` no emitter writes (CS0246), the tuple
// metadata accessor asked for a one-witness existential, and the case factory cast the
// `object` argument to ISwiftExistentialConvertible. A throwing error enum with a labeled
// `value: any Sendable` payload is the common real-world shape.

public enum ZeroWitnessPayload {
    case attribute(name: String, value: any Sendable)
    case sendable(any Sendable)
    case anything(Any)
    case empty
}

public func makeZeroWitnessAttribute(name: String, intValue: Int) -> ZeroWitnessPayload {
    .attribute(name: name, value: intValue)
}

public func makeZeroWitnessAttributeString(name: String, stringValue: String) -> ZeroWitnessPayload {
    .attribute(name: name, value: stringValue)
}

public func makeZeroWitnessSendable(_ value: Double) -> ZeroWitnessPayload {
    .sendable(value)
}

public func makeZeroWitnessAnything(_ value: String) -> ZeroWitnessPayload {
    .anything(value)
}

/// Describes the payload from the Swift side, so a case built in C# is checked by Swift.
public func describeZeroWitnessPayload(_ payload: ZeroWitnessPayload) -> String {
    switch payload {
    case let .attribute(name, value):
        return "attribute(\(name)=\(value))"
    case let .sendable(value):
        return "sendable(\(value))"
    case let .anything(value):
        return "anything(\(value))"
    case .empty:
        return "empty"
    }
}
