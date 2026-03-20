// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol Existential Constructor Parameter

/// Class with constructor taking protocol existential — exercises
/// GetCdeclParamMapping:727-732 (IsProtocolExistentialType branch).
public class DescriptionPrinter {
    public let text: String

    public init(source: any Describable) {
        self.text = source.describe()
    }

    public func getText() -> String {
        return text
    }
}

// MARK: - Optional<Class> Constructor Parameter

/// Class with constructor taking Optional<Class> — exercises
/// GetCdeclParamMapping:738-769 (IsOptionalWithReferenceInner branch).
/// Real-world pattern: Swinject Container(parent:).
public class LinkedNode {
    public let value: Int32
    public let previous: Animal?

    public init(value: Int32, previous: Animal?) {
        self.value = value
        self.previous = previous
    }

    public func describe() -> String {
        if let p = previous {
            return "\(value) -> \(p.name)"
        }
        return "\(value) -> nil"
    }
}

// MARK: - Complex Enum Constructor Parameter

/// Struct with constructor taking complex enum (associated values) — exercises
/// GetCdeclParamMapping:951-956 (complex enum param branch).
public struct ShapeMetrics {
    public let description: String
    public let area: Double

    public init(shape: Shape) {
        self.description = shape.describe()
        self.area = shape.area
    }

    public func summary() -> String {
        return "\(description): area=\(area)"
    }
}

// MARK: - Non-Frozen Failable Init

/// Non-frozen struct with failable init — exercises
/// ShouldEmitWrapper:43-45 (non-frozen failable init guard).
public struct ValidatedName {
    public let name: String

    public init?(name: String) {
        guard !name.isEmpty else { return nil }
        self.name = name
    }

    public func describe() -> String {
        return "Name: \(name)"
    }
}

// MARK: - Closure Constructor Parameter

/// Struct with closure constructor parameter — exercises
/// ShouldEmitWrapper:59-65 (closure constructor param guard).
public struct CallbackHolder {
    private let callback: (Int32) -> Void
    public let label: String

    public init(label: String, callback: @escaping (Int32) -> Void) {
        self.label = label
        self.callback = callback
    }

    public func trigger(value: Int32) {
        callback(value)
    }

    public func getLabel() -> String {
        return label
    }
}

// MARK: - Foundation.Date and Foundation.Data Constructor Parameters

/// Class with Foundation.Date and Foundation.Data constructor params — exercises
/// GetCdeclParamMapping:824-828 (Date) and 837-841 (Data).
public class TimestampedBlob {
    public let timestamp: Date
    public let contents: Data

    public init(timestamp: Date, contents: Data) {
        self.timestamp = timestamp
        self.contents = contents
    }

    public func contentsSize() -> Int32 {
        return Int32(contents.count)
    }

    public func age() -> Double {
        return -timestamp.timeIntervalSinceNow
    }
}

// MARK: - Tag-Only Enum Constructor Parameter

/// Simple enum without raw value — exercises
/// GetCdeclParamMapping:935-940 (tag-only enum, no rawValue).
public class DirectionHolder {
    public let direction: Direction
    public let label: String

    public init(direction: Direction, label: String) {
        self.direction = direction
        self.label = label
    }

    public func describe() -> String {
        return "\(label): \(direction)"
    }
}
