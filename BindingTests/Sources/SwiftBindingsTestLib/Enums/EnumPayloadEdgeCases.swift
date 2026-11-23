// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Simple enums inside a payload tuple
//
// A payload case whose tuple holds a simple enum is laid out by Swift with the enum's own layout:
// a Swift raw-value enum is stored as its case tag (one byte here), an @objc enum as its raw value.
// The byte after the enum sits at an offset only the enum's real layout gives.

public enum RawTuplePayload {
    case raw(AsyncTupleRaw, UInt8)
    case small(AsyncTupleSmall, UInt8)
    case objc(AuthErrorCodeLike, UInt8)
    case empty
}

public func rawTuplePayloadRaw() -> RawTuplePayload { .raw(.high, 42) }
public func rawTuplePayloadSmall() -> RawTuplePayload { .small(.big, 43) }
public func rawTuplePayloadObjC() -> RawTuplePayload { .objc(.userNotFound, 44) }
public func rawTuplePayloadName(_ value: RawTuplePayload) -> String {
    switch value {
    case .raw(let e, let b): return "raw:\(e.rawValue):\(b)"
    case .small(let e, let b): return "small:\(e.rawValue):\(b)"
    case .objc(let e, let b): return "objc:\(e.rawValue):\(b)"
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
