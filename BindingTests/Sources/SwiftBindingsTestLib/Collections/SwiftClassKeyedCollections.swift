// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Collections hashed by a Swift class
//
// A Swift class that is Hashable can be a dictionary key or a set element. Its value is one
// object reference, but Swift hashes and compares it through its own Hashable conformance, not
// by address, and a collection's storage holds a reference on every key. A collection C# builds
// for such a parameter must therefore be one over the class itself: a Swift lookup with an
// object Swift makes on its own finds a present key only if the storage was hashed the class's
// way, and Swift may keep the collection after the call returns, so each key must stay alive
// after C# lets go of it.

/// A Swift class hashed by its name: two nodes of one name are equal.
public final class SwiftKeyNode: Hashable {
    public let name: String

    public init(name: String) { self.name = name }

    public static func == (lhs: SwiftKeyNode, rhs: SwiftKeyNode) -> Bool { lhs.name == rhs.name }
    public func hash(into hasher: inout Hasher) { hasher.combine(name) }
}

/// An open class hashed by its name, whose subclass is Hashable only through it.
open class SwiftKeyOpenNode: Hashable {
    public let name: String

    public init(name: String) { self.name = name }

    public static func == (lhs: SwiftKeyOpenNode, rhs: SwiftKeyOpenNode) -> Bool { lhs.name == rhs.name }
    public func hash(into hasher: inout Hasher) { hasher.combine(name) }
}

/// Hashable through its parent's conformance.
public final class SwiftKeyDerivedNode: SwiftKeyOpenNode {}

/// A Swift class rooted in NSObject: Hashable through NSObject, by `isEqual(_:)` and `hash`.
public final class SwiftKeyRootedNode: NSObject {
    public let name: String

    public init(name: String) {
        self.name = name
        super.init()
    }

    public override func isEqual(_ object: Any?) -> Bool {
        guard let other = object as? SwiftKeyRootedNode else { return false }
        return other.name == name
    }

    public override var hash: Int { name.hashValue }
}

// MARK: C# builds, Swift looks up

/// Whether the set holds a node named `name`, looked up with a node Swift makes.
public func swiftKeySetContains(_ nodes: Set<SwiftKeyNode>, name: String) -> Bool {
    nodes.contains(SwiftKeyNode(name: name))
}

/// The value stored under the node named `name`, or nil when Swift finds none.
public func swiftKeyLookUp(_ index: [SwiftKeyNode: Int], name: String) -> Int? {
    index[SwiftKeyNode(name: name)]
}

/// Whether the set holds a node named `name`, or `nil` when `name` is nil.
public func swiftKeyOptionalSetContains(_ nodes: Set<SwiftKeyNode?>, name: String?) -> Bool {
    nodes.contains(name.map { SwiftKeyNode(name: $0) })
}

/// The value stored under the node named `name`, or under `nil` when `name` is nil.
public func swiftKeyOptionalLookUp(_ index: [SwiftKeyNode?: Int], name: String?) -> Int? {
    index[name.map { SwiftKeyNode(name: $0) }]
}

/// Whether the inner set stored under `group` holds a node named `name`.
public func swiftKeyNestedContains(_ groups: [String: Set<SwiftKeyNode>], group: String, name: String) -> Bool {
    groups[group]?.contains(SwiftKeyNode(name: name)) ?? false
}

/// Whether the set at `index` holds a node named `name`.
public func swiftKeyArrayContains(_ list: [Set<SwiftKeyNode>], index: Int, name: String) -> Bool {
    guard index >= 0, index < list.count else { return false }
    return list[index].contains(SwiftKeyNode(name: name))
}

/// 1 when the optional set holds a node named `name`, 0 when it does not, -1 when it is nil.
public func swiftKeyOptionalContainerContains(_ nodes: Set<SwiftKeyNode>?, name: String) -> Int {
    guard let nodes else { return -1 }
    return nodes.contains(SwiftKeyNode(name: name)) ? 1 : 0
}

/// Whether the set of subclass nodes holds one named `name`.
public func swiftKeyDerivedSetContains(_ nodes: Set<SwiftKeyDerivedNode>, name: String) -> Bool {
    nodes.contains(SwiftKeyDerivedNode(name: name))
}

/// The value stored under the NSObject-rooted node named `name`, or nil.
public func swiftKeyRootedLookUp(_ index: [SwiftKeyRootedNode: Int], name: String) -> Int? {
    index[SwiftKeyRootedNode(name: name)]
}

// MARK: Swift builds, C# looks up

/// A set of nodes with the given names.
public func swiftKeyMakeSet(_ names: [String]) -> Set<SwiftKeyNode> {
    Set(names.map { SwiftKeyNode(name: $0) })
}

/// A dictionary whose value for each node is its position in `names`.
public func swiftKeyMakeIndex(_ names: [String]) -> [SwiftKeyNode: Int] {
    var result: [SwiftKeyNode: Int] = [:]
    for (position, name) in names.enumerated() { result[SwiftKeyNode(name: name)] = position }
    return result
}

/// A set of optional nodes, `nil` for a nil name.
public func swiftKeyMakeOptionalSet(_ names: [String?]) -> Set<SwiftKeyNode?> {
    Set(names.map { $0.map { SwiftKeyNode(name: $0) } })
}

/// A set of subclass nodes with the given names.
public func swiftKeyMakeDerivedSet(_ names: [String]) -> Set<SwiftKeyDerivedNode> {
    Set(names.map { SwiftKeyDerivedNode(name: $0) })
}

/// A dictionary of NSObject-rooted nodes, each valued by its position in `names`.
public func swiftKeyMakeRootedIndex(_ names: [String]) -> [SwiftKeyRootedNode: Int] {
    var result: [SwiftKeyRootedNode: Int] = [:]
    for (position, name) in names.enumerated() { result[SwiftKeyRootedNode(name: name)] = position }
    return result
}

// MARK: Closures and reverse dispatch

/// Hands C# a set Swift built, and returns what C# made of it.
public func swiftKeyCallWithSet(_ names: [String], _ body: (Set<SwiftKeyNode>) -> Int) -> Int {
    body(swiftKeyMakeSet(names))
}

/// Hands C# a dictionary Swift built, and returns what C# made of it.
public func swiftKeyCallWithIndex(_ names: [String], _ body: ([SwiftKeyNode: Int]) -> Int) -> Int {
    body(swiftKeyMakeIndex(names))
}

/// A conformer C# implements: Swift asks it for a dictionary and hands it a set.
public protocol SwiftKeyNodeSource: AnyObject {
    func index() -> [SwiftKeyNode: Int]
    func position(in nodes: Set<SwiftKeyNode>, of name: String) -> Int
}

/// Looks `name` up in the dictionary the conformer returns.
public func swiftKeyLookUpFromSource(_ source: SwiftKeyNodeSource, name: String) -> Int? {
    source.index()[SwiftKeyNode(name: name)]
}

/// Hands the conformer a set Swift built and returns its answer for `name`.
public func swiftKeyAskSource(_ source: SwiftKeyNodeSource, names: [String], name: String) -> Int {
    source.position(in: swiftKeyMakeSet(names), of: name)
}

// MARK: Ownership

/// Keeps the collections it is handed, and reads them back later.
public final class SwiftKeyStore {
    private var keptSet: Set<SwiftKeyNode> = []
    private var keptIndex: [SwiftKeyNode: Int] = [:]
    private var keptOptional: Set<SwiftKeyNode?> = []
    private var keptOptionalIndex: [SwiftKeyNode?: Int] = [:]

    public init() {}

    public func keep(_ nodes: Set<SwiftKeyNode>) { keptSet = nodes }
    public func keepIndex(_ index: [SwiftKeyNode: Int]) { keptIndex = index }
    public func keepOptional(_ nodes: Set<SwiftKeyNode?>) { keptOptional = nodes }
    public func keepOptionalIndex(_ index: [SwiftKeyNode?: Int]) { keptOptionalIndex = index }

    /// The kept set, as a property C# can set.
    public var nodes: Set<SwiftKeyNode> {
        get { keptSet }
        set { keptSet = newValue }
    }

    /// The kept set's names, sorted: reads every node, so one freed after C# let go of it shows up here.
    public func keptNames() -> String { keptSet.map { $0.name }.sorted().joined(separator: ",") }

    /// The kept dictionary as `name=value` pairs, sorted by name.
    public func keptIndexEntries() -> String {
        keptIndex.map { "\($0.key.name)=\($0.value)" }.sorted().joined(separator: ",")
    }

    /// The kept optional set's names, `nil` for its nil element, sorted.
    public func keptOptionalNames() -> String {
        keptOptional.map { $0?.name ?? "nil" }.sorted().joined(separator: ",")
    }

    /// The kept optional dictionary as `name=value` pairs, `nil` for its nil key, sorted.
    public func keptOptionalIndexEntries() -> String {
        keptOptionalIndex.map { "\($0.key?.name ?? "nil")=\($0.value)" }.sorted().joined(separator: ",")
    }

    /// Whether the kept set holds this very object (identity, not equality).
    public func keepsObject(_ node: SwiftKeyNode) -> Bool { keptSet.contains { $0 === node } }

    /// Whether the kept set holds a node named `name`, looked up with a node Swift makes.
    public func contains(_ name: String) -> Bool { keptSet.contains(SwiftKeyNode(name: name)) }

    public func clear() {
        keptSet = []
        keptIndex = [:]
        keptOptional = []
        keptOptionalIndex = [:]
    }
}

/// Keeps an array of nodes: the control for the retain counts, an array element that already
/// crosses as the class.
public final class SwiftKeyArrayStore {
    private var kept: [SwiftKeyNode] = []

    public init() {}

    public func keep(_ nodes: [SwiftKeyNode]) { kept = nodes }
    public func clear() { kept = [] }
}

/// The strong retain count of a node C# hands over. Only differences between two reads mean
/// anything: the call itself may hold references of its own.
public func swiftKeyRetainCount(_ node: SwiftKeyNode) -> Int {
    Int(_getRetainCount(node))
}

// MARK: - A container whose sibling argument has no projection
//
// A dictionary keyed by an Objective-C object whose values are a Swift `Result`: a `Result` C#
// builds has no Swift value to marshal into the dictionary, so a member that takes one is not
// bound. One that returns one is.

public enum SwiftKeyResultError: Error {
    case failed
}

/// The successes among the values, summed: the only member here that takes such a dictionary.
public func swiftKeyResultValueSum(_ results: [NSUUID: Result<Int, SwiftKeyResultError>]) -> Int {
    results.values.reduce(0) { sum, result in
        if case .success(let value) = result { return sum + value }
        return sum
    }
}

/// A dictionary keyed by the given UUIDs: the first a success of 7, every other a failure.
public func swiftKeyMakeResults(_ uuids: [String]) -> [NSUUID: Result<Int, SwiftKeyResultError>] {
    var result: [NSUUID: Result<Int, SwiftKeyResultError>] = [:]
    for (position, text) in uuids.enumerated() {
        if let key = NSUUID(uuidString: text) {
            result[key] = position == 0 ? .success(7) : .failure(.failed)
        }
    }
    return result
}
