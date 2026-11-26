// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Optional value keys
//
// `Optional<Wrapped>` is Hashable exactly when `Wrapped` is, through the standard library's
// conditional conformance. A dictionary keyed by an optional value type (`[Int?: V]`, `[String?: V]`,
// an optional enum or struct) and a set of optional values need that conformance's witness on
// every crossing, and hold `nil` as a key or element like any other.

/// A plain enum used as an optional key.
public enum OptionalKeyShade: Hashable {
    case red
    case green
    case blue
}

/// A struct used as an optional key, Hashable by both coordinates.
public struct OptionalKeyPoint: Hashable {
    public var x: Int
    public var y: Int

    public init(x: Int, y: Int) {
        self.x = x
        self.y = y
    }
}

/// A dictionary of optional integers, each valued by its position in `values`.
public func optionalKeyMakeIntIndex(_ values: [Int?]) -> [Int?: Int] {
    var result: [Int?: Int] = [:]
    for (position, value) in values.enumerated() { result[value] = position }
    return result
}

/// The value stored under `value` (under `nil` when `value` is nil).
public func optionalKeyIntLookUp(_ index: [Int?: Int], value: Int?) -> Int? {
    index[value]
}

/// The entries of a dictionary C# built, as `key=value` pairs, `nil` for its nil key, sorted.
public func optionalKeyIntEntries(_ index: [Int?: Int]) -> String {
    index.map { "\($0.key.map(String.init) ?? "nil")=\($0.value)" }.sorted().joined(separator: ",")
}

/// Optional-integer-keyed dictionaries inside an array: each of `values` in a dictionary of its own.
public func optionalKeyMakeIntIndexList(_ values: [Int?]) -> [[Int?: Int]] {
    values.enumerated().map { position, value in [value: position] }
}

/// A set of optional integers.
public func optionalKeyMakeIntSet(_ values: [Int?]) -> Set<Int?> {
    Set(values)
}

/// Whether the set holds `value` (`nil` when `value` is nil).
public func optionalKeyIntSetContains(_ values: Set<Int?>, value: Int?) -> Bool {
    values.contains(value)
}

/// A dictionary of optional strings, each valued by its position in `values`.
public func optionalKeyMakeStringIndex(_ values: [String?]) -> [String?: Int] {
    var result: [String?: Int] = [:]
    for (position, value) in values.enumerated() { result[value] = position }
    return result
}

/// The value stored under `value` (under `nil` when `value` is nil).
public func optionalKeyStringLookUp(_ index: [String?: Int], value: String?) -> Int? {
    index[value]
}

/// A dictionary of optional shades: red at 0, nil at 1, blue at 2.
public func optionalKeyMakeShadeIndex() -> [OptionalKeyShade?: Int] {
    [.red: 0, nil: 1, .blue: 2]
}

/// The value stored under `shade` (under `nil` when `shade` is nil).
public func optionalKeyShadeLookUp(_ index: [OptionalKeyShade?: Int], shade: OptionalKeyShade?) -> Int? {
    index[shade]
}

/// A dictionary of optional points: (1, 2) at 0, nil at 1.
public func optionalKeyMakePointIndex() -> [OptionalKeyPoint?: Int] {
    [OptionalKeyPoint(x: 1, y: 2): 0, nil: 1]
}

/// The value stored under the point (`x`, `y`), or under `nil` when `present` is false.
public func optionalKeyPointLookUp(_ index: [OptionalKeyPoint?: Int], present: Bool, x: Int, y: Int) -> Int? {
    index[present ? OptionalKeyPoint(x: x, y: y) : nil]
}

// MARK: - Nested collections keyed by a bound generic class
//
// A dictionary or set inside another collection keeps Swift's key equality, optional key or not:
// two boxes of the same value are one key, whichever object C# looks one up with.

/// Optional-box-keyed dictionaries inside an array: each of `values` in a dictionary of its own.
public func swiftBoxMakeOptionalIndexList(_ values: [String?]) -> [[SwiftKeyBox<String>?: Int]] {
    values.enumerated().map { position, value in [value.map { SwiftKeyBox(value: $0) }: position] }
}

/// One optional-box-keyed dictionary under the name "all", valuing each box by its position.
public func swiftBoxMakeOptionalIndexGroups(_ values: [String?]) -> [String: [SwiftKeyBox<String>?: Int]] {
    var index: [SwiftKeyBox<String>?: Int] = [:]
    for (position, value) in values.enumerated() { index[value.map { SwiftKeyBox(value: $0) }] = position }
    return ["all": index]
}

/// Sets of optional boxes inside an array: all of `values` in one set.
public func swiftBoxMakeOptionalSetList(_ values: [String?]) -> [Set<SwiftKeyBox<String>?>] {
    [Set(values.map { $0.map { SwiftKeyBox(value: $0) } })]
}

/// Box-keyed dictionaries inside an array: each of `values` in a dictionary of its own.
public func swiftBoxMakeIndexList(_ values: [String]) -> [[SwiftKeyBox<String>: Int]] {
    values.enumerated().map { position, value in [SwiftKeyBox(value: value): position] }
}

/// One box-keyed dictionary under the name "all", valuing each box by its position.
public func swiftBoxMakeIndexGroups(_ values: [String]) -> [String: [SwiftKeyBox<String>: Int]] {
    var index: [SwiftKeyBox<String>: Int] = [:]
    for (position, value) in values.enumerated() { index[SwiftKeyBox(value: value)] = position }
    return ["all": index]
}

/// Sets of boxes inside an array: all of `values` in one set.
public func swiftBoxMakeSetList(_ values: [String]) -> [Set<SwiftKeyBox<String>>] {
    [Set(values.map { SwiftKeyBox(value: $0) })]
}

/// A conformer C# implements: Swift hands it nested optional-box-keyed dictionaries.
public protocol SwiftBoxNestedSource: AnyObject {
    func value(in list: [[SwiftKeyBox<String>?: Int]], of value: String) -> Int
}

/// Hands the conformer nested optional-box-keyed dictionaries Swift built and returns its answer for `value`.
public func swiftBoxAskNestedSource(_ source: SwiftBoxNestedSource, values: [String?], value: String) -> Int {
    source.value(in: swiftBoxMakeOptionalIndexList(values), of: value)
}

// MARK: - A nil optional key beside a bridged value
//
// A `URL` value sends the whole dictionary through `NSDictionary`, where Swift spells a `nil` key
// as `NSNull`. The nil key must survive that crossing in both directions.

/// A dictionary of optional UUIDs valued by URLs: `https://example.com/<position>` for each, nil for a nil string.
public func swiftUUIDMakeOptionalURLIndex(_ uuids: [String?]) -> [NSUUID?: URL] {
    var result: [NSUUID?: URL] = [:]
    for (position, text) in uuids.enumerated() {
        result[text.flatMap { NSUUID(uuidString: $0) }] = URL(string: "https://example.com/\(position)")!
    }
    return result
}

/// The URL stored under `uuid` (under `nil` when `uuid` is nil), as a string.
public func swiftUUIDOptionalURLLookUp(_ index: [NSUUID?: URL], uuid: String?) -> String? {
    index[uuid.flatMap { NSUUID(uuidString: $0) }]?.absoluteString
}

/// The number of entries of a dictionary C# built.
public func swiftUUIDOptionalURLCount(_ index: [NSUUID?: URL]) -> Int {
    index.count
}

/// An optional dictionary of optional UUIDs valued by URLs, nil when `uuids` is nil.
public func swiftUUIDMakeOptionalURLIndexOrNil(_ uuids: [String?]?) -> [NSUUID?: URL]? {
    uuids.map(swiftUUIDMakeOptionalURLIndex)
}

/// A conformer C# implements: Swift asks it for a dictionary with a nil key and hands it one.
public protocol SwiftUUIDURLSource: AnyObject {
    func urls() -> [NSUUID?: URL]
    func url(in index: [NSUUID?: URL], of uuid: String?) -> String
}

/// The URL stored under `uuid` (under `nil` when `uuid` is nil) in the dictionary the conformer returns.
public func swiftUUIDURLFromSource(_ source: SwiftUUIDURLSource, uuid: String?) -> String? {
    source.urls()[uuid.flatMap { NSUUID(uuidString: $0) }]?.absoluteString
}

/// Hands the conformer a dictionary Swift built and returns its answer for `uuid`.
public func swiftUUIDAskURLSource(_ source: SwiftUUIDURLSource, uuids: [String?], uuid: String?) -> String {
    source.url(in: swiftUUIDMakeOptionalURLIndex(uuids), of: uuid)
}
