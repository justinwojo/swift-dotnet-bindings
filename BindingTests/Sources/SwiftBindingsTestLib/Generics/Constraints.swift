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

// MARK: - Where-Clause Functions

/// Generic function with a where clause constraining to Summable.
public func sumTwo<T: Summable>(_ a: T, _ b: T) -> T {
    return a.add(b)
}

/// Generic function with multiple where clauses.
public func describeConstrained<T>(_ item: T) -> String where T: Describable, T: TestIdentifiable {
    return "[\(item.id)] \(item.describe())"
}

// MARK: - Concrete Protocol Specialization

/// A protocol with a Self requirement — triggers GenericProtocolConstraint skip.
/// The concrete specialization engine provides overloads for known conformers.
public protocol Processable {
    func process() -> Self
    var label: String { get }
}

/// First conformer for Processable.
@frozen
public struct TextItem: Processable {
    public let text: String

    public init(text: String) {
        self.text = text
    }

    public func process() -> TextItem {
        return TextItem(text: text.uppercased())
    }

    public var label: String { return "text:\(text)" }
}

/// Second conformer for Processable.
@frozen
public struct NumberItem: Processable {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func process() -> NumberItem {
        return NumberItem(value: value * 2)
    }

    public var label: String { return "number:\(value)" }
}

/// Non-generic struct with method-level generic constrained to Processable.
/// The specialization engine emits one concrete overload per known conformer.
@frozen
public struct ItemProcessor {
    public let prefix: String

    public init(prefix: String) {
        self.prefix = prefix
    }

    /// Method with protocol-constrained generic parameter.
    /// Specialized to: processItem(TextItem) and processItem(NumberItem).
    public func processItem<T: Processable>(_ item: T) -> String {
        let result = item.process()
        return "\(prefix): \(result.label)"
    }

    /// Static method with protocol-constrained generic parameter.
    public static func describe<T: Processable>(_ item: T) -> String {
        return item.label
    }
}

// MARK: - M2: Generic Constructor with PWT (DifferenceKit DifferentiableBox pattern)

/// Generic class where the constructor requires both type metadata and a protocol witness table.
/// The PWT is for the Describable constraint.
public class ConstrainedBox<T: Describable> {
    public let item: T

    public init(item: T) {
        self.item = item
    }

    public func getDescription() -> String {
        return item.describe()
    }
}
