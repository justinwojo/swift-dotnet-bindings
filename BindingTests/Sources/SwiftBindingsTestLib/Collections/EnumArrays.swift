// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Simple Enum Array Functions
// Tests: SwiftArray<SimpleEnum> marshalling with 1-byte Swift / 4-byte C# size mismatch.

/// Count directions in an array.
public func countDirections(_ directions: [Direction]) -> Int32 {
    return Int32(directions.count)
}

/// Check if a direction array contains a specific direction.
public func directionsContain(_ directions: [Direction], target: Direction) -> Bool {
    return directions.contains(target)
}

/// Return the first direction in an array, or .north as default.
public func firstDirection(_ directions: [Direction]) -> Direction {
    return directions.first ?? .north
}

/// Create an array of all four directions.
public func allDirections() -> [Direction] {
    return [.north, .south, .east, .west]
}
