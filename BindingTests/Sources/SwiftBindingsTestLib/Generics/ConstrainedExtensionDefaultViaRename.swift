// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Constrained-extension protocol default reached through a renamed nested type
//
// Reproduces a nested-struct inline-conformance regression: a nested struct that inline-conforms
// to a protocol whose required members are provided ONLY by a constrained protocol
// extension (`extension P where Self: RawRepresentable, Self.RawValue: P { ... }`). The
// parent struct has a property whose C# name collides with the nested type name, forcing
// the nested-type-collision pre-pass to rename the nested type with a kind-aware suffix
// (`Kind` is a struct -> `Info` -> `KindInfo`), so the conformance must survive both the constrained-extension
// validator branch AND the post-rename name re-resolution branch.
//
// Pre-fix, `ProtocolExtensionDefaultsIndex` silently skipped any extension whose
// `WhereConstraints.Count > 0`, so `CanFullyImplementProtocol` saw the property
// requirement as "no member and no extension default" and rejected the conformance,
// dropping `IConstrainedDefaulted` from the C# interface list on `ConstraintHost.KindInfo`.
// `DefaultedCursor<KindInfo>` then failed CS0311 ("no implicit reference conversion to
// `IConstrainedDefaulted`"). The witness-table dictionary entry was emitted correctly —
// the gap was strictly in the interface declaration list.
//
// NOTE on shape: the where-clause uses the SAME protocol on both sides
// (`Self.RawValue: ConstrainedDefaulted`). The where-clause uses the SAME protocol on both
// sides, which is the pattern under test. Earlier drafts split the where-clause
// constraint into a separate `SummaryProvider` protocol, but that caused
// `EveryProtocol` to conform to two protocols both declaring `summary`, which fires
// the wrapper-emission sibling-fallback path with `var selfProto: SummaryProvider = self`
// inside ConstrainedDefaulted's body and creates a cyclic conformance Swift cannot
// resolve. The single-protocol shape avoids that unrelated wrapper path while still
// exercising the validator gap this fixture targets.

/// Protocol with one property + one method requirement. Both must be reachable through
/// a constrained extension default for a conformer to satisfy `CanFullyImplementProtocol`
/// without declaring either member directly.
public protocol ConstrainedDefaulted {
    var summary: String { get }
    func describe() -> String
}

/// Concrete `RawValue` carrier that itself conforms to `ConstrainedDefaulted` directly
/// (no defaulting needed) — keeps the constraint chain
/// `Self: RawRepresentable, Self.RawValue: ConstrainedDefaulted` entirely inside this
/// module and lets the recursive where-clause anchor on a real conformer.
@frozen
public struct DefaultRawValue: ConstrainedDefaulted, Hashable, Sendable {
    // Int32 (not Int) so the C# projection is `int`, not `nint`. The generator's
    // internal `init(IntPtr handle)` ctor and a public `init(value: nint)` ctor share
    // the same C# signature (nint == IntPtr), so an Int-typed field would trip CS0111
    // independently of the conformance work this fixture targets.
    public let value: Int32
    public init(value: Int32) { self.value = value }
    public var summary: String { "raw:\(value)" }
    public func describe() -> String { "DefaultRawValue value=\(value)" }
}

/// THE constrained extension under test. Provides BOTH protocol requirements via a
/// where-clause that ties `Self.RawValue` to the SAME protocol.
/// Pre-fix, `ProtocolExtensionDefaultsIndex` skipped this entire extension because
/// `WhereConstraints.Count > 0`, leaving the validator blind to the defaults.
extension ConstrainedDefaulted where Self: RawRepresentable, Self.RawValue: ConstrainedDefaulted {
    public var summary: String { rawValue.summary }
    public func describe() -> String { "via:\(rawValue.describe())" }
}

/// Parent type with a property/nested-type name collision. The C# projection of
/// `let kind: Kind` is the property `Kind`, which clashes with the nested type `Kind`.
/// The nested-type-collision pre-pass renames the nested TYPE to `KindInfo` while the
/// property keeps its name — the property/nested-type name-collision trigger shape.
@frozen
public struct ConstraintHost {
    public let kind: Kind
    public init(kind: Kind) { self.kind = kind }

    /// Nested conformer. It declares ZERO direct members for `ConstrainedDefaulted` —
    /// every requirement is reached through the constrained extension above. RawValue is
    /// `DefaultRawValue` which conforms to `ConstrainedDefaulted`, so the recursive
    /// where-clause holds. This nested struct declares zero direct members for the protocol.
    @frozen
    public struct Kind: RawRepresentable, ConstrainedDefaulted, Hashable {
        public let rawValue: DefaultRawValue
        public init(rawValue: DefaultRawValue) { self.rawValue = rawValue }
    }

    /// Returning a `DefaultedCursor<Kind>` forces the generator to emit a C# method
    /// returning `DefaultedCursor<KindInfo>` (post-rename). The cursor's
    /// `T: ConstrainedDefaulted` constraint becomes `where T : IConstrainedDefaulted`
    /// in C#, so this method is what trips CS0311 when `KindInfo` drops the interface.
    public func makeCursor() -> DefaultedCursor<Kind> {
        DefaultedCursor(value: Kind(rawValue: DefaultRawValue(value: 1)))
    }
}

/// Generic carrier with a `ConstrainedDefaulted` constraint. The C# emission
/// preserves the constraint, so any closed instantiation over `ConstraintHost.KindInfo`
/// must see the post-rename type declaring `IConstrainedDefaulted`.
public struct DefaultedCursor<T: ConstrainedDefaulted> {
    public let value: T
    public init(value: T) { self.value = value }
    public func describe() -> String { value.describe() }
}
