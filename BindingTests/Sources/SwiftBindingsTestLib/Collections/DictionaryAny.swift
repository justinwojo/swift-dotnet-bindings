// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - [String: Any] Dictionary Pattern

/// Test type for [String: Any] dictionary projection.
/// Real-world pattern: Alamofire HTTP parameters, Mixpanel event properties.
public class ConfigStore {
    private var config: [String: Any]

    public init() {
        self.config = [:]
    }

    /// Returns the count of entries in the config dictionary.
    public func count() -> Int32 { Int32(config.count) }

    /// Sets a config dictionary, replacing all existing values.
    public func setConfig(_ newConfig: [String: Any]) {
        self.config = newConfig
    }

    /// Returns the current config dictionary.
    public func getConfig() -> [String: Any] {
        return config
    }

    /// Gets a string value from the config, or empty string if not found or wrong type.
    public func getString(_ key: String) -> String {
        return (config[key] as? String) ?? ""
    }

    /// Gets an integer value from the config, or -1 if not found or wrong type.
    public func getInt(_ key: String) -> Int {
        return (config[key] as? Int) ?? -1
    }

    /// Gets a double value from the config, or -1.0 if not found or wrong type.
    public func getDouble(_ key: String) -> Double {
        return (config[key] as? Double) ?? -1.0
    }

    /// Gets a bool value from the config, or false if not found or wrong type.
    public func getBool(_ key: String) -> Bool {
        return (config[key] as? Bool) ?? false
    }
}

// MARK: - Free function accepting [String: Any]

/// Free function that counts entries in a [String: Any] dictionary.
public func countAnyDictEntries(_ dict: [String: Any]) -> Int32 {
    return Int32(dict.count)
}
