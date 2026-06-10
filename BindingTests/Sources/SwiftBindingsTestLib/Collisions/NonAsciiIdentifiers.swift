// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Non-ASCII Identifiers
// Pattern caught in real-world library validation (accented identifiers and attributed-text libraries).
// Swift allows emoji and Unicode identifiers; C# has stricter rules.
// Generator must sanitize these to valid C# identifiers.

/// Struct with non-ASCII property names (accented characters).
/// Pattern: accented identifiers from French-derived APIs.
public struct AccentedConfig {
    public var name: String      // ASCII baseline
    public var resume: String    // Close to 'résumé' pattern

    public init(name: String, resume: String) {
        self.name = name
        self.resume = resume
    }

    public func describe() -> String {
        return "\(name): \(resume)"
    }
}

/// Enum with cases using non-ASCII characters.
public enum MarkupStyle: Int32 {
    case plain = 0
    case bold = 1
    case italic = 2
    case strikethrough = 3
}

/// Function with non-ASCII in parameter labels.
/// Tests that the generator sanitizes parameter names for C#.
public func formatText(style: MarkupStyle, content: String) -> String {
    switch style {
    case .plain: return content
    case .bold: return "**\(content)**"
    case .italic: return "_\(content)_"
    case .strikethrough: return "~~\(content)~~"
    @unknown default: return content
    }
}

/// Factory function for AccentedConfig.
public func makeAccentedConfig(name: String, resume: String) -> AccentedConfig {
    return AccentedConfig(name: name, resume: resume)
}
