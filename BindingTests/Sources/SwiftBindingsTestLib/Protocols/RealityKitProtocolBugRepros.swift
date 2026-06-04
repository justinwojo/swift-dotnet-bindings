// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bug 12: `[any Protocol]` array property on a witness-dispatched protocol requirement

/// Reproduces RealityKit `Scene.anchors` which returns a heap-allocated
/// `[any AnchorEntity]`. The generator must keep the existential element
/// in the rendered Swift type — earlier it dropped the generic parameter
/// (rendered `[Swift.Array]` / `[any Anchor]` mismatched the C# Element).
///
/// The bug lives in `WitnessDispatchEmitter.EmitPropertyGetterAccessor`, so
/// the fixture must declare an existential array as a protocol requirement
/// (and a conformer) — that's what drives witness-dispatch property emission.
/// A plain class property would only exercise the standard property wrapper
/// path and leave the changed code uncovered.
public protocol BugReproExistentialItem {
    func describe() -> String
}

public class BugReproExistentialItemImpl: BugReproExistentialItem {
    public let label: String
    public init(label: String) { self.label = label }
    public func describe() -> String { label }
}

/// Witness-dispatched property requirement returning `[any BugReproExistentialItem]`.
public protocol BugReproExistentialArrayProvider {
    var items: [any BugReproExistentialItem] { get }
}

public class BugReproExistentialArrayHolder: BugReproExistentialArrayProvider {
    public var items: [any BugReproExistentialItem]

    public init() {
        items = [
            BugReproExistentialItemImpl(label: "alpha"),
            BugReproExistentialItemImpl(label: "beta"),
        ]
    }
}

// MARK: - Bug 13: class-bound (superclass-constrained) `[any Protocol]` array round-trip
//
// Reproduces the RealityKit `ARView.installGestures(...) -> [any EntityGestureRecognizer]`
// crash. `EntityGestureRecognizer` is *class-bound* because it is constrained to a
// superclass (`UIGestureRecognizer`). A class-bound existential has a compact 2-word
// `[classRef][witnessTable]` element layout (16-byte stride), NOT the 5-word opaque
// `ExistentialContainerN` layout (40-byte stride). Marshalling the array as
// `SwiftArray<ExistentialContainer1>` makes `SwiftArray.Get` ($sSayxSicig) over-read each
// element (base + i*40 against a 16-byte array) → SIGSEGV the moment the array is indexed.
// `Count` succeeds because it only reads the array header.
//
// The conformer also exposes a witness-dispatched **optional class** property
// (`hostEntity: GestureHostBase?`) mirroring `EntityGestureRecognizer.entity: Entity?`,
// so the fixture covers both the array element layout AND dispatching a member on a
// Swift-backed class-bound proxy materialised out of that array.

/// Plain Swift class used both as the protocols' superclass constraint and as the
/// optional-class property return type (the `Entity?` analog).
public class GestureHostBase {
    public let hostTag: Int
    public init(hostTag: Int) { self.hostTag = hostTag }
    public func tagString() -> String { "host#\(hostTag)" }
}

/// Superclass-constrained protocol whose existential is returned through another
/// class-bound protocol's getter — mirrors `EntityGestureRecognizer.entity`
/// returning `(any HasCollision)?` (HasCollision is `: Entity`). Reading it must
/// materialise a Swift-backed read-only proxy without an EveryProtocol conformer.
public protocol BoundCollidable: GestureHostBase {
    var collisionLabel: String { get }
}

public final class BoundCollidableImpl: GestureHostBase, BoundCollidable {
    public let collisionLabel: String
    public init(hostTag: Int, collisionLabel: String) {
        self.collisionLabel = collisionLabel
        super.init(hostTag: hostTag)
    }
}

/// Superclass-constrained protocol → class-bound existential (`any BoundRecognizer`
/// is `[classRef][witnessTable]`, 16-byte stride).
public protocol BoundRecognizer: GestureHostBase {
    var recognizerLabel: String { get }
    /// Optional concrete-class return (the simple `Entity?` analog).
    var hostEntity: GestureHostBase? { get }
    /// Optional class-bound existential return (the `entity: (any HasCollision)?` analog) —
    /// materialises a read-only proxy of another superclass-constrained protocol.
    var collidable: (any BoundCollidable)? { get }
    /// Method-return class-bound existential (the `func currentCollidable() -> any HasCollision`
    /// analog) — exercises `EmitExistentialReturnMethodBody`, the scalar method-return sibling of
    /// the `collidable` getter. The returned `any BoundCollidable` heap cell is a 2-word
    /// `[classRef][witnessTable]`; reading it as a 5-word `ExistentialContainer1` over-reads
    /// 24 bytes past the 16-byte allocation.
    func makeCollidable() -> any BoundCollidable
    /// Optional class-bound existential method return — covers the `isOptionalReturn` branch of
    /// the method-return path (resultPtr == nil → null; non-null → read 2 words + proxy).
    func makeCollidableIf(present: Bool) -> (any BoundCollidable)?
}

public final class BoundRecognizerImpl: GestureHostBase, BoundRecognizer {
    public let recognizerLabel: String
    public let hostEntity: GestureHostBase?
    public let collidable: (any BoundCollidable)?
    public init(hostTag: Int, recognizerLabel: String,
                hostEntity: GestureHostBase?, collidable: (any BoundCollidable)?) {
        self.recognizerLabel = recognizerLabel
        self.hostEntity = hostEntity
        self.collidable = collidable
        super.init(hostTag: hostTag)
    }

    public func makeCollidable() -> any BoundCollidable {
        BoundCollidableImpl(hostTag: hostTag, collisionLabel: "current-\(recognizerLabel)")
    }

    public func makeCollidableIf(present: Bool) -> (any BoundCollidable)? {
        present ? BoundCollidableImpl(hostTag: hostTag, collisionLabel: "opt-\(recognizerLabel)") : nil
    }
}

/// Vendor returning a class-bound existential array from a method (the `installGestures` analog)
/// and from a property, so both the method-return and property-return array paths are covered.
public class BoundRecognizerVendor {
    public init() {}

    /// Method-return class-bound existential array (the `ARView.installGestures` shape).
    public func installRecognizers() -> [any BoundRecognizer] {
        let host = GestureHostBase(hostTag: 7)
        let collidable = BoundCollidableImpl(hostTag: 9, collisionLabel: "collide")
        return [
            BoundRecognizerImpl(hostTag: 1, recognizerLabel: "pan",
                                hostEntity: host, collidable: collidable),
            BoundRecognizerImpl(hostTag: 2, recognizerLabel: "tap",
                                hostEntity: nil, collidable: nil),
        ]
    }

    /// Property-return class-bound existential array.
    public var recognizers: [any BoundRecognizer] { installRecognizers() }

    /// Scalar class-bound existential return from a CONCRETE (non-protocol) class method.
    /// Unlike `BoundRecognizer.makeCollidable()` (a protocol requirement → witness/Receivers
    /// dispatch), this concrete-class method routes through the `@_cdecl` wrapper return path
    /// (`WrapperEmitter.Return` / `ExistentialBypassEmitter`), where the class-bound cell must be
    /// read at its 2-word `[classRef][witnessTable]` width — reading the 5-word opaque container
    /// pulls 24 bytes of uninitialized buffer into the unused container fields.
    public func currentCollidable() -> any BoundCollidable {
        BoundCollidableImpl(hostTag: 21, collisionLabel: "vendor-current")
    }

    /// Optional variant — covers the `isOptionalReturn` branch of the concrete-class return path.
    public func currentCollidableIf(present: Bool) -> (any BoundCollidable)? {
        present ? BoundCollidableImpl(hostTag: 22, collisionLabel: "vendor-opt") : nil
    }
}

/// Surviving-owner probe for the `Optional<any P_classbound>` RETURN ownership contract.
/// The vendor holds the SOLE strong reference to a single conformer and hands the same
/// instance back through `borrowCollidableIf`. The C# side reads the returned proxy, disposes
/// it (one `Arc.UnknownObjectRelease`), then reads the label again *through the vendor* — proving
/// the instance survived the proxy's release. If the generated `finally`'s
/// `DestroyWireBufferRetains` were a real release (rather than a no-op for the compact 2-word
/// layout, whose opaque-Optional value witness keys `.none` off the always-zero metadata word at
/// offset 24), the shared +1 would be over-released and the post-dispose read would crash.
public class RetainingCollidableVendor {
    private let retained: BoundCollidableImpl
    public init(label: String) {
        retained = BoundCollidableImpl(hostTag: 99, collisionLabel: label)
    }
    /// Returns the vendor-owned instance (the vendor keeps its strong ref) so C# adopts a
    /// shared +1 rather than the sole +1 to a fresh object.
    public func borrowCollidableIf(present: Bool) -> (any BoundCollidable)? {
        present ? retained : nil
    }
    /// Reads the retained instance's label directly through the vendor — used AFTER the C#
    /// proxy is disposed to prove the instance is still alive (no double-release).
    public func retainedLabel() -> String { retained.collisionLabel }
}

// MARK: - Class-bound existential carrying an Objective-C (NSObject-rooted) conformer
//
// `BoundCollidable` above is rooted on a pure-Swift class, so its class-bound existential
// payload is a native Swift object that `swift_retain`/`swift_release` handle. A protocol whose
// superclass constraint is an *NSObject subclass* yields a class-bound existential whose payload
// is an Objective-C object — `swift_retain` would corrupt its ObjC refcount. The proxy adoption /
// release path must route through `swift_unknownObjectRetain`/`swift_unknownObjectRelease`
// (`Arc.UnknownObjectRetain`/`UnknownObjectRelease`), which dispatch on the isa.

/// NSObject-derived superclass used as a class-bound protocol's constraint, so any existential
/// `any ObjCBoundCollidable` carries an Objective-C object rather than a pure-Swift class.
public class ObjCGestureHostBase: NSObject {
    public let hostTag: Int
    public init(hostTag: Int) { self.hostTag = hostTag; super.init() }
}

/// Superclass-constrained (class-bound) protocol whose conformer is NSObject-rooted.
public protocol ObjCBoundCollidable: ObjCGestureHostBase {
    var collisionLabel: String { get }
}

public final class ObjCBoundCollidableImpl: ObjCGestureHostBase, ObjCBoundCollidable {
    public let collisionLabel: String
    public init(hostTag: Int, collisionLabel: String) {
        self.collisionLabel = collisionLabel
        super.init(hostTag: hostTag)
    }
}

/// Vendor producing ObjC-rooted class-bound existentials through the concrete-class
/// `@_cdecl` wrapper return paths (scalar + optional), plus a surviving-owner probe that
/// proves the ObjC payload is released exactly once through `swift_unknownObjectRelease`.
public class ObjCBoundVendor {
    private let retained: ObjCBoundCollidableImpl
    public init() {
        retained = ObjCBoundCollidableImpl(hostTag: 99, collisionLabel: "objc-shared")
    }
    /// Scalar class-bound existential return carrying an NSObject-derived conformer (fresh object).
    public func currentCollidable() -> any ObjCBoundCollidable {
        ObjCBoundCollidableImpl(hostTag: 31, collisionLabel: "objc-current")
    }
    /// Optional class-bound existential return carrying an NSObject-derived conformer (fresh object).
    public func currentCollidableIf(present: Bool) -> (any ObjCBoundCollidable)? {
        present ? ObjCBoundCollidableImpl(hostTag: 32, collisionLabel: "objc-opt") : nil
    }
    /// Returns the vendor-owned NSObject conformer (shared +1) for the surviving-owner probe.
    public func borrowCollidableIf(present: Bool) -> (any ObjCBoundCollidable)? {
        present ? retained : nil
    }
    /// Reads the retained ObjC instance's label through the vendor — proves it survived the
    /// proxy's `swift_unknownObjectRelease` (an errant native `swift_release` on an ObjC object
    /// would corrupt the refcount and this read would crash).
    public func retainedLabel() -> String { retained.collisionLabel }
}

// MARK: - Bug 14: class-bound existential read through async return, closure param, enum payload
//
// The compact 2-word `[classRef][witnessTable]` heap-cell layout (16 bytes, vs the 5-word opaque
// 40-byte container) must be honoured wherever a *single class-bound* `any P` heap cell is READ —
// not only array elements / scalar method returns. These fixtures drive the three remaining read
// sites: the async-harness existential return, the `@convention(c)` closure-parameter
// reconstruction, and the enum-payload extraction. Each reads a class-bound `any BoundCollidable`;
// reading it as a 5-word `ExistentialContainer1` over-reads 24 bytes past the allocation.

/// Async method returning a class-bound existential — drives the async-harness existential
/// return marshalling (`AsyncHarnessEmitter` / `WrapperEmitter.Async`).
public final class CollidableAsyncVendor {
    public init() {}
    public func fetchCollidable(label: String) async -> any BoundCollidable {
        BoundCollidableImpl(hostTag: 11, collisionLabel: "async-\(label)")
    }
}

/// Invokes a caller-supplied closure with a class-bound existential — drives the
/// `@convention(c)` closure-parameter reconstruction (`ClosureEmitter.GetInvokeArgExpression`).
public final class CollidableClosureVendor {
    public init() {}
    public func withCollidable(label: String, _ body: (any BoundCollidable) -> String) -> String {
        body(BoundCollidableImpl(hostTag: 12, collisionLabel: "closure-\(label)"))
    }

    /// Closure PARAMETER combined with a class-bound existential RETURN. The closure param routes
    /// this method's `@_cdecl` wrapper through `ClosureEmitter.SwiftWrapper`; the `any BoundCollidable`
    /// return there must parenthesize the metatype as `(any …).self`. Emitting the bare `any ….self`
    /// parses as `any (….self)`, fails Swift compilation, and silently strips the wrapper symbol →
    /// `EntryPointNotFoundException` at the call site.
    public func collidableFrom(label: String, _ select: (Int) -> Int) -> any BoundCollidable {
        // Fold the closure result into the observable collisionLabel (the proxy surfaces
        // collisionLabel, not the superclass hostTag) so the test proves the closure fired.
        BoundCollidableImpl(hostTag: 30, collisionLabel: "from-\(label)-\(select(30))")
    }

    /// Throwing variant — covers the `methodDecl.Throws` branch of the closure-bearing wrapper
    /// (the non-throwing variant above covers the other branch).
    public func collidableFromThrowing(label: String, _ select: (Int) -> Int) throws -> any BoundCollidable {
        BoundCollidableImpl(hostTag: 40, collisionLabel: "throw-\(label)-\(select(40))")
    }

    /// Closure PARAMETER combined with a protocol-COMPOSITION existential RETURN
    /// (`any Nameable & Ageable`). A composition renders through `RenderSwiftTypeSpec` as
    /// `any Nameable & Ageable` (WITH the `any` keyword) — unlike a single-protocol return,
    /// which the generator renders bare (`Nameable.self`). The closure-bearing wrapper must
    /// therefore parenthesize the metatype as `(any Nameable & Ageable).self`; emitting the
    /// bare `any Nameable & Ageable.self` parses as `any (Nameable & Ageable.self)`, fails Swift
    /// compilation, and silently strips the wrapper symbol → `EntryPointNotFoundException` at the
    /// call site. `collidableFrom` above (single-protocol) renders bare and so does NOT exercise
    /// this parenthesization; this composition sibling is the only fixture that does.
    public func compositionFrom(name: String, _ ageOf: (Int) -> Int) -> any Nameable & Ageable {
        Person(name: name, age: Int32(ageOf(7)))
    }
}

/// Enum carrying a class-bound existential payload — drives `EnumHandler` payload extraction
/// (reads the 2-word class-bound cell off the enum-copy buffer).
public enum CollidableBox {
    case present(any BoundCollidable)
    case absent
}

public final class CollidableBoxVendor {
    public init() {}
    public func makeBox(present: Bool, label: String) -> CollidableBox {
        present ? .present(BoundCollidableImpl(hostTag: 13, collisionLabel: "box-\(label)")) : .absent
    }
}

// MARK: - Bug 15: class-bound `[any P]` array WRITE + PARAM directions (C# → Swift)
//
// Bug 13 fixed the READ stride (Swift → C#) by routing the array element carrier through the
// 16-byte `ClassExistentialContainer1`. The SYMMETRIC write/param directions (C# → Swift) were
// left on the 40-byte `ExistentialContainer1` carrier: the parameter path
// (`ArrayProjection` FromEnumerable) and the protocol-receiver getter write path
// (`ProtocolProxyEmitter.Receivers`) both built `SwiftArray<ExistentialContainer1>` (40-byte slots)
// where Swift strides at 16 bytes. Swift then reads element[i] at `base + i*16` against 40-byte
// data → wrong classRef/witness for every i>0 → SIGSEGV / over-release. The existing
// ClassBoundExistentialArrayTests only exercise the return direction, so this half shipped untested.
//
// Both consumers below index element[1..] (not just element[0]) so a wrong stride surfaces as a
// crash or wrong sum rather than a lucky element[0] hit. Conformers are Swift-vended (a
// superclass-constrained class-bound protocol can only be satisfied by Swift subclasses), matching
// the real `ARView.installGestures` round-trip shape.

/// Superclass-constrained (class-bound) marker protocol with a trivial requirement so the
/// generated proxy / round-trip stays small. `markerId()` returns a per-conformer Int the Swift
/// consumers sum.
public protocol Marker: GestureHostBase {
    func markerId() -> Int
}

public final class MarkerImpl: GestureHostBase, Marker {
    // Feeds the shared allocation counters (`recordTrackedAllocation`/`recordTrackedDeallocation`
    // in Lifetime/OwnershipTests.swift, the same counters `LifetimeTracker` reads) so the
    // class-bound `[any Marker]` ownership test can assert that a source marker passed through a
    // SwiftArray round-trip is NOT prematurely deinit'd — i.e., the consuming `Array.append` adds
    // its own +1 rather than stealing the source proxy/wrapper's +1.
    public init(mid: Int) {
        super.init(hostTag: mid)
        recordTrackedAllocation()
    }
    deinit { recordTrackedDeallocation() }
    public func markerId() -> Int { hostTag }
}

/// Vendor producing Swift-backed `Marker` conformers, so C# holds real proxies to pass back
/// through the parameter / write paths.
public class MarkerVendor {
    public init() {}
    public func make(_ id: Int) -> any Marker { MarkerImpl(mid: id) }
}

/// PARAM direction: C# passes an `IEnumerable<IMarker>`; this sums `markerId()` across the array,
/// indexing every element (not just [0]). A 40-byte fill makes Swift read garbage classRef/witness
/// at element[1..] → crash or wrong sum.
public func sumMarkerIds(_ xs: [any Marker]) -> Int {
    var total = 0
    for x in xs { total += x.markerId() }
    return total
}

/// PARAM direction, count-only — proves the array header marshals even when no element is indexed
/// (the `Count`-succeeds / index-crashes asymmetry of the stride bug).
public func countMarkers(_ xs: [any Marker]) -> Int { xs.count }

/// WRITE direction: a witness-dispatched protocol getter requirement returning a class-bound
/// `[any Marker]`. A C# class implements this; Swift calls the getter (dispatching into the C#
/// implementation through its generated proxy / receiver), and `consumeMarkerProvider` indexes the
/// returned array. Exercises `ProtocolProxyEmitter.Receivers` getter carrier + element conversion.
public protocol MarkerProvider {
    var markers: [any Marker] { get }
}

/// Swift consumer reading a `MarkerProvider`'s `markers` (whoever implements it, including a C#
/// implementation reached through its proxy) and summing the ids, indexing element[1..].
public func consumeMarkerProvider(_ p: MarkerProvider) -> Int {
    var total = 0
    for m in p.markers { total += m.markerId() }
    return total
}

/// Non-class-bound (opaque) `[any P]` PARAM control — `BugReproExistentialItem` has no class
/// constraint, so its existential keeps the 40-byte `ExistentialContainer1` carrier. Proves the
/// write/param carrier change is surgical to class-bound elements only (this must stay on the
/// opaque carrier and still round-trip correctly).
public func joinItemDescriptions(_ xs: [any BugReproExistentialItem]) -> String {
    xs.map { $0.describe() }.joined(separator: ",")
}

// MARK: - Class-bound `[String: any Marker]` dictionary VALUE carrier (audit P1-08, Dictionary sibling)
//
// The same stride bug as `[any Marker]`, but with the existential as a Dictionary VALUE.
// `MemoryLayout<any Marker>.stride == 16` (class-bound), so a `[String: any Marker]` stores 16-byte
// values — the layout is a property of the type, NOT the container, so it is identical to the array
// element case. Building `SwiftDictionary<_, ExistentialContainer1>` (40-byte slots) reads garbage
// classRef/witness for every value → SIGSEGV / over-release on dispatch. (Dictionary KEYS can't be
// class-bound existentials: `any P` is not `Hashable`, so `[any P: V]` is ill-formed — only the value
// crosses through the existential carrier.) Consumers iterate ALL values so a wrong stride surfaces
// as a crash or wrong sum, not a lucky single-value hit.

/// PARAM direction: C# passes a `[String: any Marker]`; this sums `markerId()` across all values,
/// touching every value (not just one). A 40-byte fill makes Swift read garbage at the 2nd value on.
public func sumMarkerIdsByKey(_ xs: [String: any Marker]) -> Int {
    var total = 0
    for (_, m) in xs { total += m.markerId() }
    return total
}

/// PARAM direction, count-only — proves the dictionary header marshals even when no value is read
/// (the `count`-succeeds / value-read-crashes asymmetry of the stride bug).
public func countMarkerMap(_ xs: [String: any Marker]) -> Int { xs.count }

/// WRITE direction: a witness-dispatched protocol getter requirement returning a class-bound
/// `[String: any Marker]`. A C# class implements this; Swift calls the getter (dispatching into the
/// C# implementation through its generated proxy / receiver) and reads the dictionary's values.
/// Exercises `ProtocolProxyEmitter.Receivers` dict-value getter carrier + value conversion (and,
/// transitively, the setter's `MarshalFromSwift<SwiftDictionary<_, carrier>>` ABI type).
public protocol MarkerMapProvider {
    var markerMap: [String: any Marker] { get }
}

/// Swift consumer reading a `MarkerMapProvider`'s `markerMap` (whoever implements it, including a C#
/// implementation reached through its proxy) and summing the ids across all values.
public func consumeMarkerMapProvider(_ p: MarkerMapProvider) -> Int {
    var total = 0
    for (_, m) in p.markerMap { total += m.markerId() }
    return total
}

// MARK: - OWNED-RETURN class-bound existential collections (audit P1-07 element leak / P1-08 stride)
//
// The PARAM (`sumMarkerIds`) and WRITE (`MarkerProvider`) fixtures above cover C#→Swift and
// witness-getter directions. These two factories cover the OWNED-RETURN direction P1-07/P1-08 actually
// fixed: Swift hands C# an *owned* `[any Marker]` / `[String: any Marker]` whose elements are class-bound
// existentials (16-byte `[classRef][witnessTable]` cells). The generated owned-return element/value
// conversion routes each cell through `new MarkerProxy(e, ownsContainer: true)`, which must ADOPT the +1
// the SwiftArray subscript getter (`$sSayxSicig`, InitializeWithCopy) / SwiftDictionary value move-out
// (`MarshalMovedValueFromSlot`) lays on the class ref, and release it on Dispose. `MarkerImpl` already
// feeds the shared `LifetimeTracker` counters (alloc/deinit), so a C# leak probe can assert the ARC
// balance directly: every materialized proxy AND the source carrier disposed must drive the live count
// back to 0 — a non-owning proxy orphans one element retain per materialization (leak), and a
// double-adopt double-frees on Dispose (crash). Conformers are FRESH per call so the count is exactly
// `count`.

/// OWNED-RETURN: an owned `[any Marker]` of `count` fresh tracked conformers. Exercises
/// `ArrayProjection.OwnedReturnElementConversion` → `ExistentialProjection.GetOwnedReturnElementConversion`.
public func makeTrackedMarkerArray(count: Int) -> [any Marker] {
    var result: [any Marker] = []
    result.reserveCapacity(count)
    for i in 0..<count { result.append(MarkerImpl(mid: i)) }
    return result
}

/// OWNED-RETURN, Dictionary VALUE sibling: an owned `[String: any Marker]` of `count` fresh tracked
/// conformers. Exercises `DictionaryProjection.OwnedReturnValueConversion` (move-out at +1 adopted by
/// the owns:true proxy). Keys are plain `String`s (class-bound existentials can't be dictionary keys).
public func makeTrackedMarkerMap(count: Int) -> [String: any Marker] {
    var result: [String: any Marker] = [:]
    for i in 0..<count { result["k\(i)"] = MarkerImpl(mid: i) }
    return result
}

// MARK: - PARAM/WRITE OPAQUE (non-class-bound) `[any P]` / `[String: any P]` element ownership
//
// The class-bound PARAM/WRITE fixtures above (`sumMarkerIds`, `MarkerProvider`) drive the 16-byte
// `ClassExistentialContainer1` carrier (audit P1-08), whose `__owned` array/dict write is balanced by
// `ExistentialContainerFactory.CreateOwnedClassCarrier` minting a +1 on the class ref. These fixtures
// drive the OPAQUE sibling: a non-class-bound `any BugReproExistentialItem` strides over the full
// 40-byte `ExistentialContainer1`, and the C#→Swift collection-element conversion previously routed a
// Swift-vended (borrowed) proxy straight through `GetOrCreate(...).GetExistentialContainer()` — which
// ALIASES the proxy's only +1. `SwiftArray<ExistentialContainer1>.FromEnumerable` raw-copies that
// aliased container into the array (`MarshalToSwift` → `IExistentialContainer.CopyTo`, +0) and the
// `__owned` `Array.append` ($sSa6appendyyxnF) consumes it, so disposing the temporary array runs the
// existential value-witness destroy and OVER-RELEASES the proxy's payload. A correct carrier mints/
// donates its own +1 (the opaque analogue of `CreateOwnedClassCarrier`, via the existential
// value-witness `InitializeWithCopy`).
//
// `TrackedOpaqueItem` feeds the same shared `LifetimeTracker` counters as `MarkerImpl`, so the C#
// probe asserts the balance directly: vending N Swift-backed proxies and passing them through a
// `[any P]` / `[String: any P]` param must NOT prematurely deinit them (live count stays N across the
// call) and disposing them must drive the count back to 0. An aliasing carrier shows up as a premature
// deinit (live < N right after the call) and/or a double-free crash on the proxy's own `Dispose`.

/// Tracked class conformer to the non-class-bound `BugReproExistentialItem` (40-byte opaque
/// `ExistentialContainer1` carrier). Feeds the shared allocation counters so the opaque PARAM/WRITE
/// element-ownership probe can assert ARC balance without relying on GC timing.
public final class TrackedOpaqueItem: BugReproExistentialItem {
    private let tag: Int
    public init(tag: Int) { self.tag = tag; recordTrackedAllocation() }
    deinit { recordTrackedDeallocation() }
    public func describe() -> String { "item-\(tag)" }
}

/// Vends a Swift-backed `any BugReproExistentialItem`, so C# holds a real (borrowed) proxy to pass
/// back through the `[any P]` / `[String: any P]` param paths — the branch the C#-conformer control
/// (`joinItemDescriptions([BugReproExistentialItemImpl(...)])`, which boxes/mints) never exercises.
public func makeTrackedOpaqueItem(tag: Int) -> any BugReproExistentialItem {
    TrackedOpaqueItem(tag: tag)
}

/// PARAM direction, Dictionary VALUE sibling of `joinItemDescriptions`: sums nothing — joins every
/// value's `describe()` (sorted by key for determinism), touching every opaque existential value so a
/// mis-marshalled carrier surfaces. Exercises `DictionaryProjection` opaque value-carrier conversion.
public func joinItemDescriptionsByKey(_ xs: [String: any BugReproExistentialItem]) -> String {
    xs.sorted { $0.key < $1.key }.map { $0.value.describe() }.joined(separator: ",")
}
