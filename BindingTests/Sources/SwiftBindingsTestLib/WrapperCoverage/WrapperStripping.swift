// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Wrapper Stripping Test Types
//
// Tests for wrapper stripping co-gating. In real-world libraries, some @_cdecl
// wrapper functions fail to compile because they reference types from other
// modules or internal extensions.
// The post-processor strips these, but C# P/Invoke declarations are already emitted.
//
// Since our test library is self-contained, we can't easily trigger the exact
// stripping scenario. Instead we test patterns that exercise the GUARD PATHS
// in the emitter — methods that ShouldEmitWrapper returns false for, which
// the generator should suppress entirely. If C# still emits P/Invokes for
// these suppressed methods, that's the same bug as wrapper stripping.

// MARK: - Type with Mixed Emittable Methods

/// Class with a mix of emittable and non-emittable methods.
/// Tests that the generator correctly suppresses bindings for methods
/// it can't handle while still emitting working methods.
///
/// Methods with opaque return (`some Protocol`), inout params, and
/// method-level generics should all be suppressed by ShouldEmitWrapper guards.
/// If C# emits P/Invokes for them anyway, calling them crashes.
public class MixedEmittability {
    public var name: String
    public var count: Int32

    public init(name: String, count: Int32) {
        self.name = name
        self.count = count
    }

    // --- Emittable methods (all types public, no guards triggered) ---

    public func getName() -> String {
        return name
    }

    public func getCount() -> Int32 {
        return count
    }

    public func describe() -> String {
        return "\(name):\(count)"
    }

    // --- Non-emittable: method-level generics (ShouldEmitWrapper:69-70) ---

    public func transform<T>(_ value: T, using mapper: (T) -> String) -> String {
        return mapper(value)
    }

    // --- Non-emittable: inout param (ShouldEmitWrapper:97-98) ---

    public func increment(counter: inout Int32) {
        counter += count
    }

    // --- Non-emittable: opaque return (ShouldEmitWrapper:133-134) ---

    public func asDescribable() -> some CustomStringConvertible {
        return "\(name):\(count)"
    }
}

// MARK: - Struct with Variadic Param (ShouldEmitWrapper:108-109)

/// Non-frozen struct with variadic constructor and methods.
/// Variadic params exercise ShouldEmitWrapper:108-109 guard.
public struct VariadicHolder {
    public let values: [Int32]

    public init(values: Int32...) {
        self.values = values
    }

    public func sum() -> Int32 {
        return values.reduce(0, +)
    }

    /// Variadic method param — should be suppressed.
    public func append(more: Int32...) -> [Int32] {
        return values + more
    }
}
