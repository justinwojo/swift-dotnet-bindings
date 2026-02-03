// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Library Evolution / ABI Stability Tests

// These types are intentionally NON-frozen to test the binding generator's handling
// of opaque (resilient) types. In a real library evolution scenario:
// - Non-frozen structs can add new stored properties in future versions
// - The compiler must use accessor functions rather than direct field access
// - The binding generator must emit ClassWithOpaquePayload (SafeHandle) rather than
//   C# structs with matching layout

/// Non-frozen configuration type (v1).
/// Future versions could add fields without breaking ABI.
public struct EvolvingConfig {
    public var featureA: Bool
    public var timeout: Int32

    public init(featureA: Bool, timeout: Int32) {
        self.featureA = featureA
        self.timeout = timeout
    }

    public func describe() -> String {
        return "Config(featureA=\(featureA), timeout=\(timeout))"
    }
}

/// Non-frozen configuration type (v2 simulation).
/// Demonstrates what an evolved type might look like with additional fields.
public struct EvolvingConfigV2 {
    public var featureA: Bool
    public var featureB: Bool
    public var timeout: Int32

    public init(featureA: Bool, featureB: Bool, timeout: Int32) {
        self.featureA = featureA
        self.featureB = featureB
        self.timeout = timeout
    }

    public func describe() -> String {
        return "ConfigV2(featureA=\(featureA), featureB=\(featureB), timeout=\(timeout))"
    }
}

// MARK: - Accessor Functions

/// Creates a default EvolvingConfig.
public func makeDefaultConfig() -> EvolvingConfig {
    return EvolvingConfig(featureA: true, timeout: 30)
}

/// Reads a field from EvolvingConfig via a function (simulating accessor pattern).
public func getConfigTimeout(_ config: EvolvingConfig) -> Int32 {
    return config.timeout
}

/// Creates a modified config (non-frozen types are passed/returned by reference).
public func withTimeout(_ config: EvolvingConfig, timeout: Int32) -> EvolvingConfig {
    return EvolvingConfig(featureA: config.featureA, timeout: timeout)
}
