// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Simple enums inside a payload tuple
//
// A payload case whose tuple holds a simple enum is laid out by Swift with the enum's own layout:
// a Swift raw-value enum is stored as its case tag (one byte here), an @objc enum as its raw value.
// The byte after the enum sits at an offset only the enum's real layout gives. A lone @objc enum
// payload is read the same way: as its whole raw value, not a one-byte tag.

public enum RawTuplePayload {
    case raw(AsyncTupleRaw, UInt8)
    case small(AsyncTupleSmall, UInt8)
    case objc(AuthErrorCodeLike, UInt8)
    case objcAlone(AuthErrorCodeLike)
    case empty
}

public func rawTuplePayloadRaw() -> RawTuplePayload { .raw(.high, 42) }
public func rawTuplePayloadSmall() -> RawTuplePayload { .small(.big, 43) }
public func rawTuplePayloadObjC() -> RawTuplePayload { .objc(.userNotFound, 44) }
public func rawTuplePayloadObjCAlone() -> RawTuplePayload { .objcAlone(.networkError) }
public func rawTuplePayloadName(_ value: RawTuplePayload) -> String {
    switch value {
    case .raw(let e, let b): return "raw:\(e.rawValue):\(b)"
    case .small(let e, let b): return "small:\(e.rawValue):\(b)"
    case .objc(let e, let b): return "objc:\(e.rawValue):\(b)"
    case .objcAlone(let e): return "objcAlone:\(e.rawValue)"
    case .empty: return "empty"
    }
}

// MARK: - A payload case named after the generated class's handle

/// `payload` would project a factory `Payload(...)` beside the generated class's `Payload` handle.
public enum PayloadNamedCase {
    case payload(Int)
    case label(String)
    case none
}

public func payloadNamedCaseMake(_ value: Int) -> PayloadNamedCase { .payload(value) }
public func payloadNamedCaseName(_ value: PayloadNamedCase) -> String {
    switch value {
    case .payload(let n): return "payload:\(n)"
    case .label(let s): return "label:\(s)"
    case .none: return "none"
    }
}

// MARK: - Members sharing the generated handle's name

/// A case and a property both named `payload`: the two take one rename between them, and the
/// property then yields to the factory.
public enum PayloadSharedName {
    case payload(Int)
    case other
    public var payload: Int { if case .payload(let value) = self { return value }; return -1 }
}

/// A property returning a nested type named `Payload`: the nested type is renamed for the property,
/// and the property still yields to the generated handle.
public enum PayloadNestedName {
    public struct Payload {
        public var weight: Int
        public init(weight: Int) { self.weight = weight }
    }
    case data(Int)
    case empty
    public var payload: Payload { if case .data(let value) = self { return Payload(weight: value) }; return Payload(weight: 0) }
}
