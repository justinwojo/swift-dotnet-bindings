// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Frozen Struct with Float Fields (RequiresCdeclForAbiSafety: float)

/// Frozen struct with 4 Double fields — exercises IsSelfTypeCdeclRequired
/// float field detection (WrapperValidation.cs line 877).
/// Real-world pattern: Lottie LottieColor (r/g/b/a: Double).
/// Methods on this struct MUST go through @_cdecl to avoid Mono JIT crash.
@frozen
public struct LottieColorLike {
    public var r: Double
    public var g: Double
    public var b: Double
    public var a: Double

    public init(r: Double, g: Double, b: Double, a: Double) {
        self.r = r
        self.g = g
        self.b = b
        self.a = a
    }

    /// Instance method — must use @_cdecl wrapper.
    public func brightness() -> Double {
        return (r + g + b) / 3.0
    }

    /// Another instance method to verify all methods route through @_cdecl.
    public func withAlpha(_ newAlpha: Double) -> LottieColorLike {
        return LottieColorLike(r: r, g: g, b: b, a: newAlpha)
    }

    /// Returns a descriptive string.
    public func describe() -> String {
        return "RGBA(\(r), \(g), \(b), \(a))"
    }
}

// MARK: - Frozen Struct with Bool Fields (RequiresCdeclForAbiSafety: bool)

/// Frozen struct with Bool fields — exercises IsSelfTypeCdeclRequired
/// bool field detection (WrapperValidation.cs line 881).
/// Bool fields in Swift use i1 which Mono JIT can't pass via CallConvSwift registers.
@frozen
public struct FeatureFlags {
    public var enableLogging: Bool
    public var enableCache: Bool
    public var debugMode: Bool

    public init(enableLogging: Bool, enableCache: Bool, debugMode: Bool) {
        self.enableLogging = enableLogging
        self.enableCache = enableCache
        self.debugMode = debugMode
    }

    /// Instance method — must use @_cdecl wrapper.
    public func activeCount() -> Int32 {
        var count: Int32 = 0
        if enableLogging { count += 1 }
        if enableCache { count += 1 }
        if debugMode { count += 1 }
        return count
    }

    /// Returns combined flag state.
    public func allEnabled() -> Bool {
        return enableLogging && enableCache && debugMode
    }

    public func describe() -> String {
        return "Flags(log:\(enableLogging), cache:\(enableCache), debug:\(debugMode))"
    }
}

// MARK: - Frozen Struct >8 Bytes (RequiresCdeclForAbiSafety: size)

/// Frozen struct with 3 Int fields (24 bytes) — exercises IsSelfTypeCdeclRequired
/// size > 8 bytes detection (WrapperValidation.cs line 888).
/// Structs larger than 8 bytes are passed indirectly and need @_cdecl.
@frozen
public struct LargeConfig {
    public var width: Int
    public var height: Int
    public var depth: Int

    public init(width: Int, height: Int, depth: Int) {
        self.width = width
        self.height = height
        self.depth = depth
    }

    /// Instance method — must use @_cdecl wrapper.
    public func volume() -> Int {
        return width * height * depth
    }

    /// Another instance method.
    public func surfaceArea() -> Int {
        return 2 * (width * height + height * depth + width * depth)
    }

    public func describe() -> String {
        return "\(width)x\(height)x\(depth)"
    }
}

// MARK: - Class with Non-Blittable Constructor (BUG-3 coverage)

/// Class with both a simple constructor and one with Array<String> parameter.
/// Array<T> is a generic container that requires @_cdecl wrapper because it's
/// non-blittable in CallConvSwift. Without the wrapper, Mono JIT crashes.
/// Real-world pattern: Kingfisher ImagePrefetcher(urls:options:completionHandler:).
///
/// BUG-3 fix: When no wrapper strategy is available (e.g., third-party xcframework),
/// the generator now suppresses the constructor instead of emitting a raw
/// CallConvSwift P/Invoke that crashes. In BindingTests (with wrapper support),
/// the @_cdecl wrapper handles it correctly.
public class ArrayInitHolder {
    public var count: Int32
    public var label: String

    /// Simple constructor — always works (blittable params).
    public init(count: Int32) {
        self.count = count
        self.label = "count-only"
    }

    /// Constructor with Array<String> — non-blittable, requires @_cdecl wrapper.
    /// In BindingTests, the wrapper is generated. In third-party libs without
    /// wrapper support, BUG-3 fix suppresses this to prevent Mono JIT crash.
    public init(items: [String]) {
        self.count = Int32(items.count)
        self.label = items.joined(separator: ", ")
    }

    /// Instance method for verification.
    public func describe() -> String {
        return "ArrayInitHolder(count: \(count), label: \(label))"
    }
}

// MARK: - Non-Frozen Struct with Instance Methods (RequiresCdeclForAbiSafety: non-frozen)

/// Non-frozen struct with instance methods — exercises
/// RequiresCdeclForAbiSafety:713 (IsNonFrozenStructInstanceMember).
/// Non-frozen structs always need @_cdecl because their layout is opaque.
public struct FlexibleConfig {
    public var name: String
    public var retryCount: Int32

    public init(name: String, retryCount: Int32) {
        self.name = name
        self.retryCount = retryCount
    }

    /// Instance method — must use @_cdecl because non-frozen.
    public func shouldRetry() -> Bool {
        return retryCount > 0
    }

    /// Another instance method.
    public func describe() -> String {
        return "\(name): retries=\(retryCount)"
    }
}
