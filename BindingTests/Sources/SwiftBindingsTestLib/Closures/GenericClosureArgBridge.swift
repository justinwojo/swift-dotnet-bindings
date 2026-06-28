// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Generic type parameter in closure ARGUMENT position (GenericClosureBridge gate (c))
//
// The existing GenericClosureBridge handles method-generic, noescape, throwing closures
// whose generic parameter appears in RETURN position only (e.g. `(Database) throws -> T`).
// A generic parameter in closure ARGUMENT position — `(T) throws -> T` — was gated out
// because the C# [UnmanagedCallersOnly] callback counted only concrete closure args, so
// Swift's cdecl callback passed one more void* than C# declared (an ABI mismatch).
//
// These fixtures exercise the generic-arg-in-input shape end to end: the method receives a
// generic value, hands it to the closure, the C# side transforms it, and the transformed
// value flows back as the method result.

/// A simple Swift class used as the concrete `T` instantiation so the round-trip carries a
/// real reference-counted payload across the closure boundary in both directions.
public final class LevelKnob {
    public var level: Int32
    public init(level: Int32) { self.level = level }
}

public final class GenericArgClosureFixture {
    public init() {}

    /// Method-generic, noescape, throwing closure with the generic parameter in BOTH
    /// argument and return position. The method supplies the `T` value; the closure
    /// receives it, may transform it, and returns a `T` that becomes the method result.
    public func apply<T>(_ value: T, _ transform: (T) throws -> T) rethrows -> T {
        return try transform(value)
    }

    /// Two generic args in argument position plus a generic return — exercises multi-arg
    /// generic-input counting in the callback (each generic arg becomes its own void*).
    public func combine<T>(_ first: T, _ second: T, _ merge: (T, T) throws -> T) rethrows -> T {
        return try merge(first, second)
    }
}
