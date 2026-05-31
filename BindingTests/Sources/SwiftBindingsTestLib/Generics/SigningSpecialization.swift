// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Method-level DataProtocol generics in signing methods (Ed25519-shaped)
//
// Mirrors CryptoKit's signing surface, where the message is a method-level
// `DataProtocol` generic on a NON-generic, deeply-nested key type:
//   Curve25519.Signing.PrivateKey.signature<D: DataProtocol>(for: D) throws -> Data
//   P256.Signing.PrivateKey.signature<D: DataProtocol>(for: D) throws -> ECDSASignature
//   Curve25519.Signing.PublicKey.isValidSignature<S, D>(_: S, for: D) -> Bool
//
// Empirically, the concrete-specialization engine already concretizes the *parameter*
// side: P256's `Signature<D>` (returns an ISwiftObject `ECDSASignature`) and Ed25519's
// `IsValidSignature<S, D>` (returns `Bool`) both get concrete per-conformer overloads.
// The one shape that fell through to a generic-only SB0001 stub is Ed25519's
// `signature(for:)`: its return type is `Foundation.Data`, which projects to the C#
// `byte[]` value type — not an ISwiftObject — so the CSM return-type preflight's
// indirect-result-must-be-ISwiftObject gate rejected it and C# could not *produce* an
// Ed25519 signature (only verify one). These fixtures pin all three shapes so the
// byte[]-return path is permanently covered alongside the already-working controls.

/// Constraint protocol standing in for `DataProtocol` — a byte carrier with module-local
/// conformers, so the engine resolves conformers from the ABI (the module-local analog of
/// how the real DataProtocol conformers `Foundation.Data`/`[UInt8]` arrive from hints).
public protocol MessageBytes {
    var rawBytes: [UInt8] { get }
}

@frozen
public struct PlainMessage: MessageBytes {
    public let text: String
    public init(text: String) { self.text = text }
    public var rawBytes: [UInt8] { Array(text.utf8) }
}

@frozen
public struct ContextTag: MessageBytes {
    public let label: String
    public init(label: String) { self.label = label }
    public var rawBytes: [UInt8] { Array(label.utf8) }
}

/// Concrete ISwiftObject signature value (the P256 `ECDSASignature` analog) — the control
/// proving that a method-level-generic signing method whose *return* is an ISwiftObject is
/// already specialized. Holds a `String` payload field (renamed off `Payload` to avoid the
/// runtime SafeHandle accessor collision on a ClassWithBufferStruct).
@frozen
public struct SignatureBlob {
    public let descriptor: String
    public init(descriptor: String) { self.descriptor = descriptor }
}

/// Ed25519-shaped namespace: a two-level-nested signing key (mirrors
/// `Curve25519.Signing.PrivateKey`). `sign(for:)` returns `Foundation.Data` (projects to
/// `byte[]`) — the failing case. `signBlob(for:)` returns the ISwiftObject `SignatureBlob`
/// — the passing control. `verify(_:for:)` returns `Bool` — the already-working param-side
/// control (the `IsValidSignature` analog).
public enum EdCurve {
    public enum Signing {
        @frozen
        public struct PrivateKey {
            public let seed: String
            public init(seed: String) { self.seed = seed }

            // Failing case: byte[]-projecting (`Foundation.Data`) return.
            public func sign<D: MessageBytes>(for message: D) throws -> Data {
                return Data(message.rawBytes + Array("ed[\(seed)]".utf8))
            }

            // Passing control: ISwiftObject (struct) return.
            public func signBlob<D: MessageBytes>(for message: D) throws -> SignatureBlob {
                return SignatureBlob(descriptor: "ed[\(seed)]:\(message.rawBytes.count)")
            }

            // Passing control: Bool return, two method-level generics.
            public func verify<S: MessageBytes, D: MessageBytes>(_ signature: S, for message: D) -> Bool {
                return signature.rawBytes.count >= message.rawBytes.count
            }

            // Context-string case (the `Signature<D,C>` shape): TWO method-level generics
            // whose return is still `Foundation.Data` (byte[]-projecting). This is the
            // cartesian-expansion analog of `sign(for:)` — the engine must emit a concrete
            // overload for every (D, C) conformer pair, each routing the same byte[]-return
            // through the relaxed indirect-result gate. Proves the gate fix (which lives in
            // the pairing-independent return preflight) covers the multi-generic path, not
            // just the single-generic one.
            public func signWithContext<D: MessageBytes, C: MessageBytes>(
                for message: D, context: C) throws -> Data {
                return Data(message.rawBytes + Array("|".utf8) + context.rawBytes
                    + Array("ed[\(seed)]".utf8))
            }

            // Context-string verify (the `IsValidSignature<S,D,C>` shape): THREE method-level
            // generics, Bool return. Rounds out the documented context-string surface; the
            // Bool return path already works, so this pins the 3-way cartesian expansion.
            public func verifyWithContext<S: MessageBytes, D: MessageBytes, C: MessageBytes>(
                _ signature: S, for message: D, context: C) -> Bool {
                return signature.rawBytes.count >= message.rawBytes.count + context.rawBytes.count
            }
        }
    }
}
