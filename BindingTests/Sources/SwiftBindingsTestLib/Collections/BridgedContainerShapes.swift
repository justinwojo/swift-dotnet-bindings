// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bridged containers a conformer receives
//
// A container whose elements bridge to Objective-C (`[URL]`, `Set<URL>`, `[K: URL]`, `[URL: V]`,
// nested or optional) crosses to .NET as the Objective-C collection it bridges to. That holds when
// Swift hands one to a conformer C# implements, as a method argument, a property's new value or a
// subscript index, and when the conformer hands one back.

/// A conformer C# implements: Swift hands it containers of URLs and asks for them back.
public protocol URLContainerSink: AnyObject {
    func describeArray(_ urls: [URL]) -> String
    func describeSet(_ urls: Set<URL>) -> String
    func describeIndex(_ urls: [String: URL]) -> String
    func describeNames(_ names: [URL: String]) -> String
    func describeOptionalArray(_ urls: [URL]?) -> String
    func describeGroups(_ groups: [String: [URL]]) -> String
    func describeRows(_ rows: [[URL]]) -> String
    // Sibling parameters named as a thunk might name its own locals for `urls` and `url`.
    func describeTagged(_ urls: [URL], urlsNS: String, url: URL, urlNS: Int, __urlsBridged: Int) -> String
    var storedURLs: [URL] { get set }
    var storedGroups: [String: [URL]] { get set }
    subscript(count urls: [URL]) -> Int { get }
    func makeGroups() -> [String: [URL]]
    func makeOptionalArray(_ present: Bool) -> [URL]?
}

private func sinkURL(_ name: String) -> URL { URL(string: "https://example.com/\(name)")! }

/// What the conformer says about `[a, b]`.
public func urlSinkDescribeArray(_ sink: URLContainerSink) -> String {
    sink.describeArray([sinkURL("a"), sinkURL("b")])
}

/// What the conformer says about the set `{a, b}`.
public func urlSinkDescribeSet(_ sink: URLContainerSink) -> String {
    sink.describeSet([sinkURL("a"), sinkURL("b")])
}

/// What the conformer says about `["a": a, "b": b]`.
public func urlSinkDescribeIndex(_ sink: URLContainerSink) -> String {
    sink.describeIndex(["a": sinkURL("a"), "b": sinkURL("b")])
}

/// What the conformer says about `[a: "a", b: "b"]`, keyed by URLs.
public func urlSinkDescribeNames(_ sink: URLContainerSink) -> String {
    sink.describeNames([sinkURL("a"): "a", sinkURL("b"): "b"])
}

/// What the conformer says about `[c]`, or about nil when `present` is false.
public func urlSinkDescribeOptionalArray(_ sink: URLContainerSink, present: Bool) -> String {
    sink.describeOptionalArray(present ? [sinkURL("c")] : nil)
}

/// What the conformer says about `[a, b]` tagged "tag" and `c` tagged 7, and 9.
public func urlSinkDescribeTagged(_ sink: URLContainerSink) -> String {
    sink.describeTagged([sinkURL("a"), sinkURL("b")], urlsNS: "tag", url: sinkURL("c"), urlNS: 7, __urlsBridged: 9)
}

/// What the conformer says about `["ab": [a, b], "c": [c]]`.
public func urlSinkDescribeGroups(_ sink: URLContainerSink) -> String {
    sink.describeGroups(["ab": [sinkURL("a"), sinkURL("b")], "c": [sinkURL("c")]])
}

/// What the conformer says about `[[a], [b, c]]`.
public func urlSinkDescribeRows(_ sink: URLContainerSink) -> String {
    sink.describeRows([[sinkURL("a")], [sinkURL("b"), sinkURL("c")]])
}

/// Sets the conformer's `storedURLs` to `[c, a]` and reads them back.
public func urlSinkStoreAndRead(_ sink: URLContainerSink) -> String {
    sink.storedURLs = [sinkURL("c"), sinkURL("a")]
    return sink.storedURLs.map(\.absoluteString).joined(separator: ",")
}

/// Sets the conformer's `storedGroups` to `["ab": [a, b], "c": [c]]` and reads them back, as
/// `name:url,url` sorted by name and joined by `;`.
public func urlSinkStoreAndReadGroups(_ sink: URLContainerSink) -> String {
    sink.storedGroups = ["ab": [sinkURL("a"), sinkURL("b")], "c": [sinkURL("c")]]
    let groups = sink.storedGroups
    return groups.keys.sorted()
        .map { "\($0):" + groups[$0]!.map(\.lastPathComponent).joined(separator: ",") }
        .joined(separator: ";")
}

/// The conformer's subscript at `[a, b, c]`.
public func urlSinkCount(_ sink: URLContainerSink) -> Int {
    sink[count: [sinkURL("a"), sinkURL("b"), sinkURL("c")]]
}

/// The groups the conformer makes, as `name:url,url` sorted by name and joined by `;`.
public func urlSinkReadGroups(_ sink: URLContainerSink) -> String {
    let groups = sink.makeGroups()
    return groups.keys.sorted()
        .map { "\($0):" + groups[$0]!.map(\.absoluteString).joined(separator: ",") }
        .joined(separator: ";")
}

/// The optional array the conformer makes, joined by `,`, or "nil".
public func urlSinkReadOptionalArray(_ sink: URLContainerSink, present: Bool) -> String {
    sink.makeOptionalArray(present)?.map(\.absoluteString).joined(separator: ",") ?? "nil"
}

/// A conformer C# implements: Swift hands it a dictionary keyed by a Swift class, valued by URLs.
public protocol SwiftKeyNodeURLSink: AnyObject {
    func url(in index: [SwiftKeyNode: URL], of name: String) -> String
}

/// Hands the conformer `swiftKeyNodeURLIndex(names)` and returns its answer for `name`.
public func swiftKeyNodeAskURLSink(_ sink: SwiftKeyNodeURLSink, names: [String], name: String) -> String {
    sink.url(in: swiftKeyNodeURLIndex(names), of: name)
}

// MARK: - A Swift class key beside a bridged value
//
// A URL value sends the whole dictionary through `NSDictionary`, and a Swift class key crosses
// inside it as the object itself. Swift compares the keys of the dictionary it rebuilds by the
// class's own `Hashable` conformance, and a managed copy must too.

/// Nodes named by `names`, each valued by `https://example.com/<position>`.
public func swiftKeyNodeURLIndex(_ names: [String]) -> [SwiftKeyNode: URL] {
    var result: [SwiftKeyNode: URL] = [:]
    for (position, name) in names.enumerated() { result[SwiftKeyNode(name: name)] = URL(string: "https://example.com/\(position)")! }
    return result
}

/// The URL stored under the node named `name`, looked up with a node Swift makes.
public func swiftKeyNodeURLLookUp(_ index: [SwiftKeyNode: URL], name: String) -> String? {
    index[SwiftKeyNode(name: name)]?.absoluteString
}

/// Each of `names` in a dictionary of its own.
public func swiftKeyNodeURLIndexList(_ names: [String]) -> [[SwiftKeyNode: URL]] {
    names.enumerated().map { position, name in [SwiftKeyNode(name: name): URL(string: "https://example.com/\(position)")!] }
}

/// `swiftKeyNodeURLIndex(names)`, or nil when `names` is nil.
public func swiftKeyNodeURLIndexOrNil(_ names: [String]?) -> [SwiftKeyNode: URL]? {
    names.map(swiftKeyNodeURLIndex)
}

/// A dictionary keyed by nodes and valued by URLs that C# sets.
public final class SwiftKeyNodeURLHolder {
    public var index: [SwiftKeyNode: URL] = [:]

    public init() {}

    /// The URL stored under the node named `name`, looked up with a node Swift makes.
    public func url(of name: String) -> String? { index[SwiftKeyNode(name: name)]?.absoluteString }
}

/// `swiftKeyNodeURLIndex(inner)` under the node named `outer`.
public func swiftKeyNodeNestedURLIndex(outer: String, inner: [String]) -> [SwiftKeyNode: [SwiftKeyNode: URL]] {
    [SwiftKeyNode(name: outer): swiftKeyNodeURLIndex(inner)]
}

/// The URL stored under the nodes named `outer` and `inner`, looked up with nodes Swift makes.
public func swiftKeyNodeNestedURLLookUp(_ index: [SwiftKeyNode: [SwiftKeyNode: URL]], outer: String, inner: String) -> String? {
    index[SwiftKeyNode(name: outer)]?[SwiftKeyNode(name: inner)]?.absoluteString
}

/// The strong retain count of a node C# hands over. Only differences between two reads mean anything.
public func swiftKeyNodeRetainCount(_ node: SwiftKeyNode) -> Int {
    Int(_getRetainCount(node))
}

/// Keeps an array of nodes, the +1 control for a retain count.
public final class SwiftKeyNodeArrayStore {
    private var kept: [SwiftKeyNode] = []

    public init() {}

    public func keep(_ nodes: [SwiftKeyNode]) { kept = nodes }
    public func clear() { kept = [] }
}

// MARK: - An NSObject-rooted Swift class key beside a bridged value

/// Rooted nodes named by `names`, each valued by `https://example.com/<position>`.
public func swiftKeyRootedNodeURLIndex(_ names: [String]) -> [SwiftKeyRootedNode: URL] {
    var result: [SwiftKeyRootedNode: URL] = [:]
    for (position, name) in names.enumerated() { result[SwiftKeyRootedNode(name: name)] = URL(string: "https://example.com/\(position)")! }
    return result
}

/// The URL stored under the rooted node named `name`, looked up with a node Swift makes.
public func swiftKeyRootedNodeURLLookUp(_ index: [SwiftKeyRootedNode: URL], name: String) -> String? {
    index[SwiftKeyRootedNode(name: name)]?.absoluteString
}

/// Each of `names` in a dictionary of its own.
public func swiftKeyRootedNodeURLIndexList(_ names: [String]) -> [[SwiftKeyRootedNode: URL]] {
    names.enumerated().map { position, name in [SwiftKeyRootedNode(name: name): URL(string: "https://example.com/\(position)")!] }
}

// MARK: - String keys compared as Swift compares them
//
// Swift's `String` equality is canonical equivalence: "\u{E9}" (precomposed) and "e\u{301}"
// (decomposed) are one key. A managed copy of a String-keyed collection must find either spelling.

/// Each of `keys` in a dictionary of its own, valued by its position.
public func canonicalStringIndexList(_ keys: [String]) -> [[String: Int]] {
    keys.enumerated().map { position, key in [key: position] }
}

/// Each of `keys` in a dictionary of optional strings of its own, valued by its position.
public func canonicalOptionalStringIndexList(_ keys: [String?]) -> [[String?: Int]] {
    keys.enumerated().map { position, key in [key: position] }
}

/// All of `keys` in one set inside an array.
public func canonicalStringSetList(_ keys: [String]) -> [Set<String>] {
    [Set(keys)]
}

/// All of `keys` in one set.
public func canonicalStringSet(_ keys: [String]) -> Set<String> {
    Set(keys)
}

/// `keys` valued by `https://example.com/<position>`, sent through `NSDictionary`.
public func canonicalStringURLIndex(_ keys: [String]) -> [String: URL] {
    var result: [String: URL] = [:]
    for (position, key) in keys.enumerated() { result[key] = URL(string: "https://example.com/\(position)")! }
    return result
}

/// `canonicalStringURLIndex(keys)` under each of `groups`.
public func canonicalStringNestedURLIndex(groups: [String], keys: [String]) -> [String: [String: URL]] {
    Dictionary(uniqueKeysWithValues: groups.map { ($0, canonicalStringURLIndex(keys)) })
}

/// `count` dictionaries of `width` entries each, the keys mixing ASCII and non-ASCII spellings.
public func canonicalStringLargeIndexList(count: Int, width: Int) -> [[String: Int]] {
    (0..<count).map { row in
        var index: [String: Int] = [:]
        for column in 0..<width { index[column.isMultiple(of: 2) ? "key-\(row)-\(column)" : "cl\u{E9}-\(row)-\(column)"] = column }
        return index
    }
}

/// A conformer C# implements: Swift hands it a String-keyed dictionary.
public protocol CanonicalStringSink: AnyObject {
    func value(in index: [String: Int], of key: String) -> Int
}

/// Hands the conformer `keys` valued by position and returns its answer for `key`.
public func canonicalStringAskSink(_ sink: CanonicalStringSink, keys: [String], key: String) -> Int {
    var index: [String: Int] = [:]
    for (position, k) in keys.enumerated() { index[k] = position }
    return sink.value(in: index, of: key)
}

// MARK: - Native-only containers inside a bridged one
//
// A URL key sends the whole dictionary through `NSDictionary`, and Swift bridges every value with
// it: an `[Int]` value is an `NSArray` of `NSNumber`, a `[String: Int]` value an `NSDictionary`. A
// `Set<[URL]>` is an `NSSet` of `NSArray`s. Each must be read and written as the collection it is.

private func countsURL(_ name: String) -> URL { URL(string: "https://example.com/\(name)")! }

/// `[a: [1, 2], b: [3]]`.
public func urlCountGroups() -> [URL: [Int]] {
    [countsURL("a"): [1, 2], countsURL("b"): [3]]
}

/// `groups` as `name:count,count` sorted by name and joined by `;`.
public func urlCountGroupsDescribe(_ groups: [URL: [Int]]) -> String {
    groups.map { "\($0.key.lastPathComponent):" + $0.value.map(String.init).joined(separator: ",") }
        .sorted().joined(separator: ";")
}

/// `[a: [1, nil]]`: Swift stores the nil count as NSNull once it bridges the dictionary.
public func urlOptionalCountGroups() -> [URL: [Int?]] {
    [countsURL("a"): [1, nil]]
}

/// `groups` as `name:count,nil` sorted by name and joined by `;`.
public func urlOptionalCountGroupsDescribe(_ groups: [URL: [Int?]]) -> String {
    groups.map { "\($0.key.lastPathComponent):" + $0.value.map { $0.map(String.init) ?? "nil" }.joined(separator: ",") }
        .sorted().joined(separator: ";")
}

/// `[a: ["x": 1, "y": 2]]`.
public func urlTallies() -> [URL: [String: Int]] {
    [countsURL("a"): ["x": 1, "y": 2]]
}

/// `tallies` as `name:key=count,key=count` sorted and joined by `;`.
public func urlTalliesDescribe(_ tallies: [URL: [String: Int]]) -> String {
    tallies.map { "\($0.key.lastPathComponent):" + $0.value.map { "\($0.key)=\($0.value)" }.sorted().joined(separator: ",") }
        .sorted().joined(separator: ";")
}

/// `{[a, b], [c]}`.
public func urlRowSet() -> Set<[URL]> {
    [[countsURL("a"), countsURL("b")], [countsURL("c")]]
}

/// `rows` as `name,name` per row, sorted and joined by `|`.
public func urlRowSetDescribe(_ rows: Set<[URL]>) -> String {
    rows.map { $0.map(\.lastPathComponent).joined(separator: ",") }.sorted().joined(separator: "|")
}

/// A conformer C# implements: Swift hands it native-only containers inside bridged ones, and asks
/// for them back.
public protocol NestedNativeContainerSink: AnyObject {
    func describeCounts(_ counts: [URL: [Int]]) -> String
    func describeRowSet(_ rows: Set<[URL]>) -> String
    func makeCounts() -> [URL: [Int]]
    func makeRowSet() -> Set<[URL]>
    // A parameter spelled like the thunk's own `selfProto` local.
    func describeSelfNamed(_ urls: [URL], selfProto: Int) -> String
}

/// What the conformer says about `urlCountGroups()`.
public func nestedSinkDescribeCounts(_ sink: NestedNativeContainerSink) -> String {
    sink.describeCounts(urlCountGroups())
}

/// What the conformer says about `urlRowSet()`.
public func nestedSinkDescribeRowSet(_ sink: NestedNativeContainerSink) -> String {
    sink.describeRowSet(urlRowSet())
}

/// The counts the conformer makes, described as `urlCountGroupsDescribe` does.
public func nestedSinkReadCounts(_ sink: NestedNativeContainerSink) -> String {
    urlCountGroupsDescribe(sink.makeCounts())
}

/// The row set the conformer makes, described as `urlRowSetDescribe` does.
public func nestedSinkReadRowSet(_ sink: NestedNativeContainerSink) -> String {
    urlRowSetDescribe(sink.makeRowSet())
}

/// What the conformer says about `[a]` and 41.
public func nestedSinkDescribeSelfNamed(_ sink: NestedNativeContainerSink) -> String {
    sink.describeSelfNamed([countsURL("a")], selfProto: 41)
}

// MARK: - Optional bridged containers C# sets

/// Optional bridged containers C# sets, which Swift reads back.
public final class OptionalBridgedContainerHolder {
    public var urls: [URL]? = nil
    public var urlSet: Set<URL>? = nil
    public var index: [String: URL]? = nil
    public var nodeIndex: [SwiftKeyNode: URL]? = nil

    public init() {}

    /// Every container, described: `urls|set|index|nodes`, each "nil" when absent.
    public func describe() -> String {
        let a = urls.map { $0.map(\.lastPathComponent).joined(separator: ",") } ?? "nil"
        let s = urlSet.map { $0.map(\.lastPathComponent).sorted().joined(separator: ",") } ?? "nil"
        let i = index.map { $0.map { "\($0.key)=\($0.value.lastPathComponent)" }.sorted().joined(separator: ",") } ?? "nil"
        let n = nodeIndex.map { $0.map { "\($0.key.name)=\($0.value.lastPathComponent)" }.sorted().joined(separator: ",") } ?? "nil"
        return "\(a)|\(s)|\(i)|\(n)"
    }
}

// MARK: - Swift values inside a bridged container
//
// A URL key sends the whole dictionary through `NSDictionary`, and Swift bridges every value with
// it: a Swift class value is the object itself, and so is each Swift class in an `[Box]` value.
// (A Swift struct value is boxed in an object only Swift can open, and has no binding.)

/// A Swift class held inside bridged containers.
public final class URLValueBox {
    public let name: String

    public init(name: String) { self.name = name }
}

/// `[a: box "x", b: box "y"]`.
public func urlBoxIndex() -> [URL: URLValueBox] {
    [countsURL("a"): URLValueBox(name: "x"), countsURL("b"): URLValueBox(name: "y")]
}

/// `index` as `url=name` sorted and joined by `;`.
public func urlBoxIndexDescribe(_ index: [URL: URLValueBox]) -> String {
    index.map { "\($0.key.lastPathComponent)=\($0.value.name)" }.sorted().joined(separator: ";")
}

/// `[a: [box "x", box "y"], b: []]`.
public func urlBoxRows() -> [URL: [URLValueBox]] {
    [countsURL("a"): [URLValueBox(name: "x"), URLValueBox(name: "y")], countsURL("b"): []]
}

/// `rows` as `url:name,name` sorted and joined by `;`.
public func urlBoxRowsDescribe(_ rows: [URL: [URLValueBox]]) -> String {
    rows.map { "\($0.key.lastPathComponent):" + $0.value.map(\.name).joined(separator: ",") }.sorted().joined(separator: ";")
}

// MARK: - Sets of collections compared by their contents
//
// Swift's `Hashable` for `[URL]` and `Set<URL>` compares the elements, so a fresh, equal row finds
// the row a set holds.

/// Whether `rows` holds a row equal to `row`, as Swift compares rows.
public func urlRowSetContains(_ rows: Set<[URL]>, _ row: [URL]) -> Bool {
    rows.contains(row)
}

/// `{{a, b}, {c}}`.
public func urlGroupSet() -> Set<Set<URL>> {
    [[countsURL("a"), countsURL("b")], [countsURL("c")]]
}

// MARK: - Dictionaries C# builds in one pass
//
// A dictionary C# hands Swift is read once, so each key keeps its own value even when the
// dictionary does not enumerate the same way twice.

/// `index` as `key=url` sorted and joined by `;`.
public func urlIndexDescribe(_ index: [String: URL]) -> String {
    index.map { "\($0.key)=\($0.value.lastPathComponent)" }.sorted().joined(separator: ";")
}

/// Every dictionary of `rows` as `urlIndexDescribe` does, joined by `|`.
public func urlIndexRowsDescribe(_ rows: [[String: URL]]) -> String {
    rows.map(urlIndexDescribe).joined(separator: "|")
}

/// A dictionary of URLs C# sets, which Swift reads back.
public final class URLIndexHolder {
    public var index: [String: URL] = [:]

    public init() {}

    /// `index`, described as `urlIndexDescribe` does.
    public func describe() -> String { urlIndexDescribe(index) }

    /// Reads `subscript(__val:)` with `__val` as its index, for a setter whose locals must not take
    /// the index's name.
    public subscript(__val position: Int) -> [String: URL] {
        get { position == 0 ? index : [:] }
        set { if position == 0 { index = newValue } }
    }

    public var urls: [URL]? = nil

    /// `urls` under the key "urls", for a setter with a String index spelled like its locals.
    public subscript(__val key: String) -> [URL]? {
        get { key == "urls" ? urls : nil }
        set { if key == "urls" { urls = newValue } }
    }

    /// `urls` as names joined by `,`, or "nil".
    public func describeURLs() -> String { urls.map { $0.map(\.lastPathComponent).joined(separator: ",") } ?? "nil" }
}

/// A conformer C# implements: Swift asks it for a dictionary of URLs.
public protocol URLIndexSource: AnyObject {
    func makeIndex() -> [String: URL]
}

/// The dictionary the conformer makes, described as `urlIndexDescribe` does.
public func urlIndexSourceDescribe(_ source: URLIndexSource) -> String {
    urlIndexDescribe(source.makeIndex())
}

// MARK: - Dates valued by URLs
//
// A `Date` in a dictionary keyed by URLs crosses as the `NSDate` Swift bridges it to, so the
// instant survives both ways.

/// `[a: 1000 s, b: -2.5 s]` after the reference date.
public func urlDateIndex() -> [URL: Date] {
    [countsURL("a"): Date(timeIntervalSinceReferenceDate: 1000), countsURL("b"): Date(timeIntervalSinceReferenceDate: -2.5)]
}

/// `dates` as `url=seconds` (since the reference date) sorted and joined by `;`.
public func urlDateIndexDescribe(_ dates: [URL: Date]) -> String {
    dates.map { "\($0.key.lastPathComponent)=\($0.value.timeIntervalSinceReferenceDate)" }.sorted().joined(separator: ";")
}

/// Dates keyed by URLs, which C# sets and Swift reads back.
public final class URLDateHolder {
    public var dates: [URL: Date] = [:]
    public init() {}
    /// `dates`, described as `urlDateIndexDescribe` does.
    public func describe() -> String { urlDateIndexDescribe(dates) }
}
