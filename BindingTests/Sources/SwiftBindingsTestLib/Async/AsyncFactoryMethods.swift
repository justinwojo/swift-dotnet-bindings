// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async Factory Methods (animated bundle / animation asset loading)
// Tests the pattern where a type provides async static factory methods for
// loading from file paths, data, and URL strings.
// This models:
// - AnimationBundle.loadedFrom(filepath:) / AnimationBundle.loadedFrom(data:filename:)
// - AnimationAsset.loadedFrom(url:)

/// Represents loaded animation data — models an async-loaded animation asset.
public final class AnimationAsset {
    public var name: String
    public var frameCount: Int32
    public var duration: Double

    public init(name: String, frameCount: Int32, duration: Double) {
        self.name = name
        self.frameCount = frameCount
        self.duration = duration
    }

    /// Load from a file path (async).
    /// Models: AnimationAsset.loadedFrom(filepath:)
    public static func loadFromFile(path: String) async -> AnimationAsset? {
        // Simulate file I/O delay
        try? await Task.sleep(nanoseconds: 1_000_000)
        guard !path.isEmpty else { return nil }
        let filename = (path as NSString).lastPathComponent
        return AnimationAsset(name: filename, frameCount: 60, duration: 2.0)
    }

    /// Load from raw data bytes (async).
    /// Models: AnimationBundle.loadedFrom(data:filename:)
    public static func loadFromData(_ data: [UInt8], filename: String) async -> AnimationAsset {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return AnimationAsset(
            name: filename,
            frameCount: Int32(data.count),
            duration: Double(data.count) / 30.0
        )
    }

    /// Load from a URL string (async).
    /// Models: AnimationAsset.loadedFrom(url:)
    public static func loadFromUrl(urlString: String) async -> AnimationAsset? {
        try? await Task.sleep(nanoseconds: 1_000_000)
        guard !urlString.isEmpty, urlString.hasPrefix("http") else { return nil }
        let name = (urlString as NSString).lastPathComponent
        return AnimationAsset(name: name, frameCount: 120, duration: 4.0)
    }

    public func describe() -> String {
        return "\(name): \(frameCount) frames, \(duration)s"
    }
}

/// Bundle file containing multiple animations — models an async-loaded animation bundle.
public final class AnimationBundle {
    public var filename: String
    public var animations: [AnimationAsset]

    public init(filename: String, animations: [AnimationAsset]) {
        self.filename = filename
        self.animations = animations
    }

    /// Load a bundle from file path (async).
    /// Models: AnimationBundle.loadedFrom(filepath:)
    public static func loadFromFile(path: String) async -> AnimationBundle? {
        try? await Task.sleep(nanoseconds: 1_000_000)
        guard !path.isEmpty else { return nil }
        let filename = (path as NSString).lastPathComponent
        // Simulate a bundle with 2 animations
        let anim1 = AnimationAsset(name: "intro", frameCount: 30, duration: 1.0)
        let anim2 = AnimationAsset(name: "loop", frameCount: 90, duration: 3.0)
        return AnimationBundle(filename: filename, animations: [anim1, anim2])
    }

    /// Number of animations in the bundle.
    public func animationCount() -> Int32 {
        return Int32(animations.count)
    }

    /// Get animation by index; returns nil if out of bounds.
    public func animation(at index: Int32) -> AnimationAsset? {
        let idx = Int(index)
        guard idx >= 0, idx < animations.count else { return nil }
        return animations[idx]
    }
}

/// Simple actor-isolated cache: shared instance, string-keyed store/lookup, and clear.
public final class AnimationCacheStore {
    public static let shared = AnimationCacheStore()

    private var store: [String: AnimationAsset] = [:]

    public var cacheSize: Int32 {
        get { return Int32(store.count) }
    }

    public init() {}

    public func cacheAnimation(_ animation: AnimationAsset, forKey key: String) {
        store[key] = animation
    }

    public func cachedAnimation(forKey key: String) -> AnimationAsset? {
        return store[key]
    }

    public func clearCache() {
        store.removeAll()
    }
}
