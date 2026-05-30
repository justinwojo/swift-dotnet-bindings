// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Nested-type conformers in concrete protocol specialization
//
// Mirrors CryptoKit's HPKE key protocols, whose conformers are deeply-nested types
// such as `Curve25519.KeyAgreement.PublicKey` (a type nested two levels inside its
// module). The concrete-specialization engine historically rejected any conformer whose
// module-qualified name had more than two dot-separated segments, so HPKE
// Sender/Recipient initializers fell back to generic-only stubs. These fixtures exercise
// the flat, one-level, and two-level-nested conformer shapes through all three sync CSM
// emission shapes: a generic method (`KeyRegistrar`), a generic initializer (`SealedKey`),
// and a generic parent type (`KeyVaultBox`).

/// Constraint protocol whose conformers span flat and nested types.
public protocol NestedKeyMaterial {
    var material: String { get }
}

/// Flat (top-level) conformer — the baseline the specializer already handled.
@frozen
public struct FlatKeyMaterial: NestedKeyMaterial {
    public let tag: String
    public init(tag: String) { self.tag = tag }
    public var material: String { "flat:\(tag)" }
}

/// Namespace wrapper holding nested conformers.
public enum KeyVault {
    /// One-level-nested conformer: `KeyVault.VaultKey` (module + 2 segments).
    @frozen
    public struct VaultKey: NestedKeyMaterial {
        public let tag: String
        public init(tag: String) { self.tag = tag }
        public var material: String { "vault:\(tag)" }
    }

    /// Two-level-nested conformer: `KeyVault.Agreement.PublicKey` — exactly HPKE's
    /// `Curve25519.KeyAgreement.PublicKey` nesting depth (module + 3 segments).
    public enum Agreement {
        @frozen
        public struct PublicKey: NestedKeyMaterial {
            public let tag: String
            public init(tag: String) { self.tag = tag }
            public var material: String { "agree-pub:\(tag)" }
        }
    }
}

/// Non-generic host with a CSM method over `NestedKeyMaterial`. The specializer should
/// emit one concrete overload per conformer — including the nested ones. A concrete
/// `String` return isolates the nested-conformer dimension from any generic-return concern.
@frozen
public struct KeyRegistrar {
    public let realm: String
    public init(realm: String) { self.realm = realm }

    public func registerKey<K: NestedKeyMaterial>(_ key: K) -> String {
        return "\(realm)/\(key.material)"
    }
}

/// Generic initializer over a nested-conformer constraint — the HPKE Sender/Recipient
/// init shape. The specializer should emit one `From{Conformer}` static factory per
/// conformer, including the nested ones.
@frozen
public struct SealedKey {
    public let descriptor: String

    public init<K: NestedKeyMaterial>(sealing key: K) {
        self.descriptor = "sealed[\(key.material)]"
    }

    public init(descriptor: String) {
        self.descriptor = descriptor
    }
}

/// Generic parent whose CSM specializes the parent over a nested-conformer constraint —
/// exercises the generic-parent emission path (EmitConcreteSpecializationsForGenericParent).
/// The closed receiver (`KeyVaultBox<KeyVault.VaultKey>` etc.) ranges over nested conformers,
/// and `describe` emits a per-conformer extension method on each closed receiver. This is the
/// third CSM shape (alongside the generic method on `KeyRegistrar` and the generic init on
/// `SealedKey`) and routes through the same sync nested-conformer structural gate.
@frozen
public struct KeyVaultBox<T: NestedKeyMaterial> {
    public let seed: T
    public init(seed: T) { self.seed = seed }

    public func describe() -> String {
        return "box[\(seed.material)]"
    }
}

/// Collision-renamed nested conformer. `CollisionVault` exposes both an `entry` property and a
/// nested `Entry` type, so their C# projections clash (property `Entry` vs type `Entry`) and the
/// nested type is renamed to `EntryType` by the nested-type-collision pre-pass — exactly the
/// `Codec.Encoding` property/`Codec.Encoding` type shape that becomes `Codec.EncodingType`.
/// Because `Entry` also conforms to `NestedKeyMaterial`, every CSM shape (method, init factory,
/// generic-parent extension) must reference this conformer by its *post-rename* C# name. The
/// conformer's C# name is cached at conformance-index time, before the rename pre-pass runs, so
/// without re-resolving the live name at emission these overloads would name the non-existent
/// `CollisionVault.Entry` and fail to compile. This is the conformer that actually exercises the
/// rename branch the other fixtures above leave dormant (they have no member/type clash).
@frozen
public struct CollisionVault {
    // The property's *type* is the nested type itself, so the C# projections collide as
    // `Entry` (property) vs `Entry` (type). That exact shape routes the collision pre-pass to
    // rename the nested TYPE with a "Type" suffix (-> `EntryType`) while the property keeps the
    // clean name — the `Codec.encoding: Encoding` / `Codec.Encoding` precedent. A property of an
    // unrelated type (e.g. `String`) would instead rename the property, leaving the type un-renamed
    // and the re-resolve branch dormant.
    public let entry: Entry
    public init(entry: Entry) { self.entry = entry }

    @frozen
    public struct Entry: NestedKeyMaterial {
        public let tag: String
        public init(tag: String) { self.tag = tag }
        public var material: String { "collision-entry:\(tag)" }
    }
}
