// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Method-Level Generics (Session 5)
// Tests for methods with their own generic type parameters constrained
// to protocols without Self/associated type requirements.
// The generator bridges these via Swift 5.7+ implicit existential opening.

/// Non-generic class with method-level generic parameters.
/// Each method has <T: Describable> — the generator should emit @_cdecl wrappers
/// that receive an existential container and open it via implicit conversion.
public class GenericMethodHost {
    private var _label: String

    public init(label: String) {
        self._label = label
    }

    /// Simplest case: void return, single generic param.
    public func printDescription<T: Describable>(_ item: T) {
        _ = "\(_label): \(item.describe())"
    }

    /// Returns a non-generic type (String) from a generic param.
    public func getDescription<T: Describable>(_ item: T) -> String {
        return "\(_label): \(item.describe())"
    }

    /// Static method with method-level generic.
    public static func staticDescribe<T: Describable>(_ item: T) -> String {
        return "static: \(item.describe())"
    }

    /// Multiple parameters: one generic, one primitive.
    public func describeWithTag<T: Describable>(_ item: T, tag: Int32) -> String {
        return "[\(tag)] \(_label): \(item.describe())"
    }
}

/// A simple class conforming to Describable for testing.
public class SimpleDescribable: Describable {
    public let description: String

    public init(description: String) {
        self.description = description
    }

    public func describe() -> String {
        return description
    }
}
