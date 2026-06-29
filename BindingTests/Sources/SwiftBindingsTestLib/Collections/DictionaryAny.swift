// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - [String: Any] Dictionary Pattern

/// Test type for [String: Any] dictionary projection.
/// Real-world pattern: HTTP client parameters, analytics event properties.
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

// MARK: - Nested [String: [String: Any]] property — invariant value-slot getter

/// Test type exposing a NESTED dictionary as a stored PROPERTY: `[String: [String: Any]]`.
/// The outer dictionary's value slot is an inner `[String: Any]` bag, which the C# projection surfaces
/// as a concrete `Dictionary<…>`; the outer `IReadOnlyDictionary` value slot is invariant, so the getter
/// accessor must cast the inner value to its declared `IReadOnlyDictionary` interface or the generated C#
/// getter fails to compile. Real-world pattern: sectioned config / grouped analytics payloads.
public class NestedConfigStore {
    /// Sections keyed by name, each a `[String: Any]` bag — read back through the property getter.
    public var sections: [String: [String: Any]]

    public init() {
        self.sections = [:]
    }

    /// Replaces all sections.
    public func setSections(_ newSections: [String: [String: Any]]) {
        self.sections = newSections
    }

    /// Number of top-level sections.
    public func sectionCount() -> Int32 { Int32(sections.count) }

    /// Number of entries within a named section, or -1 if the section is absent.
    public func entryCount(_ section: String) -> Int32 {
        guard let inner = sections[section] else { return -1 }
        return Int32(inner.count)
    }

    /// Reads a String entry from a section, or empty string if absent / wrong type.
    public func getString(_ section: String, _ key: String) -> String {
        return (sections[section]?[key] as? String) ?? ""
    }

    /// Reads an Int entry from a section, or -1 if absent / wrong type.
    public func getInt(_ section: String, _ key: String) -> Int {
        return (sections[section]?[key] as? Int) ?? -1
    }
}
