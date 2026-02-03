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

// MARK: - Non-Frozen Class

/// Non-frozen class that could gain new methods or properties in future versions.
public class EvolvingService {
    public var name: String
    public var isEnabled: Bool

    public init(name: String, isEnabled: Bool) {
        self.name = name
        self.isEnabled = isEnabled
    }

    /// Returns a human-readable description of the service.
    public func describe() -> String {
        return "Service(\(name), enabled=\(isEnabled))"
    }

    /// Returns the service status as a string.
    public func status() -> String {
        return isEnabled ? "active" : "inactive"
    }
}

// MARK: - Non-Frozen Enum

/// Non-frozen enum that could gain new cases in future versions.
public enum EvolvingStatus {
    case active
    case inactive
    case maintenance

    /// Returns a human-readable description.
    public func description() -> String {
        switch self {
        case .active:
            return "Active"
        case .inactive:
            return "Inactive"
        case .maintenance:
            return "Under Maintenance"
        }
    }
}

// MARK: - Non-Frozen Struct with Optional Field

/// Non-frozen struct with an optional field, simulating evolving request types.
public struct EvolvingRequest {
    public var endpoint: String
    public var retryCount: Int32?

    public init(endpoint: String, retryCount: Int32? = nil) {
        self.endpoint = endpoint
        self.retryCount = retryCount
    }
}

// MARK: - Free Functions for Non-Frozen Types

/// Returns a description of the given service.
public func describeService(_ service: EvolvingService) -> String {
    return service.describe()
}

/// Returns true if the service is active.
public func isActive(_ service: EvolvingService) -> Bool {
    return service.isEnabled
}

/// Returns a new request with the given retry count.
public func withRetry(_ request: EvolvingRequest, retryCount: Int32) -> EvolvingRequest {
    return EvolvingRequest(endpoint: request.endpoint, retryCount: retryCount)
}

/// Processes a request and returns a status string.
public func processRequest(_ request: EvolvingRequest) -> String {
    let retries = request.retryCount.map { String($0) } ?? "none"
    return "Processing \(request.endpoint) (retries: \(retries))"
}
