// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Cache Pattern (Nuke ImagePipeline.Cache)
// Tests the pattern where a class exposes a nested class with CRUD-like methods:
// - cachedItem(for:) returning Optional
// - storeItem(_:for:) storing a value
// - removeItem(for:) removing a value
// - containsItem(for:) returning Bool
// - makeKey() returning a struct
// This models Nuke's ImagePipeline.Cache with its query/store/remove methods.

/// Item stored in the cache — models Nuke's ImageContainer.
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

/// Pipeline with a nested Cache class — models Nuke's ImagePipeline.
public final class DataPipeline {
    public static let shared = DataPipeline(label: "shared")

    public var label: String

    public init(label: String) {
        self.label = label
    }

    /// Cache nested class — models Nuke's ImagePipeline.Cache.
    public final class Cache {
        private var items: [String: CachedEntry] = [:]

        public init() {}

        /// Retrieve a cached item by key; returns nil if not found.
        /// Models: ImagePipeline.Cache.cachedImage(for:)
        public func cachedItem(for key: String) -> CachedEntry? {
            return items[key]
        }

        /// Store an item in the cache.
        /// Models: ImagePipeline.Cache.storeCachedImage(_:for:)
        public func storeItem(_ item: CachedEntry, for key: String) {
            items[key] = item
        }

        /// Remove an item from the cache.
        /// Models: ImagePipeline.Cache.removeCachedImage(for:)
        public func removeItem(for key: String) {
            items.removeValue(forKey: key)
        }

        /// Check if the cache contains an item.
        /// Models: ImagePipeline.Cache.containsCachedImage(for:)
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
        /// Models: ImagePipeline.Cache.makeImageCacheKey(for:)
        public func makeKey(for url: String, variant: String) -> String {
            return "\(url)#\(variant)"
        }
    }

    private let _cache = Cache()

    /// Access the pipeline's cache.
    /// Models: ImagePipeline.cache property.
    public var cache: Cache {
        return _cache
    }
}
