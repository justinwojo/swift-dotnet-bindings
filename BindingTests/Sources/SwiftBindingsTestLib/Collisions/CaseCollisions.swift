// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Case-Insensitive Enum Case Collisions
// Pattern caught in real-world library validation (21 errors).
// Swift allows case-sensitive enum cases; C# does not.
// Generator must disambiguate (e.g., append raw value or numeric suffix).

/// Enum with cases that differ only in capitalization.
/// In C#, `circle` and `Circle` would collide. Generator must rename.
public enum DrawCommand: Int32 {
    case move = 0
    case Move = 1
    case line = 2
    case Line = 3
    case close = 4
}

/// Another case-insensitive collision pattern with string raw values.
public enum CSSProperty: String {
    case color = "color"
    case Color = "Color"
    case background = "background"
    case BACKGROUND = "BACKGROUND"
}

/// Function that uses the collision enum.
public func describeDrawCommand(_ command: DrawCommand) -> String {
    switch command {
    case .move: return "move-lowercase"
    case .Move: return "Move-uppercase"
    case .line: return "line-lowercase"
    case .Line: return "Line-uppercase"
    case .close: return "close"
    @unknown default: return "unknown"
    }
}

/// Function that uses the CSS property enum.
public func describeCSSProperty(_ property: CSSProperty) -> String {
    return property.rawValue
}

// MARK: - Property/Method Name Collisions

/// Struct where a property name collides with a C# keyword or common type.
public struct CollisionStruct {
    public var value: Int32
    public var type: String    // 'type' is a common collision with C# 'Type'
    public var display: String // display name field

    public init(value: Int32, type: String, display: String) {
        self.value = value
        self.type = type
        self.display = display
    }

    public func format() -> String {
        return "\(type)(\(value)): \(display)"
    }
}
