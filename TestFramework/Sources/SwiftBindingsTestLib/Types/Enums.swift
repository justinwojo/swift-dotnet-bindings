// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Simple Enum

/// Simple enum with no raw value or associated values.
@frozen public enum Direction {
    case north
    case south
    case east
    case west

    /// Method on a simple enum.
    public func opposite() -> Direction {
        switch self {
        case .north: return .south
        case .south: return .north
        case .east: return .west
        case .west: return .east
        }
    }
}

// MARK: - Raw Value Enums

/// Enum with Int32 raw value.
@frozen public enum Color: Int32 {
    case red = 0
    case green = 1
    case blue = 2
    case alpha = 3
}

/// Enum with String raw value.
public enum StatusCode: String {
    case ok = "OK"
    case notFound = "NOT_FOUND"
    case error = "ERROR"
    case timeout = "TIMEOUT"
}

// MARK: - BX2 Simple Enum with Members

/// Frozen enum exercising all BX2 simple enum extension features:
/// CustomStringConvertible.description, CaseIterable, instance property,
/// static method, static property.
@frozen public enum Priority: Int32, CustomStringConvertible, CaseIterable {
    case low = 0
    case medium = 1
    case high = 2
    case critical = 3

    public var description: String {
        switch self {
        case .low: return "Low"
        case .medium: return "Medium"
        case .high: return "High"
        case .critical: return "Critical"
        }
    }

    public var numericValue: Int32 { return self.rawValue }

    public static func defaultPriority() -> Priority { return .medium }

    public static var maxValue: Int32 { return 3 }
}

// MARK: - Enum with Associated Values

/// Enum with associated values (discriminated union).
public enum Shape {
    case circle(radius: Double)
    case rectangle(width: Double, height: Double)
    case point(FrozenPoint)
    case empty

    /// Computed property on an enum.
    public var area: Double {
        switch self {
        case .circle(let radius):
            return Double.pi * radius * radius
        case .rectangle(let width, let height):
            return width * height
        case .point:
            return 0.0
        case .empty:
            return 0.0
        }
    }

    /// Method on an enum.
    public func describe() -> String {
        switch self {
        case .circle(let radius):
            return "Circle with radius \(radius)"
        case .rectangle(let width, let height):
            return "Rectangle \(width)x\(height)"
        case .point(let p):
            return "Point at (\(p.x), \(p.y))"
        case .empty:
            return "Empty shape"
        }
    }
}

// MARK: - Generic Enum

/// Generic enum testing generic enum emission.
public enum GenericResult<T> {
    case success(T)
    case failure(String)

    /// Check if this is a success case.
    public var isSuccess: Bool {
        switch self {
        case .success: return true
        case .failure: return false
        }
    }
}

// MARK: - Enum Property Holder

/// Class with non-simple enum stored properties for testing B18 gate lift.
/// Verifies that non-simple enum property getters/setters compile and work correctly.
public class EnumPropertyHolder {
    public var currentShape: Shape
    public var optionalShape: Shape?

    public init(shape: Shape) {
        self.currentShape = shape
        self.optionalShape = nil
    }

    public func getShape() -> Shape {
        return currentShape
    }
}

// MARK: - Helper Functions

/// Free function accepting an enum.
public func isHorizontal(_ direction: Direction) -> Bool {
    switch direction {
    case .east, .west: return true
    case .north, .south: return false
    }
}

/// Free function returning an enum.
public func colorForIndex(_ index: Int32) -> Color {
    return Color(rawValue: index) ?? .red
}

// MARK: - Enum Extension Methods (KeychainAccess/DeviceKit pattern)

/// Extension-defined methods on Color enum.
/// Tests extension method emission (different path from inline methods like Direction.opposite()).
extension Color {
    public func complementary() -> Int32 { (self.rawValue + 3) % 6 }

    public func getHexDescription() -> String {
        switch self {
        case .red: return "#FF0000"
        case .green: return "#00FF00"
        case .blue: return "#0000FF"
        case .alpha: return "#000000FF"
        }
    }
}

/// Extension-defined method on Direction enum (separate from inline opposite()).
extension Direction {
    public func getDescription() -> String {
        switch self {
        case .north: return "North"
        case .south: return "South"
        case .east: return "East"
        case .west: return "West"
        }
    }
}
