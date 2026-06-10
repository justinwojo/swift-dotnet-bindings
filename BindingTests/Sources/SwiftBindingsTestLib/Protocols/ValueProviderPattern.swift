// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Value Provider Pattern
// Tests the pattern where:
// 1. A protocol has a required method/property
// 2. Concrete types conform to the protocol
// 3. A function accepts the protocol type (existential)
// 4. C# concrete types implement the interface for compile-time type safety

/// Protocol requiring a value kind tag and a hasUpdate check.
public protocol ValueProviding {
    /// Tag identifying the kind of value (e.g., "color", "float", "gradient").
    var valueKind: String { get }

    /// Whether the provider has an updated value at the given frame.
    func hasUpdate(frame: Double) -> Bool
}

/// Extension providing a default for hasUpdate — types that are static never update.
extension ValueProviding {
    public func hasUpdate(frame: Double) -> Bool {
        return false
    }
}

// MARK: - Concrete Value Providers

/// Float value provider — provides a single Double value.
public final class FloatProvider: ValueProviding {
    public var floatValue: Double

    public init(floatValue: Double) {
        self.floatValue = floatValue
    }

    public var valueKind: String { "float" }

    /// Override: float providers always have updates (they're dynamic).
    public func hasUpdate(frame: Double) -> Bool {
        return true
    }
}

/// Color value provider — provides RGBA color components.
public final class ColorProvider: ValueProviding {
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

    public var valueKind: String { "color" }
}

/// Gradient value provider — provides an array of color stops with locations.
public final class GradientProvider: ValueProviding {
    public var colors: [Double]
    public var locations: [Double]

    public init(colors: [Double], locations: [Double]) {
        self.colors = colors
        self.locations = locations
    }

    public var valueKind: String { "gradient" }

    /// Number of color stops in the gradient.
    public func stopCount() -> Int32 {
        return Int32(locations.count)
    }
}

// MARK: - SetValueProvider Pattern

/// Animation keypath for identifying animation targets.
public struct AnimKeypath {
    public var keypath: String

    public init(keypath: String) {
        self.keypath = keypath
    }
}

/// Container that accepts value providers via protocol type.
public final class AnimationContainer {
    private var providers: [String: any ValueProviding] = [:]
    private var keypaths: [String] = []

    public init() {}

    /// Set a value provider for a given keypath.
    public func setProvider(_ provider: any ValueProviding, keypath: AnimKeypath) {
        providers[keypath.keypath] = provider
        keypaths.append(keypath.keypath)
    }

    /// Get the value kind for a given keypath, or empty string if not set.
    public func valueKindForKeypath(_ keypath: String) -> String {
        return providers[keypath]?.valueKind ?? ""
    }

    /// Check if a provider at the given keypath has updates at a frame.
    public func hasUpdateForKeypath(_ keypath: String, frame: Double) -> Bool {
        return providers[keypath]?.hasUpdate(frame: frame) ?? false
    }

    /// Number of registered providers.
    public func providerCount() -> Int32 {
        return Int32(providers.count)
    }
}

// MARK: - Free Functions

/// Accept any ValueProviding and return its kind — tests existential parameter passing.
public func getProviderKind(_ provider: any ValueProviding) -> String {
    return provider.valueKind
}

/// Check if a provider has updates at a specific frame.
public func checkProviderUpdate(_ provider: any ValueProviding, frame: Double) -> Bool {
    return provider.hasUpdate(frame: frame)
}
