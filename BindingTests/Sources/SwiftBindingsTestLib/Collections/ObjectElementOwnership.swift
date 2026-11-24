// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Ownership of the object references a collection holds, beyond its plain elements
//
// An optional element or value of an object type (`[NSUUID?]`, `[String: NSUUID?]`) is one object
// reference, or nil: Swift's storage holds a reference on every non-nil element exactly as it does for
// `[NSUUID]`, so a collection C# builds must be the optional type's own. A bound generic Objective-C
// class (`NSCache<NSString, NSUUID>`) is one object reference too. A collection whose values are
// themselves collections (`[String: [NSUUID]]`, `[[NSUUID]?]`) is built from inner collections C#
// creates only to have Swift copy them; each must give its references back when copied, not when
// the garbage collector gets to it.

/// Keeps the collections of optional and generic object references it is handed.
public final class ObjectOwnershipStore {
    private var list: [NSUUID?] = []
    private var values: [String: NSUUID?] = [:]
    private var tracked: [ObjectElementTracked?] = []
    private var refs: [ObjectElementRef?] = []
    private var caches: [NSCache<NSString, NSUUID>] = []
    private var groupsStorage: [String: [NSUUID]] = [:]
    private var nestedStorage: [[NSUUID]?] = []

    public init() {}

    public func keep(_ list: [NSUUID?]) { self.list = list }
    public func keepValues(_ values: [String: NSUUID?]) { self.values = values }
    public func keepTracked(_ list: [ObjectElementTracked?]) { tracked = list }
    public func keepRefs(_ list: [ObjectElementRef?]) { refs = list }
    public func keepCaches(_ list: [NSCache<NSString, NSUUID>]) { caches = list }
    public func keepGroups(_ groups: [String: [NSUUID]]) { groupsStorage = groups }

    /// The kept optional list, as a property C# can set and read.
    public var items: [NSUUID?] {
        get { list }
        set { list = newValue }
    }

    /// Groups of UUIDs, as a property C# can set.
    public var groups: [String: [NSUUID]] {
        get { groupsStorage }
        set { groupsStorage = newValue }
    }

    /// Optional lists of UUIDs, as a property C# can set.
    public var nested: [[NSUUID]?] {
        get { nestedStorage }
        set { nestedStorage = newValue }
    }

    /// The kept list in order, `nil` for a nil element: reads every element, so one whose object was
    /// freed after C# let go of it shows up here.
    public func keptStrings() -> String { list.map { $0?.uuidString ?? "nil" }.joined(separator: ",") }

    /// The kept dictionary as `key=uuid` pairs sorted by key, `nil` for a nil value.
    public func keptValueStrings() -> String {
        values.sorted { $0.key < $1.key }.map { "\($0.key)=\($0.value?.uuidString ?? "nil")" }.joined(separator: ",")
    }

    public func keptTrackedTags() -> String { tracked.map { $0.map { String($0.tag) } ?? "nil" }.joined(separator: ",") }
    public func keptRefTags() -> String { refs.map { $0.map { String($0.tag) } ?? "nil" }.joined(separator: ",") }

    /// The UUID each kept cache holds under `key`, `nil` where it holds none.
    public func keptCacheStrings(_ key: String) -> String {
        caches.map { $0.object(forKey: key as NSString)?.uuidString ?? "nil" }.joined(separator: ",")
    }

    /// The kept groups as `key=uuid;uuid` sorted by key.
    public func keptGroupStrings() -> String {
        groupsStorage.sorted { $0.key < $1.key }
            .map { "\($0.key)=\($0.value.map { $0.uuidString }.joined(separator: ";"))" }
            .joined(separator: ",")
    }

    /// The kept optional lists, `nil` for a nil list, `|` between lists.
    public func keptNestedStrings() -> String {
        nestedStorage.map { $0.map { $0.map { $0.uuidString }.joined(separator: ";") } ?? "nil" }.joined(separator: "|")
    }

    public func clear() {
        list = []
        values = [:]
        tracked = []
        refs = []
        caches = []
        groupsStorage = [:]
        nestedStorage = []
    }
}

/// A list of optional UUIDs Swift builds; `-` makes a nil element.
public func objectOwnershipMakeList(_ uuids: [String]) -> [NSUUID?] {
    return uuids.map { $0 == "-" ? nil : NSUUID(uuidString: $0) }
}

/// A dictionary of optional UUIDs Swift builds, keyed `k0`, `k1`, …; `-` makes a nil value.
public func objectOwnershipMakeValues(_ uuids: [String]) -> [String: NSUUID?] {
    var result: [String: NSUUID?] = [:]
    for (index, text) in uuids.enumerated() {
        result["k\(index)"] = .some(text == "-" ? nil : NSUUID(uuidString: text))
    }
    return result
}

/// Hands C# a list of optional UUIDs Swift built, and returns what C# made of it.
public func objectOwnershipCallWithList(_ uuids: [String], _ body: ([NSUUID?]) -> String) -> String {
    return body(objectOwnershipMakeList(uuids))
}

/// Hands C# a set of UUIDs Swift built, and returns what C# made of it.
public func objectOwnershipCallWithSet(_ uuids: [String], _ body: (Set<NSUUID>) -> Int) -> Int {
    return body(Set(uuids.compactMap { NSUUID(uuidString: $0) }))
}

/// A source of UUID groups C# implements: Swift reads the dictionary C# builds.
public protocol ObjectGroupSource: AnyObject {
    var groups: [String: [NSUUID]] { get }
}

/// Swift reads the groups `source` returns, as `key=uuid;uuid` sorted by key.
public func objectOwnershipReadGroups(_ source: ObjectGroupSource) -> String {
    return source.groups.sorted { $0.key < $1.key }
        .map { "\($0.key)=\($0.value.map { $0.uuidString }.joined(separator: ";"))" }
        .joined(separator: ",")
}
