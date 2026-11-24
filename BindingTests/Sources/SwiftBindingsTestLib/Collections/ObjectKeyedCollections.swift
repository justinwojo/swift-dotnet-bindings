// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Collections keyed by a type that crosses as one object reference
//
// An Objective-C class key (NSUUID) and a string-backed Objective-C key type
// (NSAttributedString.Key, an NS_TYPED_ENUM struct wrapping its NSString) each cross the
// boundary as one object pointer. A dictionary or set C# builds for such a parameter must hash
// every key the way Swift does: NSUUID by its UUID, NSAttributedString.Key by its string. A
// count alone cannot tell; only a Swift lookup of a key that is present can.

/// Returns the value stored under the attribute named `key`, or nil when Swift finds none.
public func objectKeyLookUpAttribute(_ attributes: [NSAttributedString.Key: Int], key: String) -> Int? {
    return attributes[NSAttributedString.Key(rawValue: key)]
}

/// Returns the value stored under the UUID `uuid`, or nil when Swift finds none.
public func objectKeyLookUpUUID(_ counts: [NSUUID: Int], uuid: String) -> Int? {
    guard let key = NSUUID(uuidString: uuid) else { return nil }
    return counts[key]
}

/// Whether the set holds the UUID `uuid`.
public func objectKeySetContainsUUID(_ uuids: Set<NSUUID>, uuid: String) -> Bool {
    guard let key = NSUUID(uuidString: uuid) else { return false }
    return uuids.contains(key)
}

/// Whether the set holds the attribute named `key`.
public func objectKeySetContainsAttribute(_ keys: Set<NSAttributedString.Key>, key: String) -> Bool {
    return keys.contains(NSAttributedString.Key(rawValue: key))
}

/// Looks `key` up in the inner dictionary stored under `group`.
public func objectKeyNestedLookUp(_ groups: [String: [NSAttributedString.Key: Int]], group: String, key: String) -> Int? {
    return groups[group]?[NSAttributedString.Key(rawValue: key)]
}

/// Looks `uuid` up in the dictionary at `index`.
public func objectKeyArrayLookUp(_ list: [[NSUUID: Int]], index: Int, uuid: String) -> Int? {
    guard index >= 0, index < list.count, let key = NSUUID(uuidString: uuid) else { return nil }
    return list[index][key]
}

/// Looks `key` up in an optional dictionary; -1 when the dictionary itself is nil.
public func objectKeyOptionalLookUp(_ attributes: [NSAttributedString.Key: Int]?, key: String) -> Int? {
    guard let attributes else { return -1 }
    return attributes[NSAttributedString.Key(rawValue: key)]
}

/// Builds an attribute dictionary whose value for each name is its position in `names`.
public func objectKeyMakeAttributes(_ names: [String]) -> [NSAttributedString.Key: Int] {
    var result: [NSAttributedString.Key: Int] = [:]
    for (index, name) in names.enumerated() {
        result[NSAttributedString.Key(rawValue: name)] = index
    }
    return result
}

/// Builds a UUID dictionary whose value for each UUID is its position in `uuids`.
public func objectKeyMakeUUIDCounts(_ uuids: [String]) -> [NSUUID: Int] {
    var result: [NSUUID: Int] = [:]
    for (index, text) in uuids.enumerated() {
        if let key = NSUUID(uuidString: text) { result[key] = index }
    }
    return result
}

/// Builds a set of the given UUIDs.
public func objectKeyMakeUUIDSet(_ uuids: [String]) -> Set<NSUUID> {
    return Set(uuids.compactMap { NSUUID(uuidString: $0) })
}

/// Hands C# a dictionary Swift built, and returns what C# made of it.
public func objectKeyCallWithAttributes(_ names: [String], _ body: ([NSAttributedString.Key: Int]) -> Int) -> Int {
    return body(objectKeyMakeAttributes(names))
}

/// A conformer C# implements: Swift asks it for a dictionary and hands it one.
public protocol ObjectKeyAttributeSource: AnyObject {
    func attributes() -> [NSAttributedString.Key: Int]
    func value(in attributes: [NSAttributedString.Key: Int], named name: String) -> Int
}

/// Looks `key` up in the dictionary the conformer returns.
public func objectKeyLookUpFromSource(_ source: ObjectKeyAttributeSource, key: String) -> Int? {
    return source.attributes()[NSAttributedString.Key(rawValue: key)]
}

/// Hands the conformer a dictionary Swift built and returns its answer for `name`.
public func objectKeyAskSource(_ source: ObjectKeyAttributeSource, names: [String], name: String) -> Int {
    return source.value(in: objectKeyMakeAttributes(names), named: name)
}

// MARK: - Key ownership
//
// A dictionary C# builds is Swift's once it crosses: Swift may keep it after the call returns,
// so its storage must hold its own reference on every key, as a Swift-built one does.

/// Keeps the last dictionary it was handed.
public final class ObjectKeyStore {
    private var kept: [NSAttributedString.Key: Int] = [:]
    private var keptUUIDs: Set<NSUUID> = []

    public init() {}

    public func keep(_ attributes: [NSAttributedString.Key: Int]) { kept = attributes }

    public func keepUUIDs(_ uuids: Set<NSUUID>) { keptUUIDs = uuids }

    /// The value kept under the attribute named `key`, or nil.
    public func lookUp(_ key: String) -> Int? { kept[NSAttributedString.Key(rawValue: key)] }

    /// The kept attribute names, sorted: reads every key's string, so a key whose storage was
    /// freed after C# let go of it shows up here.
    public func keptNames() -> String { kept.keys.map { $0.rawValue }.sorted().joined(separator: ",") }

    /// The kept UUIDs, sorted.
    public func keptUUIDStrings() -> String { keptUUIDs.map { $0.uuidString }.sorted().joined(separator: ",") }

    public func clear() {
        kept = [:]
        keptUUIDs = []
    }
}

/// The retain count of an object C# hands over. Only differences between two reads mean
/// anything: the call itself may hold references of its own.
public func objectKeyRetainCount(_ object: NSUUID) -> Int {
    return CFGetRetainCount(object)
}
