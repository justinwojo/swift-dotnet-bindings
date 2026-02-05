// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Basic Protocols (Blittable Types)

/// Protocol with a read-only Int32 property.
public protocol Printable {
    func print()
}

/// Protocol with a computed Int32 property.
public protocol HasInt32Value {
    var intValue: Int32 { get }
}

/// Protocol with a method returning Int32.
public protocol Computable {
    func compute(_ input: Int32) -> Int32
}

/// Protocol combining property and method.
public protocol Counter {
    var count: Int32 { get }
    func increment(_ by: Int32) -> Int32
}

// MARK: - Protocol Inheritance

/// Protocol inheriting from Counter.
public protocol ResettableCounter: Counter {
    func reset() -> Int32
}

// MARK: - Conforming Types (Frozen for Blittable Marshalling)

/// Frozen struct conforming to HasInt32Value.
@frozen
public struct IntHolder: HasInt32Value {
    public let intValue: Int32

    public init(value: Int32) {
        self.intValue = value
    }
}

/// Frozen struct conforming to Computable.
@frozen
public struct Doubler: Computable {
    public let multiplier: Int32

    public init(multiplier: Int32) {
        self.multiplier = multiplier
    }

    public func compute(_ input: Int32) -> Int32 {
        return input * multiplier
    }
}

/// Frozen struct conforming to Counter.
@frozen
public struct SimpleCounter: Counter {
    public let count: Int32

    public init(count: Int32) {
        self.count = count
    }

    public func increment(_ by: Int32) -> Int32 {
        return count + by
    }
}

/// Frozen struct conforming to ResettableCounter (inherited protocol).
@frozen
public struct AdvancedCounter: ResettableCounter {
    public let count: Int32

    public init(count: Int32) {
        self.count = count
    }

    public func increment(_ by: Int32) -> Int32 {
        return count + by
    }

    public func reset() -> Int32 {
        return 0
    }
}

/// Frozen struct conforming to multiple protocols.
@frozen
public struct MultiConformer: HasInt32Value, Computable, Counter {
    public let intValue: Int32
    public let count: Int32

    public init(value: Int32, count: Int32) {
        self.intValue = value
        self.count = count
    }

    public func compute(_ input: Int32) -> Int32 {
        return intValue + input
    }

    public func increment(_ by: Int32) -> Int32 {
        return count + by
    }
}

// MARK: - Functions for Testing Existentials

/// Accepts any HasInt32Value and returns its value.
public func extractInt32Value(_ item: any HasInt32Value) -> Int32 {
    return item.intValue
}

/// Accepts any Computable and applies computation.
public func applyComputation(_ item: any Computable, input: Int32) -> Int32 {
    return item.compute(input)
}

/// Accepts any Counter and returns count + increment.
public func getIncrementedCount(_ item: any Counter, by: Int32) -> Int32 {
    return item.increment(by)
}

/// Returns a computed sum using Counter protocol.
public func computeCounterSum(_ item: any Counter, incrementValue: Int32) -> Int32 {
    return item.count + item.increment(incrementValue)
}

// MARK: - Factory Functions (Return Conforming Types)

/// Creates an IntHolder with the given value.
public func createIntHolder(_ value: Int32) -> IntHolder {
    return IntHolder(value: value)
}

/// Creates a Doubler with the given multiplier.
public func createDoubler(_ multiplier: Int32) -> Doubler {
    return Doubler(multiplier: multiplier)
}

/// Creates a SimpleCounter with the given count.
public func createSimpleCounter(_ count: Int32) -> SimpleCounter {
    return SimpleCounter(count: count)
}

/// Creates an AdvancedCounter with the given count.
public func createAdvancedCounter(_ count: Int32) -> AdvancedCounter {
    return AdvancedCounter(count: count)
}

/// Creates a MultiConformer with the given values.
public func createMultiConformer(_ value: Int32, count: Int32) -> MultiConformer {
    return MultiConformer(value: value, count: count)
}
