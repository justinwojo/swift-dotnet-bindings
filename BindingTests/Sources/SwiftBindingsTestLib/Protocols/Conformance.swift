// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Types Conforming to Protocols

/// A struct conforming to Describable and TestIdentifiable.
public struct SimpleItem: Describable, TestIdentifiable {
    public let id: String
    public let label: String

    public init(id: String, label: String) {
        self.id = id
        self.label = label
    }

    public var description: String {
        return "[\(id)] \(label)"
    }

    public func describe() -> String {
        return description
    }
}

/// A struct conforming to HasValue (get+set protocol).
public struct MutableItem: HasValue {
    public var value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func getValue() -> Int32 {
        return value
    }

    public mutating func setValue(_ newValue: Int32) {
        value = newValue
    }
}

/// A struct conforming to Displayable (protocol with inheritance).
public struct DisplayItem: Displayable {
    public let text: String

    public init(text: String) {
        self.text = text
    }

    public var description: String {
        return text
    }

    public func describe() -> String {
        return "Describe: \(text)"
    }

    public func display() -> String {
        return "Display: \(text)"
    }
}

/// A struct conforming to Nameable & Ageable for composition tests.
public struct Person: Nameable, Ageable {
    public let name: String
    public let age: Int32

    public init(name: String, age: Int32) {
        self.name = name
        self.age = age
    }
}

// MARK: - N3: Class Conforming to Multiple Custom Protocols

/// Class conforming to three separate protocols simultaneously.
/// Tests multiple witness table registrations and IExistentialBoxable boxing paths.
public class MultiProtocolEntity: Describable, TestIdentifiable, Nameable {
    public let id: String
    public let name: String

    public init(id: String, name: String) {
        self.id = id
        self.name = name
    }

    public var description: String { "[\(id)] \(name)" }
    public func describe() -> String { description }
}

// MARK: - N1: Configurable Conformance (uses protocol default implementation)

/// Uses the default configure() from Configurable extension.
public struct ConfigurableItem: Configurable {
    public let configName: String

    public init(configName: String) {
        self.configName = configName
    }
    // Uses default configure() — no override
}

/// Overrides the default configure() implementation.
public struct CustomConfigItem: Configurable {
    public let configName: String

    public init(configName: String) {
        self.configName = configName
    }

    public func configure() -> String {
        return "Custom: \(configName)"
    }
}

// MARK: - N4: Marker Protocol Conformance

/// Type conforming to the empty Taggable marker protocol.
public struct TaggedItem: Taggable {
    public let tag: String

    public init(tag: String) {
        self.tag = tag
    }
}

// MARK: - AB2: 3-Level Protocol Chain Conformance

/// Concrete type implementing the full 3-level protocol chain.
public struct LengthRule: StrictInputValidation {
    public let ruleName: String
    public let strictLevel: Int32
    public let maxLength: Int32

    public init(ruleName: String, strictLevel: Int32, maxLength: Int32) {
        self.ruleName = ruleName
        self.strictLevel = strictLevel
        self.maxLength = maxLength
    }

    public func validate(input: String) -> Bool {
        return input.count <= Int(maxLength)
    }
}
