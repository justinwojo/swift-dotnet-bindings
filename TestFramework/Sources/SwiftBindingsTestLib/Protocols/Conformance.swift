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
