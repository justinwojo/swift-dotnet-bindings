// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - OptionSet

/// OptionSet struct for testing bitflag-style type emission.
public struct TextStyle: OptionSet {
    public let rawValue: Int32

    public init(rawValue: Int32) {
        self.rawValue = rawValue
    }

    public static let bold = TextStyle(rawValue: 1 << 0)
    public static let italic = TextStyle(rawValue: 1 << 1)
    public static let underline = TextStyle(rawValue: 1 << 2)
    public static let strikethrough = TextStyle(rawValue: 1 << 3)
}

// MARK: - Nested OptionSet in Class

/// Class with nested OptionSet struct.
public class ImageRequest {
    public struct Options: OptionSet {
        public let rawValue: Int32

        public init(rawValue: Int32) {
            self.rawValue = rawValue
        }

        public static let disableCache = Options(rawValue: 1 << 0)
        public static let returnCached = Options(rawValue: 1 << 1)
        public static let lowPriority = Options(rawValue: 1 << 2)
    }

    public var options: Options

    public init(options: Options) {
        self.options = options
    }
}

// MARK: - Frozen OptionSet over a narrow raw type

/// `@frozen` so this projects as a C# value type, and `UInt8` so the raw value is narrower than
/// the type C# promotes bitwise operands to — together they exercise the value-type arm and the
/// wrapping cast that a synthesized complement needs.
@frozen
public struct AccessFlags: OptionSet {
    public let rawValue: UInt8

    public init(rawValue: UInt8) {
        self.rawValue = rawValue
    }

    public static let read = AccessFlags(rawValue: 1 << 0)
    public static let write = AccessFlags(rawValue: 1 << 1)
    public static let execute = AccessFlags(rawValue: 1 << 2)
}

// MARK: - OptionSet over a platform-width raw value

/// `Int` rather than a fixed-width integer, so the emitted property (narrowed to `int`) and the
/// initializer parameter (left at `nint`) disagree. Deliberately not `@frozen`, so this projects
/// as a C# class and the handle-taking constructor a synthesized `new` expression can collide
/// with actually exists.
public struct PermissionMask: OptionSet {
    public let rawValue: Int

    public init(rawValue: Int) {
        self.rawValue = rawValue
    }

    public static let readData = PermissionMask(rawValue: 1 << 0)
    public static let writeData = PermissionMask(rawValue: 1 << 1)
    public static let share = PermissionMask(rawValue: 1 << 2)
}

// MARK: - OptionSet Helper

/// Describe a TextStyle as a comma-separated list of active flags.
public func describeTextStyle(_ style: TextStyle) -> String {
    var parts: [String] = []
    if style.contains(.bold) { parts.append("bold") }
    if style.contains(.italic) { parts.append("italic") }
    if style.contains(.underline) { parts.append("underline") }
    if style.contains(.strikethrough) { parts.append("strikethrough") }
    return parts.joined(separator: ", ")
}

/// Describe a PermissionMask, so a mask combined on the C# side over a platform-width raw value
/// can be proven to arrive in Swift as the same option set.
public func describePermissionMask(_ mask: PermissionMask) -> String {
    var parts: [String] = []
    if mask.contains(.readData) { parts.append("readData") }
    if mask.contains(.writeData) { parts.append("writeData") }
    if mask.contains(.share) { parts.append("share") }
    return parts.joined(separator: ", ")
}

/// Describe an AccessFlags value, so a set combined on the C# side can be proven to arrive in
/// Swift as the same option set rather than merely holding the expected raw bits.
public func describeAccessFlags(_ flags: AccessFlags) -> String {
    var parts: [String] = []
    if flags.contains(.read) { parts.append("read") }
    if flags.contains(.write) { parts.append("write") }
    if flags.contains(.execute) { parts.append("execute") }
    return parts.joined(separator: ", ")
}
