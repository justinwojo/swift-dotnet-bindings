// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Suppressed-proxy reference channels (plain, non-constrained protocol)
//
// `Boxable` is a PLAIN protocol — no associated type, no generic parameter — used
// as a bare `any Boxable`. Its `init()` requirement makes EveryProtocol unable to
// conform (a universal proxy cannot synthesize an initializer), so EveryProtocolEmitter
// records the conformance as skipped (`ConstructorRequirements`) and `BoxableProxy`
// is suppressed. Unlike the constrained-existential `LabelledContainer<Label>` fixture
// — whose `any LabelledContainer<String>` projects with a NULL proxy name for an
// unrelated (parameterized-protocol) reason and therefore never emits a proxy
// reference at all — `any Boxable` flows through the STANDARD existential marshalling
// path, where the generator would emit a reference to the suppressed `BoxableProxy`
// unless the emit-time suppressed-proxy gate intervenes:
//
//   * CONSUME (parameter / array element / closure-return / enum-case / property-set):
//     the `GetOrCreate<IBoxable>(value, static __v => new BoxableProxy(__v))` wrap
//     fallback must DROP its lambda → `GetOrCreate<IBoxable>(value)`. The member stays
//     and a Swift-vended conformer still round-trips.
//   * PRODUCE (return / property-get / enum-payload-read): a standalone
//     `new BoxableProxy(...)` cannot be constructed, so the whole member is re-emitted
//     as a throw stub.
//
// This is the durable in-repo gate for the emit-time decision that replaced the
// CSharpWrapperCoGater regex post-pass (trigger #3: suppressed proxy references).

/// Plain protocol with an `init()` requirement → EveryProtocol conformance skipped
/// (`ConstructorRequirements`) → `BoxableProxy` suppressed. The interface `IBoxable`
/// is still emitted; only the universal proxy is gone.
public protocol Boxable {
    init()
    func boxedValue() -> Int32
}

/// Concrete conformer. Its own witness table for `Boxable` is emitted, so a C# value
/// of this type can be boxed to `any Boxable` (forward dispatch) even though the
/// universal `BoxableProxy` is suppressed.
public struct BoxableIntCell: Boxable {
    private let v: Int32
    public init() { self.v = 0 }
    public init(value: Int32) { self.v = value }
    public func boxedValue() -> Int32 { v }
}

// MARK: CONSUME — method parameter (standard existential wrap fallback)

/// Accepts a bare `any Boxable`. The generated wrapper boxes the parameter through
/// `GetOrCreate<IBoxable>(value, …)`; the suppressed-proxy gate must drop the
/// `static __v => new BoxableProxy(__v)` fallback so the member compiles.
public func acceptBoxable(_ value: any Boxable) -> Int32 {
    return value.boxedValue()
}

// MARK: PRODUCE — method return (standalone proxy construction)

/// Returns a bare `any Boxable`. The generated wrapper would wrap the Swift result in
/// `new BoxableProxy(…)`; with the proxy suppressed the member becomes a throw stub.
public func makeBoxable(_ seed: Int32) -> any Boxable {
    return BoxableIntCell(value: seed)
}

// MARK: CONSUME — array element conversion (collection parameter)

/// Accepts `[any Boxable]`. Each element boxes through the per-element wrap fallback,
/// which must drop its suppressed-proxy lambda.
public func sumBoxables(_ values: [any Boxable]) -> Int32 {
    return values.reduce(0) { $0 + $1.boxedValue() }
}

// MARK: PRODUCE — array element return

/// Returns `[any Boxable]`. Each element would be wrapped in `new BoxableProxy(…)`;
/// with the proxy suppressed the member becomes a throw stub.
public func makeBoxables(_ seeds: [Int32]) -> [any Boxable] {
    return seeds.map { BoxableIntCell(value: $0) }
}

// MARK: CONSUME — closure return marshalling

/// Invokes a C#-supplied factory closure returning `any Boxable`. The C#-side callback
/// thunk wraps the managed result into an owned existential container via the
/// `CreateOwnedExistential1<IBoxable>(result, static __v => new BoxableProxy(__v))`
/// wrap fallback, which must drop its suppressed-proxy lambda.
public func applyBoxableFactory(_ make: () -> any Boxable) -> Int32 {
    return make().boxedValue()
}

// MARK: CONSUME (set) + PRODUCE (get) — property channel

/// Property of existential type. The getter PRODUCEs `any Boxable` (→ throw stub once
/// the proxy is suppressed); the setter CONSUMEs `any Boxable` (→ drop wrap fallback).
/// `currentValue()` is a non-existential read-back so the setter round-trip is
/// observable without touching the throw-stub getter.
public final class BoxableHolder {
    private var stored: any Boxable
    public init() { stored = BoxableIntCell(value: 0) }
    public var boxable: any Boxable {
        get { stored }
        set { stored = newValue }
    }
    public func currentValue() -> Int32 { stored.boxedValue() }
}

// MARK: CONSUME (case construction) + PRODUCE (payload read) — enum channel

/// Enum with an existential-payload case. Constructing `.boxed(any Boxable)` from C#
/// CONSUMEs the value (→ drop wrap fallback); `payloadValue()` reads it Swift-side so
/// the construction round-trip is observable.
public enum BoxableCarrier {
    case empty
    case boxed(any Boxable)
    public func payloadValue() -> Int32 {
        switch self {
        case .empty: return -1
        case .boxed(let b): return b.boxedValue()
        }
    }
}

// MARK: ============================================================
// MARK: Gap-shape channels (change-8 completion, 2026-06-19)
//
// The channels above exercise only bare `any Boxable` through the wrapper
// method body + enum + property-setter paths. The channels below exercise the
// PRODUCE/CONSUME sites that live OUTSIDE the wrapper-method-body checkpoint
// (async completion callback, closure invoke-thunk + callback helper classes,
// bound-generic enum payload projection, container-wrapped property getter) —
// each a path the first change-8 pass left ungated and the CoGater still masks.
// One channel per emit site so a CoGater-disabled regen surfaces exactly which
// site emits a dangling `new BoxableProxy(` reference.
// MARK: ============================================================

// MARK: PRODUCE — async existential return (AsyncHarnessEmitter completion callback)

/// Async method returning a bare `any Boxable`. The async completion callback (an
/// `[UnmanagedCallersOnly]` thunk) would wrap the Swift result in `new BoxableProxy(…)`;
/// with the proxy suppressed that callback must become a no-op stub (CoGater no-ops
/// `[UnmanagedCallersOnly]` bodies rather than throwing across the native boundary).
public func makeBoxableAsync(_ seed: Int32) async -> any Boxable {
    return BoxableIntCell(value: seed)
}

// MARK: PRODUCE — async COLLECTION existential return ([any Boxable])

/// Async method returning `[any Boxable]`. The async completion callback marshals the
/// collection return via AsyncHarnessEmitter's collection-return projection, which would
/// construct `new BoxableProxy(…)` per element — distinct from the scalar async return above
/// (gated via the completion callback). Exercises the async-collection projection path.
public func makeManyBoxablesAsync(_ seed: Int32) async -> [any Boxable] {
    return [BoxableIntCell(value: seed), BoxableIntCell(value: seed + 1)]
}

// MARK: PRODUCE — bound-generic enum payload ([any Boxable] / Optional<any Boxable>)

/// Enum whose payloads wrap the existential in a bound generic (`[any Boxable]`,
/// `Optional<any Boxable>`). The payload-read projection would construct
/// `new BoxableProxy(…)` per element; the bound-generic projection must thread
/// EmissionContext so the suppressed-proxy throw fires and the reader is restubbed.
public enum BoxableBoundCarrier {
    case none
    case many([any Boxable])
    case maybe((any Boxable)?)
    public func count() -> Int32 {
        switch self {
        case .none: return 0
        case .many(let xs): return Int32(xs.count)
        case .maybe(let x): return x == nil ? 0 : 1
        }
    }
}

// MARK: CONSUME (set) + PRODUCE (get) — container-wrapped existential property

/// Optional-existential property. The getter PRODUCEs `(any Boxable)?` through the
/// container-wrapped projection (→ throw stub); the setter CONSUMEs it (→ drop wrap
/// fallback). `hasValue()` is a non-existential read-back for the setter round-trip.
public final class OptionalBoxableHolder {
    private var stored: (any Boxable)?
    public init() { stored = nil }
    public var maybeBoxable: (any Boxable)? {
        get { stored }
        set { stored = newValue }
    }
    public func hasValue() -> Bool { stored != nil }
}

// MARK: PRODUCE — closure ARG any Boxable (Swift→C# callback deserialization)

/// Invokes a C#-supplied callback, handing it a Swift-vended `any Boxable`. The C#
/// callback thunk receives the existential and would deserialize it via
/// `new BoxableProxy(…)`; with the proxy suppressed that callback body must stub.
public func runBoxableConsumer(_ cb: (any Boxable) -> Void) {
    cb(BoxableIntCell(value: 7))
}

// MARK: PRODUCE — THROWING closure ARG any Boxable (Swift→C# callback deserialization)

/// Throwing twin of `runBoxableConsumer`: invokes a C#-supplied THROWING callback, handing it a
/// Swift-vended `any Boxable`. The throwing closure ARG routes through the throwing-closure callback
/// emitter (ClosureEmitter.Throwing.cs), whose arg-deserialization would construct
/// `new BoxableProxy(…)`. With the proxy suppressed that helper-emitted `[UnmanagedCallersOnly]`
/// trampoline reports through the throwing channel (*errorOut) instead of the non-throwing twin's
/// no-op — and, like every UCO body, must do so inside the try/catch envelope
/// (CatchFreeUcoValidatorTests). This is the only fixture that exercises the throwing-closure
/// suppressed-arg arm.
public func runThrowingBoxableConsumer(_ cb: (any Boxable) throws -> Void) throws {
    try cb(BoxableIntCell(value: 15))
}

// MARK: PRODUCE — closure RETURN any Boxable (Swift-closure-as-delegate invoke thunk)

/// Returns a Swift closure that produces `any Boxable`. C# wraps the closure as a
/// delegate; its invoke-thunk helper would construct `new BoxableProxy(…)` on each
/// invocation. With the proxy suppressed the invoke thunk must stub.
public func boxableProducer() -> () -> any Boxable {
    return { BoxableIntCell(value: 9) }
}

// NOTE: the CONTAINER closure-return twin (`() -> [any Boxable]`) is intentionally NOT a
// fixture. A closure whose RETURN is a container is rejected upstream by the closure-support
// gate ("Return type is a closure whose params/return cannot be invoked from C# without a
// function-pointer marshaler") BEFORE it ever reaches the proxy-suppression decision — so it
// is a pre-existing closure-support limitation, not an ungated trigger-#3 site. Verified
// empirically: a CoGater-disabled regen of such a method emits an "Unsupported: method …
// closure signature not yet supported" stub, never a dangling `new BoxableProxy(`.

// MARK: PRODUCE — closure RETURN any Boxable with a struct parameter

/// Like `boxableProducer` but the returned closure takes a frozen-struct parameter,
/// routing through the struct-param closure invoke-thunk emitter.
public struct BoxableTag {
    public let n: Int32
    public init(n: Int32) { self.n = n }
}
public func boxableProducerWithParam() -> (BoxableTag) -> any Boxable {
    return { tag in BoxableIntCell(value: tag.n) }
}

// MARK: PRODUCE — throwing closure RETURN any Boxable

/// Returns a throwing Swift closure producing `any Boxable`, routing through the
/// throwing-closure invoke-thunk emitter's success-payload path.
public func throwingBoxableProducer() -> () throws -> any Boxable {
    return { BoxableIntCell(value: 11) }
}

// MARK: ============================================================
// MARK: Change-8 completion: the three remaining ungated emit sites (2026-06-19)
//
// The audit of every proxy-construction site found three that the first change-8
// passes (8a–8c) left ungated and the CoGater still masks. Each is the SAME
// trigger #3 (suppressed proxy references) at a different emit site, not a fourth
// CoGater responsibility. One fixture per site so a CoGater-disabled regen
// surfaces exactly which site would otherwise emit a dangling `new BoxableProxy(`.
//
//   * A — indirect-return closure callback ARG deserialization
//     (ClosureEmitter.IndirectReturn.cs, GetInvokeArgExpression's third caller)
//   * B — reverse-dispatch existential-return proxy method/property body
//     (ProtocolProxyEmitter.InterfaceImpl.cs)
//   * C — existential-bypass adapter existential-return wrap
//     (ExistentialBypassEmitter.cs)
// MARK: ============================================================

// MARK: PRODUCE — closure ARG any Boxable on an INDIRECT-return callback (Category A)

/// Invokes a C#-supplied closure that takes a Swift-vended `any Boxable` AND returns a
/// bound generic (`Int32?` → `Optional<Int32>`). The bound-generic return forces the
/// indirect-return callback emitter (RequiresIndirectReturnMarshalling), whose
/// arg-deserialization loop would construct `new BoxableProxy(…)` for the `any Boxable`
/// parameter. With the proxy suppressed that helper-emitted `[UnmanagedCallersOnly]`
/// callback must stub through its own failure channel (FailFast) — it cannot
/// checkpoint-throw (Hazard D), and a silent empty body would leave the Swift-allocated
/// indirect-result buffer uninitialized for the adapter to `.move()`.
public func runIndirectBoxableConsumer(_ cb: (any Boxable) -> Int32?) -> Int32 {
    return cb(BoxableIntCell(value: 13)) ?? -1
}

// MARK: PRODUCE — reverse-dispatch existential return (proxy method + property) (Category B)

/// A protocol whose method AND property RETURN a bare `any Boxable`. `BoxableVending`
/// itself has NO `init()` requirement, so its own `BoxableVendingProxy` is emitted
/// normally; but that proxy's `VendBoxable()` / `CurrentBoxable` reverse-dispatch into
/// the Swift value, read back the existential, and would construct `new BoxableProxy(…)`.
/// With the proxy suppressed, the proxy-class method body + property getter must throw
/// NotSupportedException while KEEPING the interface member (the caller owns the braces,
/// so the gate is a local throw-statement, not a body checkpoint).
///
/// The `…ManyBoxables` members extend Category B to the COLLECTION-return reverse-dispatch
/// paths: `[any Boxable]` routes the proxy method through `EmitCollectionReturnMethodBody`
/// and the proxy property through the `isCollectionReturnGetter` block, where the per-element
/// PRODUCE projection (`GetCollectionMarshalExpression`) would construct `new BoxableProxy(…)`.
/// With the proxy suppressed, the projection throws `SuppressedProxyReferenceException` during
/// string building (no body written yet), and the gate catches it to emit a throw stub —
/// distinct from the scalar gate's predictive local check.
public protocol BoxableVending {
    func vendBoxable() -> any Boxable
    var currentBoxable: any Boxable { get }
    func vendManyBoxables() -> [any Boxable]
    var allBoxables: [any Boxable] { get }
}

/// Concrete conformer + a vendor function that hands C# an `any BoxableVending`, forcing
/// the `BoxableVendingProxy` class (and thus its existential-return reverse-dispatch
/// bodies) to be emitted.
public struct BoxableVendingImpl: BoxableVending {
    private let seed: Int32
    public init(seed: Int32) { self.seed = seed }
    public func vendBoxable() -> any Boxable { BoxableIntCell(value: seed) }
    public var currentBoxable: any Boxable { BoxableIntCell(value: seed) }
    public func vendManyBoxables() -> [any Boxable] { [BoxableIntCell(value: seed), BoxableIntCell(value: seed + 1)] }
    public var allBoxables: [any Boxable] { [BoxableIntCell(value: seed)] }
}

public func makeBoxableVending(_ seed: Int32) -> any BoxableVending {
    return BoxableVendingImpl(seed: seed)
}

// MARK: PRODUCE — concrete-type subscript returning [any Boxable] (Category B-collection)

/// A concrete type whose SUBSCRIPT returns `[any Boxable]`. The generated C# indexer getter
/// projects each element via `AsProjected(e => (IBoxable)new BoxableProxy(e))` through
/// `SubscriptHandler.EmitIndexerGetter`. With the proxy suppressed, the per-element PRODUCE
/// projection throws `SuppressedProxyReferenceException` during string building; the probe at
/// the top of `EmitIndexerGetter` catches it (before any getter body is written) and restubs
/// the indexer getter while keeping the public member — the subscript twin of the concrete
/// `BoxableVendingImpl.allBoxables` property getter.
public final class BoxableShelf {
    private let cells: [BoxableIntCell]
    public init(count: Int32) { self.cells = (0..<max(0, count)).map { BoxableIntCell(value: $0) } }
    public subscript(group: Int32) -> [any Boxable] {
        return cells
    }
}

/// A concrete type whose SUBSCRIPT returns a SCALAR `any Boxable` (not a collection). The scalar
/// existential getter conversion is `(null, false)` — the C# indexer getter delegates to the
/// wrapper cdecl accessor (`get => …()`), whose existential PRODUCE is gated by WrapperEmitter's
/// EmitMethod catch (the same mechanism as a scalar existential property getter). Exercises that
/// the scalar-existential subscript wrapper accessor restubs rather than referencing the absent
/// proxy — the scalar twin of `BoxableShelf`'s collection subscript.
public final class BoxableRack {
    private let cell: BoxableIntCell
    public init(seed: Int32) { self.cell = BoxableIntCell(value: seed) }
    public subscript(index: Int32) -> any Boxable {
        return cell
    }
}

// MARK: PRODUCE — existential-bypass adapter existential return (Category C)

/// Instance method with an existential ARG that has a DEFAULT value AND an existential
/// RETURN. The existential-bypass adapter fires first (HasExistentialArg) and omits the
/// defaulted `tag`, so it owns the whole method emission; its existential-return wrap
/// would construct `new BoxableProxy(…)`. With the proxy suppressed the bypass must keep
/// the public member but emit a throw body (a `return false` would drop the member, not
/// fall back).
public final class BoxableBypassHost {
    public init() {}
    public func produceBoxable(tag: any Boxable = BoxableIntCell()) -> any Boxable {
        return tag
    }
}

// MARK: CONSUME — collection-valued existential property SETTER (PropertyHandler container setter)

/// A settable property whose value is `[any Boxable]`. The SCALAR existential setter is gated
/// (the scalar special-case path consults `IsProxyNameSuppressed` with EmissionContext), but the
/// COLLECTION setter projects through the general projection path whose `ProjectionContext` must
/// carry EmissionContext — otherwise the per-element conversion constructs `new BoxableProxy(…)`.
/// With the proxy suppressed the setter must drop the element-wrap lambda (CONSUME), not reference
/// the absent proxy. The collection GETTER is the already-gated PRODUCE-throw path.
public final class BoxableCollectionHolder {
    private var stored: [any Boxable] = []
    public init() {}
    public var boxables: [any Boxable] {
        get { stored }
        set { stored = newValue }
    }
    public func count() -> Int32 { Int32(stored.count) }
}

// MARK: CONSUME — settable COLLECTION existential SUBSCRIPT (SubscriptHandler setter)

/// A settable subscript whose value type is `[any Boxable]`. Like the property container setter
/// (`BoxableCollectionHolder`), the SCALAR existential subscript setter is safe — it boxes through
/// `ExistentialContainerFactory.GetOrCreate`, which constructs no proxy — but the COLLECTION setter
/// projects through the general projection path in `SubscriptHandler.EmitIndexerSetter`, whose
/// `ProjectionContext` must carry EmissionContext; otherwise the per-element CONSUME conversion
/// constructs `new BoxableProxy(…)`. With the proxy suppressed the setter drops the element-wrap
/// lambda. The collection GETTER is the already-gated PRODUCE-throw probe path (`EmitIndexerGetter`,
/// twin of `BoxableShelf`).
public final class BoxableSettableRack {
    private var stored: [Int32: [any Boxable]] = [:]
    public init() {}
    public subscript(index: Int32) -> [any Boxable] {
        get { stored[index] ?? [] }
        set { stored[index] = newValue }
    }
    /// Non-existential read-back so the CONSUME subscript-setter round-trip is observable without
    /// touching the throw-stub collection getter (mirrors `BoxableCollectionHolder.count()`).
    public func count(_ index: Int32) -> Int32 { Int32(stored[index]?.count ?? 0) }
}

// MARK: PRODUCE — async OPTIONAL-of-COLLECTION existential return (AsyncHarnessEmitter ProjectReturn)

/// Async method returning `[any Boxable]?` — the optional-of-collection async return. The optional
/// projection is built by `AsyncHarnessEmitter.ProjectReturn`, whose `ProjectionContext` must carry
/// EmissionContext so the inner container's per-element PRODUCE projection throws
/// `SuppressedProxyReferenceException` (caught in `TryGetOptionalMarshalType`, surfaced as
/// `proxySuppressed`) rather than constructing `new BoxableProxy(…)` per element. With the proxy
/// suppressed the completion callback faults the awaiting Task.
///
/// NOTE: the SCALAR async-optional twin (`async -> (any Boxable)?`) is intentionally NOT a fixture
/// here. Its marshalling is a SEPARATE pre-existing gap, NOT trigger #3: the async harness's optional
/// branch blindly emits `(IFoo?)_swiftOpt.Some` with no existential proxy-wrapping at all (broken for
/// a LIVE proxy too, unlike the collection path which wraps correctly and only needs suppression
/// gating), and the CoGater never masked it (there is no `new {Proxy}(` text to rewrite). Adding
/// async-optional-scalar-existential return marshalling is a standalone follow-up, outside the
/// CoGater-retirement scope.
public func makeManyBoxablesAsyncOptional(_ seed: Int32) async -> [any Boxable]? {
    return seed >= 0 ? [BoxableIntCell(value: seed)] : nil
}

// MARK: CONSUME — container closure ARG ([any Boxable]) -> Void

/// A closure whose ARGUMENT is `[any Boxable]` (Swift hands C# a Swift-vended array of existentials).
/// The UCO trampoline's suppression guard (`IsProxyReferenceSuppressed`) only recurses into tuples,
/// not Array/Set/Dictionary/Optional, so a container existential arg slips past the guard and the
/// element deserialization would construct `new BoxableProxy(…)` per element. With the proxy
/// suppressed the trampoline must no-op (it cannot hand the delegate an unmaterializable existential).
public func runBoxableListConsumer(_ cb: ([any Boxable]) -> Void) {
    cb([BoxableIntCell(value: 1), BoxableIntCell(value: 2)])
}
