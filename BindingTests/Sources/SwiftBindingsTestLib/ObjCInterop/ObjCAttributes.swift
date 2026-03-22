// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - @objc Attribute

/// Class with individual @objc-annotated members.
/// Tests that @objc methods and properties are visible through Objective-C runtime.
public class ObjCAnnotated: NSObject {
    @objc public var title: String

    public init(title: String) {
        self.title = title
        super.init()
    }

    /// Method exposed to Objective-C.
    @objc public func objcMethod() -> String {
        return "ObjC: \(title)"
    }

    /// Property exposed to Objective-C.
    @objc public var uppercaseTitle: String {
        return title.uppercased()
    }

    /// Non-@objc method (Swift only).
    public func swiftOnlyMethod() -> Int32 {
        return Int32(title.count)
    }
}

// MARK: - @objcMembers

/// Class where all members are automatically exposed to Objective-C.
/// Expected C#: Same emission as regular class, but @objcMembers affects ABI visibility.
@objcMembers
public class FullyObjCExposed: NSObject {
    public var identifier: String
    public var value: Int32

    public init(identifier: String, value: Int32) {
        self.identifier = identifier
        self.value = value
        super.init()
    }

    /// Automatically @objc due to @objcMembers.
    public func summary() -> String {
        return "\(identifier): \(value)"
    }

    /// Automatically @objc due to @objcMembers.
    public func doubleValue() -> Int32 {
        return value * 2
    }

    /// Static method, also automatically @objc.
    public static func defaultItem() -> FullyObjCExposed {
        return FullyObjCExposed(identifier: "default", value: 0)
    }
}

// MARK: - @objc Enum

/// Integer-backed enum exposed to Objective-C.
/// Only Int-backed enums can be @objc.
@objc public enum ObjCPriority: Int32 {
    case low = 0
    case medium = 1
    case high = 2
    case critical = 3
}

// MARK: - Free Functions

/// Creates an ObjCAnnotated instance.
public func createObjCAnnotated(title: String) -> ObjCAnnotated {
    return ObjCAnnotated(title: title)
}

/// Returns the priority label for a given priority.
public func priorityLabel(_ priority: ObjCPriority) -> String {
    switch priority {
    case .low: return "Low"
    case .medium: return "Medium"
    case .high: return "High"
    case .critical: return "Critical"
    @unknown default: return "Unknown"
    }
}
