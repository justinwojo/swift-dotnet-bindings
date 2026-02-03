// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Protocol with Self or Associated Type Requirements

/// A protocol requiring an add operation.
public protocol Summable {
    func add(_ other: Self) -> Self
}

/// Conforming frozen struct for Summable.
@frozen
public struct SummableInt32: Summable {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func add(_ other: SummableInt32) -> SummableInt32 {
        return SummableInt32(value: value + other.value)
    }
}

// MARK: - Generic Type with Constraints

/// A generic struct constrained by Summable.
/// Note: not @frozen — binding generator cannot resolve generic type parameter layouts.
public struct AcceptsSummable<T: Summable> {
    public let item: T

    public init(item: T) {
        self.item = item
    }

    public func addWith(_ other: T) -> T {
        return item.add(other)
    }
}

// MARK: - Protocol with Associated Type

/// A protocol with an associated type.
public protocol Container {
    associatedtype Element
    var count: Int32 { get }
    func element(at index: Int32) -> Element
}

/// Concrete conformance to Container.
public struct IntContainer: Container {
    public typealias Element = Int32

    private var items: [Int32]

    public init(items: [Int32]) {
        self.items = items
    }

    public var count: Int32 {
        return Int32(items.count)
    }

    public func element(at index: Int32) -> Int32 {
        return items[Int(index)]
    }
}

// MARK: - Where-Clause Functions

/// Generic function with a where clause constraining to Summable.
public func sumTwo<T: Summable>(_ a: T, _ b: T) -> T {
    return a.add(b)
}

/// Generic function with multiple where clauses.
public func describeConstrained<T>(_ item: T) -> String where T: Describable, T: TestIdentifiable {
    return "[\(item.id)] \(item.describe())"
}
