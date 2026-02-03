// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Generic Free Functions

/// Unconstrained generic identity function.
public func identity<T>(_ value: T) -> T {
    return value
}

/// Generic function returning a tuple pair.
public func pair<T, U>(_ first: T, _ second: U) -> (T, U) {
    return (first, second)
}

/// Generic function with a single protocol constraint.
public func constrained<T: Describable>(_ item: T) -> String {
    return item.describe()
}

/// Generic function with multiple protocol constraints.
public func multiConstrained<T: Describable & TestIdentifiable>(_ item: T) -> String {
    return "[\(item.id)] \(item.describe())"
}

/// Generic function with where clause.
public func compareIdentifiables<T: TestIdentifiable, U: TestIdentifiable>(_ a: T, _ b: U) -> Bool where T: Describable {
    return a.id == b.id
}
