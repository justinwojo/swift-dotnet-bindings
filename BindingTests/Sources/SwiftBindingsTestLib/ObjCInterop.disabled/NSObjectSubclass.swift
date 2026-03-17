// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Basic NSObject Subclass

/// A simple NSObject subclass to test Objective-C class hierarchy emission.
/// Expected C#: class that inherits from NSObject-rooted handle type.
public class SimpleNSObject: NSObject {
    public var label: String

    public init(label: String) {
        self.label = label
        super.init()
    }

    /// Instance method on an NSObject subclass.
    public func describe() -> String {
        return "SimpleNSObject: \(label)"
    }
}

// MARK: - NSObject Subclass with Properties

/// NSObject subclass with stored and computed properties.
public class LabeledItem: NSObject {
    public var name: String
    public var tag: Int32

    public init(name: String, tag: Int32) {
        self.name = name
        self.tag = tag
        super.init()
    }

    /// Computed property.
    public var displayName: String {
        return "\(name) (#\(tag))"
    }

    /// Static factory method.
    public static func create(name: String, tag: Int32) -> LabeledItem {
        return LabeledItem(name: name, tag: tag)
    }
}

// MARK: - NSObject Subclass Inheritance

/// Subclass of an NSObject-derived class, testing multi-level inheritance.
public class SpecialItem: LabeledItem {
    public var priority: Int32

    public init(name: String, tag: Int32, priority: Int32) {
        self.priority = priority
        super.init(name: name, tag: tag)
    }

    /// Overridden computed property.
    public override var displayName: String {
        return "\(name) (#\(tag)) [P\(priority)]"
    }
}

// MARK: - Free Functions

/// Creates an NSObject subclass instance.
public func createSimpleNSObject(label: String) -> SimpleNSObject {
    return SimpleNSObject(label: label)
}

/// Accepts an NSObject parameter and returns its description.
public func describeNSObject(_ obj: NSObject) -> String {
    return obj.description
}
