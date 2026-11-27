// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Swift members named like the members every projected type carries
//
// A projected struct or class carries its own members beside the Swift ones: a `Payload` handle
// and `Dispose()` on every owner, and `PayloadBuffer` and a nested `Buffer` on a frozen struct
// projected as a class. A Swift property or method spelled the same way must take another C#
// name rather than collide with them. Each type below reads its fields back on the Swift side, so
// a write through a renamed member that landed on the wrong field shows as a wrong value.

/// Frozen and holding a `String`, so it is projected as a class with a `Buffer`.
@frozen public struct InfraNamedFrozenRecord {
    public var payload: String
    public var payloadBuffer: Int
    public var buffer: Int
    public var dispose: Int
    public init(payload: String, payloadBuffer: Int, buffer: Int, dispose: Int) {
        self.payload = payload; self.payloadBuffer = payloadBuffer; self.buffer = buffer; self.dispose = dispose
    }
    public var summary: String { "\(payload)/\(payloadBuffer)/\(buffer)/\(dispose)" }
    public func buffer(at index: Int) -> Int { buffer * 100 + index }
}

/// Not frozen: projected as a class over an opaque payload.
public struct InfraNamedResilientRecord {
    public var payload: String
    public var dispose: Int
    public init(payload: String, dispose: Int) { self.payload = payload; self.dispose = dispose }
    public var summary: String { "\(payload)/\(dispose)" }
    public func payload(prefix: String) -> String { prefix + payload }
}

/// Frozen and plain data: projected as a C# struct, which still carries `Dispose()`.
@frozen public struct InfraNamedValueRecord {
    public var payload: Int
    public var dispose: Int
    public init(payload: Int, dispose: Int) { self.payload = payload; self.dispose = dispose }
    public var summary: String { "\(payload)/\(dispose)" }
}

public final class InfraNamedRecordClass {
    public var payload: String
    public var dispose: Int
    public init(payload: String, dispose: Int) { self.payload = payload; self.dispose = dispose }
    public var summary: String { "\(payload)/\(dispose)" }
}

public final class InfraNamedMethodClass {
    public init() {}
    public func payload(prefix: String) -> String { prefix + "-method" }
}
