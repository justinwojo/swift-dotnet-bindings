// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Typealias-wrapped return types (CryptoKit SHA256.Digest pattern)
//
// Apple frameworks frequently expose nested typealiases that point to a top-level
// type — e.g. CryptoKit's `SHA256.Digest = SHA256Digest`, `HMAC<H>.MAC =
// HashedAuthenticationCode<H>`. swift-api-digester encodes a use of such a
// nested alias as a TypeNameAlias node wrapping the underlying TypeNominal.
// Before the parser handled TypeNameAlias, every method whose return or parameter
// referenced one of these aliases was silently dropped at parse time.

/// Concrete payload that the alias resolves to.
public struct AliasPayload {
    public let value: Int32
    public init(value: Int32) {
        self.value = value
    }
}

/// Producer with a nested typealias used in its return type.
public struct AliasProducer {
    /// The nested alias the digester will encode as TypeNameAlias.
    public typealias Payload = AliasPayload

    private let seed: Int32

    public init(seed: Int32) {
        self.seed = seed
    }

    /// Returns the alias-wrapped payload.
    /// Generator must unwrap the TypeNameAlias node and bind the method to AliasPayload.
    public func makePayload() -> Payload {
        return AliasPayload(value: seed * 2)
    }
}

/// Generic producer with a nested generic alias (HMAC<H>.MAC pattern).
public struct AliasGenericProducer<T> {
    /// Nested alias whose underlying type is generic in the parent type parameter.
    public typealias Wrapped = AliasGenericPayload<T>

    private let seed: T

    public init(seed: T) {
        self.seed = seed
    }

    public func makeWrapped() -> Wrapped {
        return AliasGenericPayload(element: seed)
    }
}

/// Field is named `element` rather than `payload` because the C# emitter exposes a
/// runtime-backed `Payload` SafeHandle on every Swift struct, which would collide.
public struct AliasGenericPayload<T> {
    public let element: T
    public init(element: T) {
        self.element = element
    }
}

/// Simple Swift class used to cover the class-T case of
/// `AliasGenericPayload<T>.element` — Swift writes the class instance pointer into
/// the indirect-result buffer, so the C# side must dereference the buffer before
/// wrapping the handle (distinct ownership from struct T, where the buffer itself
/// is the handle). Class conformers must not be confused with struct conformers.
public final class AliasClassItem {
    public let tag: Int32
    public init(tag: Int32) {
        self.tag = tag
    }
}
