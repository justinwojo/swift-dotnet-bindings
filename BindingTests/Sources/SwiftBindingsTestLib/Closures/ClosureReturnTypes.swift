// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Closure Return Type Edge Cases

/// Closure returning a frozen struct (direct return path).
/// FrozenPoint is blittable — callback returns by value.
public func callWithFrozenPointReturn(_ callback: @escaping () -> FrozenPoint) -> FrozenPoint {
    return callback()
}

/// Closure returning a simple enum (integer return path).
/// Color has Int32 raw value — callback returns the underlying integer.
public func callWithEnumReturn(_ callback: @escaping () -> Color) -> Color {
    return callback()
}

/// Closure returning a frozen struct, with a parameter to the closure.
/// Tests that both parameter marshalling and direct return work together.
public func callWithFrozenPointTransform(_ value: Double, _ callback: @escaping (Double) -> FrozenPoint) -> FrozenPoint {
    return callback(value)
}

/// Closure returning Bool (special byte marshalling path).
/// Tests the Bool↔byte conversion in closure return.
public func callWithBoolReturn(_ callback: @escaping (Int32) -> Bool) -> Bool {
    return callback(42)
}

/// Closure returning a non-frozen struct (indirect return path).
/// NonFrozenPoint requires indirect return marshalling via SwiftMarshal.
public func callWithNonFrozenReturn(_ callback: @escaping () -> NonFrozenPoint) -> NonFrozenPoint {
    return callback()
}

/// Closure returning a class (indirect return with memory management).
/// FinalCounter is a reference type — tests class return marshalling.
public func callWithClassReturn(_ callback: @escaping () -> FinalCounter) -> FinalCounter {
    return callback()
}
