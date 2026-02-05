// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Generic Container

/// A generic box type used as the base for conditional conformance.
/// Tests: extension Box: CustomStringConvertible where T: CustomStringConvertible.
/// Expected C#: Box<T> implements ICustomStringConvertible only when T does.
public struct Box<T> {
    public var value: T

    public init(value: T) {
        self.value = value
    }

    /// Returns the boxed value.
    public func unwrap() -> T {
        return value
    }
}

// MARK: - Conditional Conformance to CustomStringConvertible

/// Box gains CustomStringConvertible only when its element type also conforms.
extension Box: CustomStringConvertible where T: CustomStringConvertible {
    public var description: String {
        return "Box(\(value.description))"
    }
}

// MARK: - Conditional Conformance to Equatable

/// Box gains Equatable only when its element type also conforms.
extension Box: Equatable where T: Equatable {
    public static func == (lhs: Box<T>, rhs: Box<T>) -> Bool {
        return lhs.value == rhs.value
    }
}

// MARK: - Concrete Types for Testing

/// A describable type that satisfies the CustomStringConvertible constraint.
public struct DescribableItem: CustomStringConvertible {
    public var name: String

    public init(name: String) {
        self.name = name
    }

    public var description: String {
        return "DescribableItem(\(name))"
    }
}

/// A non-describable type that does NOT satisfy CustomStringConvertible.
public struct PlainItem {
    public var tag: Int32

    public init(tag: Int32) {
        self.tag = tag
    }
}

// MARK: - Free Functions

/// Creates a Box containing a describable item (Box will have description).
public func createDescribableBox(name: String) -> Box<DescribableItem> {
    return Box(value: DescribableItem(name: name))
}

/// Creates a Box containing a plain item (Box will NOT have description).
public func createPlainBox(tag: Int32) -> Box<PlainItem> {
    return Box(value: PlainItem(tag: tag))
}

/// Describes a box only when T is CustomStringConvertible.
public func describeBox<T: CustomStringConvertible>(_ box: Box<T>) -> String {
    return box.description
}
