// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Key Path Target Types

/// A frozen struct used as a key path root type.
/// Tests: KeyPath<KeyPathTarget, V> for various property types.
/// Expected C#: KeyPath mapped to delegate or opaque handle.
@frozen
public struct KeyPathTarget {
    public var x: Int32
    public var y: Int32
    public var label: String

    public init(x: Int32, y: Int32, label: String) {
        self.x = x
        self.y = y
        self.label = label
    }
}

// MARK: - Read-Only Key Path Functions

/// Reads a property from a KeyPathTarget using a KeyPath.
public func readProperty<V>(_ target: KeyPathTarget, keyPath: KeyPath<KeyPathTarget, V>) -> V {
    return target[keyPath: keyPath]
}

/// Returns the Int32 value at the given key path.
public func readInt32Property(_ target: KeyPathTarget, keyPath: KeyPath<KeyPathTarget, Int32>) -> Int32 {
    return target[keyPath: keyPath]
}

/// Returns the String value at the given key path.
public func readStringProperty(_ target: KeyPathTarget, keyPath: KeyPath<KeyPathTarget, String>) -> String {
    return target[keyPath: keyPath]
}

// MARK: - Writable Key Path Functions

/// Writes a value to a KeyPathTarget using a WritableKeyPath.
public func writeProperty(_ target: inout KeyPathTarget, keyPath: WritableKeyPath<KeyPathTarget, Int32>, value: Int32) {
    target[keyPath: keyPath] = value
}

/// Increments a property on a KeyPathTarget via a WritableKeyPath.
public func incrementProperty(_ target: inout KeyPathTarget, keyPath: WritableKeyPath<KeyPathTarget, Int32>) {
    target[keyPath: keyPath] += 1
}

// MARK: - Key Path as Parameter

/// A struct that stores a key path and uses it to extract values.
@frozen
public struct KeyPathExtractor {
    public let keyPath: KeyPath<KeyPathTarget, Int32>

    public init(keyPath: KeyPath<KeyPathTarget, Int32>) {
        self.keyPath = keyPath
    }

    /// Extracts the value from the given target using the stored key path.
    public func extract(from target: KeyPathTarget) -> Int32 {
        return target[keyPath: keyPath]
    }
}

// MARK: - Generic Key Path Functions

/// Generic function that reads a value from any root type using a key path.
public func getValue<Root, Value>(_ root: Root, at keyPath: KeyPath<Root, Value>) -> Value {
    return root[keyPath: keyPath]
}

/// Generic function that writes a value to any root type using a writable key path.
public func setValue<Root, Value>(_ root: inout Root, at keyPath: WritableKeyPath<Root, Value>, to value: Value) {
    root[keyPath: keyPath] = value
}
