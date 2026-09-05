// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Non-failable-init overload-collapse dedup naming.
//
// Ordinary (non-failable) `init`s whose Swift argument labels differ but whose parameter TYPES are
// identical erase to one projected C# constructor signature. They survive the primary label-inclusive
// dedup and collide at the secondary projected-C# dedup, where a constructor used to be unrecoverable —
// a constructor's C# name is the enclosing type's — so the first claimant emitted and every colliding
// sibling was dropped as DuplicateSignature. These are different operations, not redundant overloads;
// dropping them deletes half the type's construction surface. The shape was reported on a payments SDK
// whose sheet-configuration type declares `init(paymentIntentClientSecret:configuration:)` beside
// `init(setupIntentClientSecret:configuration:)`.
//
// The recovery mirrors the failable lane: a colliding initializer is emitted as a static
// `CreateWith{Labels}` factory instead of being dropped. Which member keeps the plain constructor is
// decided by CONTENT, not declaration order — the fully positional member owns the bare slot, and only
// when exactly one member is fully positional; otherwise nobody owns it and every member of the family
// becomes a factory. A re-ordered `.swiftinterface` therefore cannot silently re-point an existing
// `new T(a, b)` call at a different Swift initializer.
//
// Distinct `dispatchMarker` values prove WHICH Swift init body each emitted constructor/factory reaches.

import Foundation

/// Three-way collision, every member labeled: no member owns the bare constructor slot, so all three
/// recover as label-named factories and the type has no plain `(string, int)` constructor at all.
public final class LabeledSessionHandle {
    public let dispatchMarker: String

    public init(paymentToken: String, retries: Int32) {
        self.dispatchMarker = "payment:\(paymentToken):\(retries)"
    }

    public init(setupToken: String, retries: Int32) {
        self.dispatchMarker = "setup:\(setupToken):\(retries)"
    }

    public init(customerToken: String, retries: Int32) {
        self.dispatchMarker = "customer:\(customerToken):\(retries)"
    }
}

/// Two-way collision with exactly one fully positional member. The positional init owns the plain
/// constructor; the labeled sibling recovers as a factory. Declared labeled-first on purpose: ownership
/// must follow the positional shape, not the order the members appear in.
public final class LabeledEndpointHandle {
    public let dispatchMarker: String

    public init(opaqueToken: String) {
        self.dispatchMarker = "opaque:\(opaqueToken)"
    }

    public init(_ raw: String) {
        self.dispatchMarker = "raw:\(raw)"
    }
}

/// The same collision on a frozen blittable struct, whose constructor terminal is a returned value
/// rather than an adopted class handle. One positional member keeps the constructor; the two labeled
/// members recover as factories.
@frozen
public struct LabeledPortDescriptor {
    public let dispatchMarker: Int32
    public let value: Int32

    public init(tcpPort: Int32) {
        self.dispatchMarker = 1
        self.value = tcpPort
    }

    public init(udpPort: Int32) {
        self.dispatchMarker = 2
        self.value = udpPort
    }

    public init(_ rawPort: Int32) {
        self.dispatchMarker = 0
        self.value = rawPort
    }
}
