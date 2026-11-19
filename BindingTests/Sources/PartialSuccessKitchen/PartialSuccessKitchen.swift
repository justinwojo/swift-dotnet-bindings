// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// PartialSuccessKitchen — a deliberately tiny pure-Swift module that mixes a handful of
// intentionally-unsupported shapes with two must-emit "positive control" types. It is the
// fixture behind the `--partial-success-kitchen` gate, which proves the product promise that
// a third-party library carrying hard shapes still yields a clean partial binding: the
// generator exits 0, the emitted C# compiles, and the skip report honestly accounts for every
// dropped shape with a defensible disposition (no Review-tier surprises).
//
// Each S## shape below is the minimal Swift that triggers one skip class. The IDs are stable
// so the gate's baseline can map an observed skip back to the shape that produced it. Keep this
// module small on purpose: its skip budget is frozen in
// build/baselines/partial-success-kitchen-baseline.json, so an unrelated new shape here would
// look like report drift and fail the gate until the baseline is updated in the same commit.

import Combine
import SwiftUI

// ── Positive controls (must emit) ────────────────────────────────────────────────────────

// P01 — frozen blittable struct: init + stored property usable from C#.
public struct KitchenOk {
    public var x: Int
    public init(x: Int) { self.x = x }
}

// P02 — a second must-emit surface so "partial" is never a single-type fluke.
public final class KitchenOkClass {
    public init() {}
    public func ping() -> Int32 { 7 }
}

// ── Intentional skip shapes ──────────────────────────────────────────────────────────────

// S01 — SwiftUI View: routed to the SwiftUI bridge, skipped from the main binding.
public struct KitchenView: View {
    public init() {}
    public var body: some View { EmptyView() }
}

// S02 — protocol with an associated type consumed through an existential parameter.
public protocol KitchenPAT {
    associatedtype Item
    func item() -> Item
}
public func useKitchenPAT(_ p: any KitchenPAT) {}

// S03 — multi-requirement PAT that cannot reverse-dispatch cleanly, returned from a class.
public protocol KitchenMultiPAT {
    associatedtype A
    associatedtype B
    func pair() -> (A, B)
}
public final class KitchenMultiPATCarrier {
    public init() {}
    public func make() -> any KitchenMultiPAT { fatalError("fixture-only") }
}

// S04–S05 — closure-bearing members (a small supporting protocol for the second bucket).
public protocol KitchenSignal { func tag() -> Int32 }
public final class KitchenAsyncVoidClosure {
    public init() {}
    public func open(_ h: @escaping (Int32) async -> Void) {}
}
public final class KitchenClosureOptExistential {
    public init() {}
    public func check(_ f: @escaping (Int32) -> (any KitchenSignal)?) -> Bool { f(0) != nil }
}

// S06 — method-level generic (emit-or-honest-skip: the gate tolerates either, never both).
public final class KitchenMethodGeneric {
    public init() {}
    public func map<T: KitchenSignal>(_ v: T) -> Int32 { v.tag() }
}

// S07–S08 — parameter packs (type-level pack gate + free variadic-generic function).
// Variadic generic packs need the iOS 17 / macOS 14 runtime metadata machinery, so both are
// availability-gated to compile against the fixture's iOS 15 deployment floor (matching the
// main test lib's VariadicGenericPack fixture). The gate fires on the pack shape regardless.
@available(iOS 17.0, macOS 14.0, tvOS 17.0, *)
public struct KitchenPack<each R> { public init() {} }
@available(iOS 17.0, macOS 14.0, tvOS 17.0, *)
public func kitchenPackCall<each T>(_ xs: repeat each T) {}

// S09 — public members on a @usableFromInline internal parent with no clean fallback.
@usableFromInline
internal final class KitchenInternalParent {
    public init() {}
    public func describeAsync() async -> Int32 { 1 }
    public func transform(using f: @escaping (Int32) -> Int32) -> Int32 { f(1) }
}

// S10 — public host type with a @usableFromInline internal member.
public final class KitchenPublicHost {
    public init() {}
    @usableFromInline
    internal func register(_ x: Int32) {}
}

// S12 — Codable synthesizes encode(to:)/init(from:) members: expected structural skip.
public struct KitchenCodable: Codable {
    public var n: Int
    public init(n: Int) { self.n = n }
}

// S13 — a Codable struct's CodingKeys: an `enum: String` that is NOT public, nested inside a
// PUBLIC parent. Swift's own synthesized CodingKeys has exactly this shape but is `private`, so it
// never reaches the ABI surface a binding is generated from; spelling it out as
// @usableFromInline internal reproduces the shape at the visibility real libraries ship it with
// (an explicit CodingKeys is the common way to rename JSON fields) and puts it in front of the
// raw-representable planner. The wrapper module cannot name a non-public type, so its Swift plane
// is discarded — nothing on this enum may plan a wrapper P/Invoke.
//
// The four CodingKey requirements are written out by hand because the compiler-synthesized ones
// are plain `internal` and so are absent from the emitted .swiftinterface, which then fails its own
// verification (the printed `init?(rawValue:)` is left as the only candidate for the protocol's
// `init?(stringValue:)`). Restating them @usableFromInline keeps the interface self-consistent and
// changes nothing about the shape under test.
public struct KitchenCodableKeys: Codable {
    public var title: String
    public var count: Int
    public init(title: String, count: Int) {
        self.title = title
        self.count = count
    }

    @usableFromInline
    internal enum CodingKeys: String, CodingKey {
        case title
        case count = "n"
        @usableFromInline internal init?(stringValue: String) { self.init(rawValue: stringValue) }
        @usableFromInline internal var stringValue: String { rawValue }
        @usableFromInline internal init?(intValue: Int) { nil }
        @usableFromInline internal var intValue: Int? { nil }
    }
}

// S14 — a plain module-internal `enum X: String` with no public exposure. It cannot be surfaced
// from a public signature at all (Swift rejects "function cannot be declared public because its
// result uses an internal type"), and it does not need to be: an ABI-visible internal declaration
// reaches the planner on its own. Same discard contract as S13, without a public parent.
@usableFromInline
internal enum KitchenInternalRawValue: String {
    case alpha
    case beta = "B"
}

// S15 — declarations this module adds to another module's Swift enum (Foundation's caseless
// AttributeScopes): a nested type and a property. Only class and frozen-struct receivers are bound
// across modules, so the parser drops the receiver; both declarations must still be reported as
// skipped, not lost with it.
@available(iOS 15, macOS 12, tvOS 15, *)
extension AttributeScopes {
    public struct KitchenAttributes {
        public init() {}
    }
    public var kitchen: KitchenAttributes.Type { KitchenAttributes.self }
}

// S16 — single-case enums nested in a generic type, directly, in an extension of it, and one level
// deeper. A single-case enum has zero runtime size and none of these can take the C# enum
// projection, so each is refused as a type and its row must reach the report (the refusal once
// ran after the type was recorded as emitted, which swallowed the row).
public struct KitchenGenericHost<Value> {
    public var value: Value
    public init(value: Value) { self.value = value }
    public enum Unit { case only }
    public struct Interpolation {
        public init() {}
        public enum Token { case id }
    }
}

extension KitchenGenericHost {
    public enum DecodingErrors: Error { case unsupportedValueType }
}

// S17 — generic operator functions declared at module level. The generator binds operators a
// type declares; these have no C# home (and `&&`/`||` are not overloadable in C#), so each must
// be reported rather than dropped by the parser.
public struct KitchenPredicate<Value> {
    public init() {}
}
public func && <Value>(left: KitchenPredicate<Value>, right: KitchenPredicate<Value>) -> KitchenPredicate<Value> { left }
public func || <Value>(left: KitchenPredicate<Value>, right: KitchenPredicate<Value>) -> KitchenPredicate<Value> { left }
prefix public func ! <Value>(operand: KitchenPredicate<Value>) -> KitchenPredicate<Value> { operand }

// S18 — a property-wrapper projection (`$link`) of a public wrapped property. The interface prints
// only `link`; the projection is not bound and must be reported as a projection, not as internal.
@propertyWrapper
public struct KitchenWrapper<T> {
    public var wrappedValue: T
    public init(wrappedValue: T) { self.wrappedValue = wrappedValue }
    public var projectedValue: KitchenWrapper<T> { self }
}

public final class KitchenWrapperHost {
    @KitchenWrapper public var link: String = "kitchen"
    public init() {}
}

// S19 — a method this module adds to another module's protocol (Combine's Cancellable). No path
// binds members of a foreign protocol's extension, so the member must be reported.
public final class KitchenAnchor { public init() {} }
extension Cancellable {
    public func kitchenStoreWhileAlive(_ anchor: KitchenAnchor) {}
}
