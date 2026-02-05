// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Array as Parameter

/// Returns the count of elements in the array.
public func arrayCount(_ array: [Int32]) -> Int32 {
    return Int32(array.count)
}

/// Sums all elements in the array.
public func sumArray(_ array: [Int32]) -> Int32 {
    return array.reduce(0, +)
}

/// Returns true if the array is empty.
public func isEmptyArray(_ array: [Int32]) -> Bool {
    return array.isEmpty
}

// MARK: - Array as Return

/// Creates an array of count elements, each set to value.
public func createIntArray(count: Int32, value: Int32) -> [Int32] {
    return Array(repeating: value, count: Int(count))
}

/// Creates a two-element string array.
public func createStringArray(first: String, second: String) -> [String] {
    return [first, second]
}

/// Reverses an Int32 array.
public func reverseIntArray(_ array: [Int32]) -> [Int32] {
    return array.reversed()
}

// MARK: - Array of Class Types

/// Describes each animal in the array, returning an array of descriptions.
public func describeAnimals(_ animals: [Animal]) -> [String] {
    return animals.map { $0.describe() }
}

// MARK: - Array Round-Trip

/// Filters out non-positive values from the array.
public func filterPositive(_ array: [Int32]) -> [Int32] {
    return array.filter { $0 > 0 }
}
