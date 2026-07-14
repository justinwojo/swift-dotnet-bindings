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
