// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Metatype Parameters

/// Returns the type name of the given metatype.
/// Tests: T.Type parameter, T.self usage.
/// Expected C#: TypeMetadata or IntPtr representing the Swift metatype.
public func typeName<T>(of type: T.Type) -> String {
    return String(describing: type)
}

/// Creates a default instance of a type that conforms to DefaultInitializable.
public protocol DefaultInitializable {
    init()
}

/// Creates an instance from a metatype parameter.
public func createInstance<T: DefaultInitializable>(of type: T.Type) -> T {
    return T()
}

// MARK: - Concrete Types for Metatype Tests

/// A simple struct conforming to DefaultInitializable.
@frozen
public struct MetatypeTestStruct: DefaultInitializable {
    public var value: Int32

    public init() {
        self.value = 0
    }

    public init(value: Int32) {
        self.value = value
    }
}

/// Another type for metatype comparison tests.
@frozen
public struct AnotherMetatypeStruct: DefaultInitializable {
    public var tag: Int32

    public init() {
        self.tag = -1
    }

    public init(tag: Int32) {
        self.tag = tag
    }
}

// MARK: - Metatype Return

/// Returns the metatype of the given value.
public func getType<T>(of value: T) -> T.Type {
    return type(of: value)
}

// MARK: - Metatype Comparison

/// Checks whether two values have the same type.
public func isSameType<T, U>(_ a: T, _ b: U) -> Bool {
    return type(of: a as Any) == type(of: b as Any)
}

// MARK: - Struct with Metatype Method

/// A class that stores a type and uses it for instance creation.
public class TypeFactory<T: DefaultInitializable> {
    public let storedType: T.Type

    public init(type: T.Type) {
        self.storedType = type
    }

    /// Creates a new instance of the stored type.
    public func create() -> T {
        return storedType.init()
    }
}
