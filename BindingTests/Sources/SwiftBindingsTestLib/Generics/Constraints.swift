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
