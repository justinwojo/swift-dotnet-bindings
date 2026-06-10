// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Functions Returning Closures

/// Returns an adder closure.
public func makeAdder(_ base: Int32) -> (Int32) -> Int32 {
    return { x in base + x }
}

/// Returns a multiplier closure.
public func makeMultiplier(_ factor: Int32) -> (Int32) -> Int32 {
    return { x in factor * x }
}

/// Returns a predicate closure.
public func makeGreaterThan(_ threshold: Int32) -> (Int32) -> Bool {
    return { x in x > threshold }
}

// MARK: - Closure Properties Returning Class Types

/// Holder with a closure property that returns a class.
/// Tests the C12 gate fix: closure properties returning class types were previously blocked
/// because the P/Invoke return type maps to void* but the C# delegate expects the class type.
/// The fix wraps the void* result in `new ClassName(new SwiftHandle((IntPtr)...))`.
public final class ClosureClassReturnHolder {
    private let _count: Int32

    public init(count: Int32) {
        self._count = count
    }

    /// Closure property returning a class type (non-optional).
    /// Exercises the fallback lambda class return wrapping path in EmitClosureReturnMarshalling.
    public var counterFactory: () -> FinalCounter {
        return { FinalCounter(count: self._count) }
    }

    /// Static closure property returning a class.
    /// Exercises the static-closure-property returning a class shape.
    public static var defaultCounter: () -> FinalCounter {
        return { FinalCounter(count: 0) }
    }
}

// MARK: - Struct Returning Closures

/// A frozen struct with methods that return closures.
@frozen
public struct ClosureFactory {
    public let base: Int32

    public init(base: Int32) {
        self.base = base
    }

    /// Instance method returning a closure.
    public func makeTransform() -> (Int32) -> Int32 {
        return { x in self.base + x }
    }

    /// Static method returning a closure.
    public static func makeScaler(_ scale: Int32) -> (Int32) -> Int32 {
        return { x in x * scale }
    }
}
