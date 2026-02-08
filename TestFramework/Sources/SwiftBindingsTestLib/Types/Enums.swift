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
