// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Unconstrained generics instantiated with String from C#
//
// A C# caller binds an unconstrained `T` to `string`, which projects onto Swift.String. Every
// generic value seam — a method parameter, a generic return, a stored generic property and its
// setter — has to carry the managed string to Swift and back as a Swift String.

/// A non-generic host with method-level generics.
public struct StringGenericHost {
    public init() {}

    public func echo<T>(_ value: T) -> T { value }

    public func describe<T>(_ value: T) -> String { "<\(value)>" }
}

/// A generic class that stores its type parameter.
public final class StringGenericCell<T> {
    public var value: T

    public init(_ value: T) { self.value = value }

    public func replace(_ newValue: T) -> T {
        let old = value
        value = newValue
        return old
    }

    public func describe() -> String { "cell:\(value)" }
}
