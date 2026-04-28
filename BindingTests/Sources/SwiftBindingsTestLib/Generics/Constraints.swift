// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol with Self or Associated Type Requirements

/// A protocol requiring an add operation.
public protocol Summable {
    func add(_ other: Self) -> Self
}

/// Conforming frozen struct for Summable.
@frozen
public struct SummableInt32: Summable {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func add(_ other: SummableInt32) -> SummableInt32 {
        return SummableInt32(value: value + other.value)
    }
}

// MARK: - Generic Type with Constraints

/// A generic struct constrained by Summable.
/// Note: not @frozen — binding generator cannot resolve generic type parameter layouts.
public struct AcceptsSummable<T: Summable> {
    public let item: T

    public init(item: T) {
        self.item = item
    }

    public func addWith(_ other: T) -> T {
        return item.add(other)
    }
}

// MARK: - Where-Clause Functions

/// Generic function with a where clause constraining to Summable.
public func sumTwo<T: Summable>(_ a: T, _ b: T) -> T {
    return a.add(b)
}

/// Generic function with multiple where clauses.
public func describeConstrained<T>(_ item: T) -> String where T: Describable, T: TestIdentifiable {
    return "[\(item.id)] \(item.describe())"
}

// MARK: - Concrete Protocol Specialization

/// A protocol with a Self requirement — triggers GenericProtocolConstraint skip.
/// The concrete specialization engine provides overloads for known conformers.
public protocol Processable {
    func process() -> Self
    var label: String { get }
}

/// First conformer for Processable.
@frozen
public struct TextItem: Processable {
    public let text: String

    public init(text: String) {
        self.text = text
    }

    public func process() -> TextItem {
        return TextItem(text: text.uppercased())
    }

    public var label: String { return "text:\(text)" }
}

/// Second conformer for Processable.
@frozen
public struct NumberItem: Processable {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func process() -> NumberItem {
        return NumberItem(value: value * 2)
    }

    public var label: String { return "number:\(value)" }
}

/// Non-generic struct with method-level generic constrained to Processable.
/// The specialization engine emits one concrete overload per known conformer.
@frozen
public struct ItemProcessor {
    public let prefix: String

    public init(prefix: String) {
        self.prefix = prefix
    }

    /// Method with protocol-constrained generic parameter.
    /// Specialized to: processItem(TextItem) and processItem(NumberItem).
    public func processItem<T: Processable>(_ item: T) -> String {
        let result = item.process()
        return "\(prefix): \(result.label)"
    }

    /// Static method with protocol-constrained generic parameter.
    public static func describe<T: Processable>(_ item: T) -> String {
        return item.label
    }
}

// MARK: - Generic Constructor Specialization (Fix 4)

/// Frozen struct with a generic constructor constrained to Processable.
/// The concrete specialization engine should emit one static factory method per conformer:
///   ProcessedItem.FromTextItem(TextItem), ProcessedItem.FromNumberItem(NumberItem).
@frozen
public struct ProcessedItem {
    public let title: String

    /// Generic constructor — specialized to From{Conformer} static factories in C#.
    public init<T: Processable>(from source: T) {
        self.title = source.label
    }

    /// Non-generic init so the struct is usable directly.
    public init(title: String) {
        self.title = title
    }
}

// MARK: - some Collection<T> Specialization (Fix 4)

/// Host with a method taking `some Collection<String>` — opaque parameter with
/// a parameterized protocol. The specialization engine emits one concrete overload
/// for the `[String]` conformer.
public class CollectionHost {
    private let separator: String

    public init(separator: String) {
        self.separator = separator
    }

    /// Accepts any collection whose Element is String. Should specialize to
    /// Array<String> (see Swift.Collection hint in specialization-hints.json).
    public func joinItems(_ items: some Collection<String>) -> String {
        return items.joined(separator: separator)
    }
}

// MARK: - M2: Generic Constructor with PWT (DifferenceKit DifferentiableBox pattern)

/// Generic class where the constructor requires both type metadata and a protocol witness table.
/// The PWT is for the Describable constraint.
public class ConstrainedBox<T: Describable> {
    public let item: T

    public init(item: T) {
        self.item = item
    }

    public func getDescription() -> String {
        return item.describe()
    }
}

// MARK: - Issue C — Parent-generic sugared type parameter in bound generic

/// Protocol constraint for CollectibleBag.
public protocol CollectibleItem {
    var collectibleId: String { get }
}

@frozen
public struct CollectibleCoin: CollectibleItem {
    public let collectibleId: String
    public init(collectibleId: String) { self.collectibleId = collectibleId }
}

/// MusicKit.MusicItemCollection shape: member signatures reference the parent's
/// own sugared generic name as a bound-generic argument.
public struct CollectibleBag<Item: CollectibleItem> {
    public let items: [Item]
    public init(items: [Item]) { self.items = items }

    public func paired() -> CollectiblePair<Item> {
        return CollectiblePair(first: items.first!, second: items.last!)
    }
}

@frozen
public struct CollectiblePair<Element: CollectibleItem> {
    public let first: Element
    public let second: Element
    public init(first: Element, second: Element) {
        self.first = first
        self.second = second
    }
}

/// Factory helper returning a concrete `CollectibleBag<CollectibleCoin>`. The generic
/// constructor is now emittable via the static-factory dispatch path (Array&lt;T&gt; where T
/// is a parent generic is accepted by `GenericDispatchEmitter.CanEmitStaticDispatch`), but
/// this free function is kept as the precedent for users who still hit a wrapper-blocked
/// ctor on a generic type. The return type also exercises Issue C bound-generic specialization.
public func makeCoinBag(firstId: String, secondId: String) -> CollectibleBag<CollectibleCoin> {
    return CollectibleBag(items: [
        CollectibleCoin(collectibleId: firstId),
        CollectibleCoin(collectibleId: secondId),
    ])
}

// MARK: - MusicKit.MusicItemCollection shape with nint-arithmetic Collection methods
//
// Session 2 Issue C regression fixture. Generic struct conforming to `Collection`
// where methods like `index(_:offsetBy:) -> Int`, `distance(from:to:) -> Int`, and
// `index(after:) -> Int` are pure Int arithmetic — their ABI signatures never
// reference the parent's generic parameter `Item`. Before the relaxation in
// `GenericDispatchEmitter.CanEmitStaticDispatch` the `signatureReferencesT`
// hard-gate rejected them on generic struct parents, matching the MusicKit
// `MusicItemCollection<TMusicItemType>` skip reason of `generic_parent`.
//
// Collection-family stored/computed properties (`startIndex`, `endIndex`, `items`)
// are also declared directly on this type — a separate fix in
// `PropertyWrapperEmitter.CanEmitGenericClassPropertyWrapper` routes them
// through `@_cdecl` static-dispatch wrappers instead of direct `CallConvSwift`
// (which trips Mono Issue 1 `!ji->async` with 2+ type-metadata args). Together
// these cover the full MusicKit parent-generic-param surface.

public struct MusicItemBag<Item: CollectibleItem>: Collection {
    public let items: [Item]

    public init(items: [Item]) {
        self.items = items
    }

    // Collection requirements — Index = Int via typealias inference.
    public var startIndex: Int { 0 }
    public var endIndex: Int { items.count }
    public subscript(position: Int) -> Item { items[position] }
    public func index(after i: Int) -> Int { i + 1 }

    // Explicit overrides of Collection protocol extensions. These are the
    // nint-only methods whose wrappers the pre-fix generator skipped. Keep
    // them as overrides (rather than relying on the default synthesis) so
    // they surface as declared methods on the struct in ABI JSON, which is
    // the shape MusicKit's ABI exports.
    public func index(_ i: Int, offsetBy distance: Int) -> Int {
        return i + distance
    }

    public func distance(from start: Int, to end: Int) -> Int {
        return end - start
    }

    // Round 6: `formIndex(_:offsetBy:)` with an `inout` nint parameter on a generic
    // struct parent. Matches MusicKit's `MusicItemCollection<TMusicItemType>.formIndex(nint)`
    // ABI shape — inout non-T-referencing type on a generic non-frozen struct. Before the
    // relaxation in `MethodWrapperEmitter.ShouldEmitWrapper` (and the matching shared guard
    // in `WrapperValidation.HasCdeclCompatibleFunctionShape`) the hard gate on "any inout
    // param on generic parent" forced SB0001 + raw CallConvSwift fallback. Declared
    // `mutating` to match the MusicKit ABI exactly, even though the body only reads self.
    public mutating func formIndex(_ i: inout Int, offsetBy distance: Int) {
        i = index(i, offsetBy: distance)
    }
}

/// Factory returning a concrete `MusicItemBag<CollectibleCoin>`. The direct C#
/// ctor is independently exercised by the runtime test; this factory keeps a
/// wrapper-free path in case future gate changes regress one or the other.
public func makeMusicItemBag(firstId: String, secondId: String, thirdId: String) -> MusicItemBag<CollectibleCoin> {
    return MusicItemBag(items: [
        CollectibleCoin(collectibleId: firstId),
        CollectibleCoin(collectibleId: secondId),
        CollectibleCoin(collectibleId: thirdId),
    ])
}

// MARK: - WeatherKit.Forecast<Element> shape — Collection with PRIVATE backing
//
// Session 3 Issue E.2 regression fixture. Generic struct conforming to
// `Collection` where the backing array is PRIVATE — only `startIndex`,
// `endIndex`, `subscript(Int) -> Element`, and `index(after:)` are public.
// The existing `MusicItemBag` fixture has a *public* `items: [Item]` property
// that the pre-fix `CollectionProjectionEmitter.TryFindBacking` used as the
// delegation target for `Count` / `this[int]` / `GetEnumerator`. Apple's
// WeatherKit `Forecast<Element>` has no such public backing — storage is
// opaque — so that path never fires and consumers can't iterate a forecast.
//
// This fixture forces the witness-dispatch fallback in
// `CollectionProjectionEmitter`: the projection must emit `Count` via
// `StartIndex` / `EndIndex`, and `this[int]` / `GetEnumerator` via the
// type's `subscript(Int) -> Element` witness — without any visible `[Element]`
// property to delegate to. Element type is `CollectibleCoin` to reuse the
// Session 2 `CollectibleItem` witness table plumbing.
public struct ForecastSeries<Element: CollectibleItem>: Collection {
    private let storage: [Element]

    public init(_ storage: [Element]) {
        self.storage = storage
    }

    // Collection requirements — Index = Int via typealias inference.
    public var startIndex: Int { 0 }
    public var endIndex: Int { storage.count }
    public subscript(position: Int) -> Element { storage[position] }
    public func index(after i: Int) -> Int { i + 1 }
}

/// Factory returning a concrete `ForecastSeries<CollectibleCoin>`. Mirrors the
/// `makeMusicItemBag` pattern so the runtime test can construct the value
/// without depending on the generic constructor wrapper path.
public func makeForecastSeries(firstId: String, secondId: String, thirdId: String) -> ForecastSeries<CollectibleCoin> {
    return ForecastSeries([
        CollectibleCoin(collectibleId: firstId),
        CollectibleCoin(collectibleId: secondId),
        CollectibleCoin(collectibleId: thirdId),
    ])
}

// MARK: - WeatherKit.Forecast<Element> Apple-shape — parent over 3-PWT threshold
//
// Round 5 Session 3 actual-cause fixture. `ForecastSeries<Element>` above uses
// `CollectibleItem` (no Self requirement, no associated types) so its metadata
// accessor stays thin-mode and its PWTs are all resolvable as static C#
// interfaces. Apple's `WeatherKit.Forecast<Element>` constrains Element by
// `Decodable & Encodable & Equatable & Sendable` — three non-marker PWTs that
// all carry Self requirements AND push (1 metadata + 3 PWTs) > 3 register slots,
// flipping the parent type's metadata accessor to buffer-mode ABI.
//
// The pre-2026-04-23 Collection-witness emitter bailed in that state for two
// reasons: (1) its Swift-side metadata helper was a thin-mode dlsym call that
// PAC-traps when asked for buffer-mode metadata, and (2) the PwtEntries gate
// rejected non-resolvable (descriptor-only) conformances. The fix passes parent
// type metadata directly from C# and drops both gates — so iteration works
// regardless of PWT shape. This fixture reproduces the exact Apple failure mode
// inside source-compiled BindingTests so the regression guard is durable.
//
// `IdentifiableCoin` is `Decodable & Encodable & Equatable` — the same three
// non-marker Self-requirement PWTs the WeatherKit surface declares, matching
// the constraint density and PWT-resolvability profile that pushes the parent
// type's metadata accessor into buffer-mode ABI.
public struct IdentifiableCoin: Decodable, Encodable, Equatable {
    public let identifier: String
    public init(identifier: String) { self.identifier = identifier }
}

/// Generic Collection whose Element carries the exact PWT shape of Apple's
/// `WeatherKit.Forecast<Element>`. Private storage so the witness-backed
/// projection path must fire (no public array backing to delegate to). All
/// Collection requirements are declared directly on the struct, not on a
/// conditional extension, so the ABI surfaces them.
public struct AppleShapedForecast<Element: Decodable & Encodable & Equatable>: Collection {
    private let storage: [Element]

    public init(_ storage: [Element]) {
        self.storage = storage
    }

    public var startIndex: Int { 0 }
    public var endIndex: Int { storage.count }
    public subscript(position: Int) -> Element { storage[position] }
    public func index(after i: Int) -> Int { i + 1 }
}

/// Factory returning a concrete `AppleShapedForecast<IdentifiableCoin>` for the
/// runtime tests — bypasses the generic constructor wrapper path.
public func makeAppleShapedForecast(
    firstId: String, secondId: String, thirdId: String
) -> AppleShapedForecast<IdentifiableCoin> {
    return AppleShapedForecast([
        IdentifiableCoin(identifier: firstId),
        IdentifiableCoin(identifier: secondId),
        IdentifiableCoin(identifier: thirdId),
    ])
}

// MARK: - RealityKit.RealityRenderer.EntityCollection shape — class-bounded sequence param
//
// Reproduces RealityFoundation Bug 3: a non-generic value type with a method
// `insert<S: Sequence>(contentsOf source: S, beforeIndex i: Int) where S.Element : SomeClass`.
// The class-inheritance bound is encoded in `genericSig` as `S.Element : SomeClass`, which the
// parser routes through `ConformanceKind.Protocol` (Swift writes any `:` clause that way). The
// pre-fix bilateral filter in `ConcreteProtocolSpecializationEmitter.DoesPairingSatisfyAssociated-
// TypeConstraints` skipped Protocol-kind entries unconditionally, so every Sequence conformer
// in the engine's pool — including `Foundation.Data` and `[UInt8]` — got paired with this
// method and the generator emitted wrappers whose bodies referenced the wrong element type.
// Wrapper.swift then failed to compile with `Data.Element (UInt8) does not inherit from Animal`.
//
// The fix consults the type database: when the constraint target resolves to a class, the
// filter enforces exact-name equality on the conformer's recorded `Element`. Conformers
// without recorded associated types (ABI-only) fail closed.
//
// `Animal` already exists in `Types/Classes.swift` as a public class with a `Dog: Animal`
// subclass — reusing it keeps the surface narrow.
public struct AnimalRoster {
    public private(set) var animals: [Animal]

    public init() { self.animals = [] }

    public init(_ animals: [Animal]) {
        self.animals = animals
    }

    /// `S.Element : Animal` — class-inheritance bound on a method-level generic.
    /// The pre-fix engine emitted broken wrappers for `[UInt8]` / `Foundation.Data`
    /// against this method; the post-fix engine drops them and only emits a wrapper
    /// for conformers whose recorded Element matches `SwiftBindingsTestLib.Animal`
    /// exactly (declared in specialization-hints.json).
    public mutating func insert<S: Sequence>(
        contentsOf source: S, beforeIndex i: Int
    ) where S.Element : Animal {
        let upcast: [Animal] = source.map { $0 as Animal }
        animals.insert(contentsOf: upcast, at: i)
    }

    public var count: Int { animals.count }
    public subscript(position: Int) -> Animal { animals[position] }
}

/// Factory returning a concrete `AnimalRoster` populated with two `Animal` instances.
/// Mirrors the `makeMusicItemBag` pattern so the runtime test doesn't depend on
/// the generic-method dispatch path for construction.
public func makeAnimalRoster(firstName: String, secondName: String) -> AnimalRoster {
    return AnimalRoster([
        Animal(name: firstName, sound: "Roar"),
        Animal(name: secondName, sound: "Howl"),
    ])
}

// MARK: - Bug 3 Follow-up: Protocol-target associated-type constraint
//
// Closes the gap left open after the original Bug 3 fix. The bilateral pairing
// filter previously passed through `ConformanceKind.Protocol` entries when the
// constraint target was itself a protocol (e.g., `where S.Element : SomeProtocol`).
// Pre-fix, the engine paired every Sequence conformer in the pool with
// `HashSink.sumHashes` regardless of whether the conformer's Element actually
// conformed to `HashLike` — including a non-conforming wrapper struct
// (`NonHashableBox`) registered alongside the conforming one. The generator
// stamped wrappers for both, and `Wrapper.swift` failed to compile with
// `'NonHashableBox' does not conform to protocol 'HashLike'`.
//
// Post-fix: the filter consults the type database. When the constraint target
// resolves to a protocol, it looks up the conformer's Element TypeRecord and
// checks (transitively) that the target appears in its `ProtocolConformances`.
// Conformers without the recorded conformance fail closed.
//
// `HashLike` is intentionally library-defined rather than stdlib `Hashable`,
// for two reasons: (1) the parser path populates `TypeRecord.ProtocolConformances`
// for both boxes inside the same module being generated, exercising the fresh-
// parser plumbing end-to-end; (2) older module-database XMLs predate the field
// and don't carry conformance metadata for stdlib protocols, which would
// short-circuit the filter to the fail-closed branch and weaken the test.

public protocol HashLike {
    var hashCode: Int { get }
}

/// Conforms to `HashLike` — should pair with `HashSink.sumHashes`.
@frozen
public struct HashableBox: HashLike {
    public let value: Int32
    public init(value: Int32) { self.value = value }
    public var hashCode: Int { return Int(value) }
}

/// Does NOT conform to `HashLike` — must NOT pair with `HashSink.sumHashes`.
/// Registered as a Swift.Sequence conformer in specialization-hints.json so the
/// bilateral filter sees both options. Build success is the regression detector:
/// without the fix, the generator emits a wrapper for this box whose body calls
/// `sumHashes` and Swift compilation fails on the missing conformance.
@frozen
public struct NonHashableBox {
    public let label: Int32
    public init(label: Int32) { self.label = label }
}

/// Non-`@frozen` so the C# emitter projects it as `ClassWithOpaquePayload`
/// (matching the `AnimalRoster` pattern above). The concrete-specialization
/// emitter currently emits `_payload.DangerousGetHandle()` for the parent
/// `self`, which only exists on class-projected types — a `@frozen` empty
/// struct projects as a value-type and the specialization fails to compile.
/// That's an unrelated gap in the CSM emitter's coverage of value-projected
/// parents; this fixture sidesteps it by following the existing class-shape
/// convention rather than expanding scope.
public struct HashSink {
    public init() {}

    /// `S.Element : HashLike` — protocol-conformance bound on a method-level
    /// generic. The bilateral filter must accept the `[HashableBox]` pairing
    /// (Element conforms) and reject the `[NonHashableBox]` pairing (Element
    /// does not conform).
    public func sumHashes<S: Sequence>(_ source: S) -> Int where S.Element : HashLike {
        var sum = 0
        for item in source { sum += item.hashCode }
        return sum
    }
}

// MARK: - HasSelfRequirement existential boxing
//
// Companion fixture to `PATFallbackBoundary.swift`. `TaggedAssociator` there
// triggers `HasAssociatedTypes` only; this protocol triggers
// `HasSelfRequirement` — the parser in `SwiftABIParser` looks for "Self." or
// "Self ==" in the protocol genericSig, and declaring `Stamp == Self` on the
// associated type plants both substrings. Both flags fire here, walking the
// shared `HasSelfRequirement || HasAssociatedTypes` lowering branch in
// `ExistentialHandler.GetPublicExistentialType()`. The conformance
// dictionary registration in
// `TypeHandlerHelpers.GenerateProtocolConformanceDictionaryEntries` then
// skips Self-requirement protocols from the standard `typeof(I{Proto})`
// entry, so the runtime `GetOrCreate<object>` lookup must be serviced by
// the `typeof(object)` entry the HasAssociatedTypes branch emits. The free
// function below dispatches `.anchorTag` through `any SelfReqAnchored`,
// exercising that lookup end-to-end; two distinct conformers prove the
// witness table routes to the concrete type rather than landing on a
// default.
public protocol SelfReqAnchored {
    associatedtype Stamp where Stamp == Self
    var anchorTag: String { get }
}

/// First conformer. Non-`@frozen` so it projects as `ClassWithOpaquePayload`
/// (matching the `IntTaggedAssociator` shape that already exercises the PAT
/// boxing path).
public struct StampedAlpha: SelfReqAnchored {
    public typealias Stamp = StampedAlpha
    public let anchorTag: String
    public init(anchorTag: String) { self.anchorTag = anchorTag }
}

/// Second conformer with a distinct `anchorTag` so dispatch routing through
/// the existential container is observable — if every value comes back with
/// the same tag, the witness table isn't actually carrying the concrete type.
public struct StampedOmega: SelfReqAnchored {
    public typealias Stamp = StampedOmega
    public let anchorTag: String
    public init(anchorTag: String) { self.anchorTag = anchorTag }
}

/// Free function with `any SelfReqAnchored` at the parameter position. The
/// generator must lower this through the
/// `HasSelfRequirement || HasAssociatedTypes` branch and the runtime must
/// resolve the witness table descriptor from the conformer's
/// `_protocolConformanceSymbols` dictionary so the property dispatch lands
/// on the concrete struct's `.anchorTag`.
public func readSelfReqAnchored(_ value: any SelfReqAnchored) -> String {
    return "stamp:\(value.anchorTag)"
}

