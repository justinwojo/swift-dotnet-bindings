// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Collections hashed by a bound generic Swift class
//
// A bound generic class (`SwiftKeyBox<String>`) is a key type of its own: Swift hashes and compares
// it through the Hashable conformance of that instantiation (here a conditional one, which holds
// only because String is Hashable), and a collection's storage holds a reference on every key.
// A collection C# builds over it must be one over that bound class, not over its bare pointer.

/// A generic Swift class that is Hashable exactly when its value is: two boxes of equal values are equal.
public final class SwiftKeyBox<Value> {
    public let value: Value

    public init(value: Value) { self.value = value }
}

extension SwiftKeyBox: Equatable where Value: Equatable {
    public static func == (lhs: SwiftKeyBox, rhs: SwiftKeyBox) -> Bool { lhs.value == rhs.value }
}

extension SwiftKeyBox: Hashable where Value: Hashable {
    public func hash(into hasher: inout Hasher) { hasher.combine(value) }
}

/// A generic Swift class Hashable for every argument, by its name alone.
public final class SwiftKeyLabel<Tag>: Hashable {
    public let name: String

    public init(name: String) { self.name = name }

    public static func == (lhs: SwiftKeyLabel, rhs: SwiftKeyLabel) -> Bool { lhs.name == rhs.name }
    public func hash(into hasher: inout Hasher) { hasher.combine(name) }
}

// MARK: C# builds, Swift looks up

/// Whether the set holds a box of `value`, looked up with a box Swift makes.
public func swiftBoxSetContains(_ boxes: Set<SwiftKeyBox<String>>, value: String) -> Bool {
    boxes.contains(SwiftKeyBox(value: value))
}

/// The value stored under the box of `value`, or nil when Swift finds none.
public func swiftBoxLookUp(_ index: [SwiftKeyBox<Int>: Int], value: Int) -> Int? {
    index[SwiftKeyBox(value: value)]
}

/// Whether the set holds a box of `value`, or `nil` when `value` is nil.
public func swiftBoxOptionalSetContains(_ boxes: Set<SwiftKeyBox<String>?>, value: String?) -> Bool {
    boxes.contains(value.map { SwiftKeyBox(value: $0) })
}

/// The value stored under the box of `value`, or under `nil` when `value` is nil.
public func swiftBoxOptionalLookUp(_ index: [SwiftKeyBox<String>?: Int], value: String?) -> Int? {
    index[value.map { SwiftKeyBox(value: $0) }]
}

/// Whether the inner set stored under `group` holds a box of `value`.
public func swiftBoxNestedContains(_ groups: [String: Set<SwiftKeyBox<String>>], group: String, value: String) -> Bool {
    groups[group]?.contains(SwiftKeyBox(value: value)) ?? false
}

/// Whether the set at `index` holds a box of `value`.
public func swiftBoxArrayContains(_ list: [Set<SwiftKeyBox<String>>], index: Int, value: String) -> Bool {
    guard index >= 0, index < list.count else { return false }
    return list[index].contains(SwiftKeyBox(value: value))
}

/// 1 when the optional set holds a box of `value`, 0 when it does not, -1 when it is nil.
public func swiftBoxOptionalContainerContains(_ boxes: Set<SwiftKeyBox<String>>?, value: String) -> Int {
    guard let boxes else { return -1 }
    return boxes.contains(SwiftKeyBox(value: value)) ? 1 : 0
}

/// Whether the set of labels holds one named `name`.
public func swiftLabelSetContains(_ labels: Set<SwiftKeyLabel<Int>>, name: String) -> Bool {
    labels.contains(SwiftKeyLabel(name: name))
}

// MARK: Swift builds, C# looks up

/// A set of boxes of the given values.
public func swiftBoxMakeSet(_ values: [String]) -> Set<SwiftKeyBox<String>> {
    Set(values.map { SwiftKeyBox(value: $0) })
}

/// A dictionary whose value for each box is its position in `values`.
public func swiftBoxMakeIndex(_ values: [Int]) -> [SwiftKeyBox<Int>: Int] {
    var result: [SwiftKeyBox<Int>: Int] = [:]
    for (position, value) in values.enumerated() { result[SwiftKeyBox(value: value)] = position }
    return result
}

/// A set of optional boxes, `nil` for a nil value.
public func swiftBoxMakeOptionalSet(_ values: [String?]) -> Set<SwiftKeyBox<String>?> {
    Set(values.map { $0.map { SwiftKeyBox(value: $0) } })
}

/// A set of labels with the given names.
public func swiftLabelMakeSet(_ names: [String]) -> Set<SwiftKeyLabel<Int>> {
    Set(names.map { SwiftKeyLabel(name: $0) })
}

// MARK: Reverse dispatch
//
// No closure over a bound generic class collection here: the projection factory leaves every
// user-defined bound generic unprojected, so such a closure is skipped as unsupported.

/// A conformer C# implements: Swift asks it for a set and hands it one.
public protocol SwiftKeyBoxSource: AnyObject {
    func boxes() -> Set<SwiftKeyBox<String>>
    func position(in boxes: Set<SwiftKeyBox<String>>, of value: String) -> Int
    func position(inOptional boxes: Set<SwiftKeyBox<String>>?, of value: String) -> Int
}

/// Whether the set the conformer returns holds a box of `value`.
public func swiftBoxContainsFromSource(_ source: SwiftKeyBoxSource, value: String) -> Bool {
    source.boxes().contains(SwiftKeyBox(value: value))
}

/// Hands the conformer a set Swift built and returns its answer for `value`.
public func swiftBoxAskSource(_ source: SwiftKeyBoxSource, values: [String], value: String) -> Int {
    source.position(in: swiftBoxMakeSet(values), of: value)
}

/// Hands the conformer an optional set Swift built (nil when `values` is nil) and returns its answer for `value`.
public func swiftBoxAskSourceOptional(_ source: SwiftKeyBoxSource, values: [String]?, value: String) -> Int {
    source.position(inOptional: values.map(swiftBoxMakeSet), of: value)
}

// MARK: Ownership

/// Keeps the collections of boxes it is handed, and reads them back later.
public final class SwiftKeyBoxStore {
    private var keptSet: Set<SwiftKeyBox<String>> = []
    private var keptIndex: [SwiftKeyBox<Int>: Int] = [:]
    private var keptOptional: Set<SwiftKeyBox<String>?> = []

    public init() {}

    public func keep(_ boxes: Set<SwiftKeyBox<String>>) { keptSet = boxes }
    public func keepIndex(_ index: [SwiftKeyBox<Int>: Int]) { keptIndex = index }
    public func keepOptional(_ boxes: Set<SwiftKeyBox<String>?>) { keptOptional = boxes }

    /// The kept set, as a property C# can set.
    public var boxes: Set<SwiftKeyBox<String>> {
        get { keptSet }
        set { keptSet = newValue }
    }

    /// The kept set's values, sorted: reads every box, so one freed after C# let go of it shows up here.
    public func keptValues() -> String { keptSet.map { $0.value }.sorted().joined(separator: ",") }

    /// The kept dictionary as `value=position` pairs, sorted by value.
    public func keptIndexEntries() -> String {
        keptIndex.sorted { $0.key.value < $1.key.value }.map { "\($0.key.value)=\($0.value)" }.joined(separator: ",")
    }

    /// The kept optional set's values, `nil` for its nil element, sorted.
    public func keptOptionalValues() -> String {
        keptOptional.map { $0?.value ?? "nil" }.sorted().joined(separator: ",")
    }

    /// Whether the kept set holds this very object (identity, not equality).
    public func keepsObject(_ box: SwiftKeyBox<String>) -> Bool { keptSet.contains { $0 === box } }

    /// Whether the kept set holds a box of `value`, looked up with a box Swift makes.
    public func contains(_ value: String) -> Bool { keptSet.contains(SwiftKeyBox(value: value)) }

    public func clear() {
        keptSet = []
        keptIndex = [:]
        keptOptional = []
    }
}

/// Keeps an array of boxes: the control for the retain counts.
public final class SwiftKeyBoxArrayStore {
    private var kept: [SwiftKeyBox<String>] = []

    public init() {}

    public func keep(_ boxes: [SwiftKeyBox<String>]) { kept = boxes }
    public func clear() { kept = [] }
}

/// The strong retain count of a box C# hands over. Only differences between two reads mean anything.
public func swiftBoxRetainCount(_ box: SwiftKeyBox<String>) -> Int {
    Int(_getRetainCount(box))
}

// MARK: - A nil optional key
//
// `nil` is a key like any other in `[Node?: V]` and an element like any other in `Set<Node?>`, in
// every direction: a dictionary C# builds may hold it, and one Swift hands C# (a return, a property,
// a closure argument, a conformer's argument, a nested dictionary) must keep it.

/// A dictionary of optional nodes, each valued by its position in `names`, `nil` for a nil name.
public func swiftKeyMakeOptionalIndex(_ names: [String?]) -> [SwiftKeyNode?: Int] {
    var result: [SwiftKeyNode?: Int] = [:]
    for (position, name) in names.enumerated() { result[name.map { SwiftKeyNode(name: $0) }] = position }
    return result
}

/// Optional-keyed dictionaries inside an array: each of `names` in a dictionary of its own.
public func swiftKeyMakeOptionalIndexList(_ names: [String?]) -> [[SwiftKeyNode?: Int]] {
    names.enumerated().map { position, name in [name.map { SwiftKeyNode(name: $0) }: position] }
}

/// The entries of a dictionary C# built, as `name=value` pairs, `nil` for its nil key, sorted.
public func swiftKeyOptionalIndexEntries(_ index: [SwiftKeyNode?: Int]) -> String {
    index.map { "\($0.key?.name ?? "nil")=\($0.value)" }.sorted().joined(separator: ",")
}

/// Hands C# an optional-keyed dictionary Swift built, and returns what C# made of it.
public func swiftKeyCallWithOptionalIndex(_ names: [String?], _ body: ([SwiftKeyNode?: Int]) -> Int) -> Int {
    body(swiftKeyMakeOptionalIndex(names))
}

/// Hands C# a set of optional nodes Swift built, and returns what C# made of it.
public func swiftKeyCallWithOptionalSet(_ names: [String?], _ body: (Set<SwiftKeyNode?>) -> Int) -> Int {
    body(swiftKeyMakeOptionalSet(names))
}

/// A conformer C# implements: Swift asks it for optional-keyed collections and hands it some.
public protocol SwiftKeyOptionalSource: AnyObject {
    func optionalIndex() -> [SwiftKeyNode?: Int]
    func value(in index: [SwiftKeyNode?: Int], of name: String?) -> Int
    func count(in nodes: Set<SwiftKeyNode?>, of name: String?) -> Int
}

/// The value stored under the node named `name` (under `nil` when `name` is nil) in the dictionary the conformer returns.
public func swiftKeyOptionalLookUpFromSource(_ source: SwiftKeyOptionalSource, name: String?) -> Int? {
    source.optionalIndex()[name.map { SwiftKeyNode(name: $0) }]
}

/// Hands the conformer an optional-keyed dictionary Swift built and returns its answer for `name`.
public func swiftKeyAskOptionalSource(_ source: SwiftKeyOptionalSource, names: [String?], name: String?) -> Int {
    source.value(in: swiftKeyMakeOptionalIndex(names), of: name)
}

/// Hands the conformer a set of optional nodes Swift built and returns its answer for `name`.
public func swiftKeyAskOptionalSetSource(_ source: SwiftKeyOptionalSource, names: [String?], name: String?) -> Int {
    source.count(in: swiftKeyMakeOptionalSet(names), of: name)
}

/// Keeps an optional-keyed dictionary behind a property.
public final class SwiftKeyOptionalIndexStore {
    public var index: [SwiftKeyNode?: Int] = [:]

    public init() {}

    /// The kept dictionary as `name=value` pairs, `nil` for its nil key, sorted.
    public func entries() -> String { swiftKeyOptionalIndexEntries(index) }
}

/// A dictionary keyed by optional UUIDs, each valued by its position, `nil` for a nil string.
public func swiftUUIDMakeOptionalIndex(_ uuids: [String?]) -> [NSUUID?: Int] {
    var result: [NSUUID?: Int] = [:]
    for (position, text) in uuids.enumerated() { result[text.flatMap { NSUUID(uuidString: $0) }] = position }
    return result
}

/// The value stored under the UUID `uuid` (under `nil` when `uuid` is nil).
public func swiftUUIDOptionalLookUp(_ index: [NSUUID?: Int], uuid: String?) -> Int? {
    index[uuid.flatMap { NSUUID(uuidString: $0) }]
}

/// Hands C# an optional-UUID-keyed dictionary Swift built, and returns what C# made of it.
public func swiftUUIDCallWithOptionalIndex(_ uuids: [String?], _ body: ([NSUUID?: Int]) -> Int) -> Int {
    body(swiftUUIDMakeOptionalIndex(uuids))
}

/// Optional-UUID-keyed dictionaries inside an array: each of `uuids` in a dictionary of its own.
public func swiftUUIDMakeOptionalIndexList(_ uuids: [String?]) -> [[NSUUID?: Int]] {
    uuids.enumerated().map { position, text in [text.flatMap { NSUUID(uuidString: $0) }: position] }
}

/// A conformer C# implements: Swift asks it for an optional-UUID-keyed dictionary and hands it one.
public protocol SwiftUUIDOptionalSource: AnyObject {
    func optionalIndex() -> [NSUUID?: Int]
    func value(in index: [NSUUID?: Int], of uuid: String?) -> Int
}

/// The value stored under `uuid` (under `nil` when `uuid` is nil) in the dictionary the conformer returns.
public func swiftUUIDOptionalLookUpFromSource(_ source: SwiftUUIDOptionalSource, uuid: String?) -> Int? {
    source.optionalIndex()[uuid.flatMap { NSUUID(uuidString: $0) }]
}

/// Hands the conformer an optional-UUID-keyed dictionary Swift built and returns its answer for `uuid`.
public func swiftUUIDAskOptionalSource(_ source: SwiftUUIDOptionalSource, uuids: [String?], uuid: String?) -> Int {
    source.value(in: swiftUUIDMakeOptionalIndex(uuids), of: uuid)
}
