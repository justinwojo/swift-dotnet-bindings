// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - String-Keyed Subscript (KeychainAccess pattern)

/// Class with string-keyed subscript returning optional string.
public class KeyValueStore {
    private var storage: [String: String] = [:]

    public init() {}

    public subscript(key: String) -> String? {
        get { storage[key] }
        set { storage[key] = newValue }
    }

    public func count() -> Int32 { Int32(storage.count) }
    public func removeAll() { storage.removeAll() }
    public func allKeys() -> [String] { Array(storage.keys.sorted()) }
    public func allValues() -> [String] { Array(storage.keys.sorted().compactMap { storage[$0] }) }
}

// MARK: - Int-Keyed Subscript (Blittable Comparison)

/// Class with int-keyed subscript returning int.
public class IndexedStore {
    private var items: [Int32]

    public init(capacity: Int32) {
        items = Array(repeating: 0, count: Int(capacity))
    }

    public subscript(index: Int32) -> Int32 {
        get { items[Int(index)] }
        set { items[Int(index)] = newValue }
    }

    public func count() -> Int32 { Int32(items.count) }
}
