// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - OptionSet (BonMot Emphasis pattern)

/// OptionSet struct for testing bitflag-style type emission.
/// Real-world pattern: BonMot Emphasis, XMLParsingOptions.
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

// MARK: - Nested OptionSet in Class (Nuke ImageRequest.Options pattern)

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
