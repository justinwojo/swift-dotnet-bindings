// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Cache Pattern
// Tests the pattern where a class exposes a nested class with CRUD-like methods:
// - cachedItem(for:) returning Optional
// - storeItem(_:for:) storing a value
// - removeItem(for:) removing a value
// - containsItem(for:) returning Bool
// - makeKey() returning a struct

/// Item stored in the cache — holds the payload data, byte size, and a timestamp.
public final class CachedEntry {
    public var data: String
    public var size: Int32
    public var timestamp: Double

    public init(data: String, size: Int32, timestamp: Double) {
        self.data = data
        self.size = size
        self.timestamp = timestamp
    }

    public func describe() -> String {
        return "\(data) (\(size) bytes)"
    }
}

/// Pipeline class with a nested Cache class.
public final class DataPipeline {
    public static let shared = DataPipeline(label: "shared")

    public var label: String

    public init(label: String) {
        self.label = label
    }

    /// Nested cache class with query/store/remove/contains operations.
    public final class Cache {
        private var items: [String: CachedEntry] = [:]

        public init() {}

        /// Retrieve a cached item by key; returns nil if not found.
        public func cachedItem(for key: String) -> CachedEntry? {
            return items[key]
        }

        /// Store an item in the cache.
        public func storeItem(_ item: CachedEntry, for key: String) {
            items[key] = item
        }

        /// Remove an item from the cache.
        public func removeItem(for key: String) {
            items.removeValue(forKey: key)
        }

        /// Check if the cache contains an item.
        public func containsItem(for key: String) -> Bool {
            return items[key] != nil
        }

        /// Number of cached items.
        public func itemCount() -> Int32 {
            return Int32(items.count)
        }

        /// Remove all cached items.
        public func removeAll() {
            items.removeAll()
        }

        /// Generate a cache key from components.
        public func makeKey(for url: String, variant: String) -> String {
            return "\(url)#\(variant)"
        }
    }

    private let _cache = Cache()

    /// Access the pipeline's cache.
    public var cache: Cache {
        return _cache
    }
}
