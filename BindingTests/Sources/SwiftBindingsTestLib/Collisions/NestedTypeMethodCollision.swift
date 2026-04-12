// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - NestedTypeMethodCollision (fix #10)
//
// Pins the nested-type / method name collision detection fix from commit
// 26f764f1. Swift lets a struct have both a nested type and an instance
// method whose PascalCase names are identical — Swift's name lookup
// distinguishes types from values. C# does not. Both end up in the
// enclosing type's member namespace, producing CS0102 ("The type 'X'
// already contains a definition for 'Y'").
//
// Fix #10 extends the property/method rename collision set in
// FrozenStructHandler / NonFrozenStructHandler / EnumHandler to include the
// nested types declared on the same type. When a method's PascalCase name
// equals a nested type's PascalCase name, the method (or the nested type)
// gets renamed via the existing rename mechanism so the emitted C# compiles.
//
// The fixture below has the collision-producing shape: struct `Navigator`
// has a nested struct `Route` AND a method `route(to:)`. Both would PascalCase
// to `Route` in C# and trip CS0102 without fix #10.

/// Struct with a nested type and an instance method whose PascalCase names
/// collide in C#. Fix #10 resolves the collision at emission time by
/// renaming one of them.
public struct Navigator {
    public let origin: String

    public init(origin: String) {
        self.origin = origin
    }

    /// Nested type named `Route`. Flattens in C# to `Navigator.Route` or a
    /// renamed variant when a collision is detected.
    public struct Route {
        public let destination: String

        public init(destination: String) {
            self.destination = destination
        }
    }

    /// Instance method whose PascalCase name is also `Route`. Without
    /// fix #10 the emitted C# trips CS0102 on the method vs the nested
    /// type. With fix #10 one of them is renamed and both are reachable.
    public func route(to destination: String) -> String {
        return "\(origin) -> \(destination)"
    }
}

// MARK: - Helpers exercised by the C# test

/// Constructs a Navigator.Route via its nested-type initializer. Lets the C#
/// test observe the nested type was emitted without having to guess at the
/// C# rename for the nested type (if any) — the factory call goes through
/// the Swift side and returns the constructed value whose C# type the test
/// only has to name via its return type.
public func makeNavigatorRoute(destination: String) -> Navigator.Route {
    return Navigator.Route(destination: destination)
}

/// Invokes `Navigator.route(to:)` through a free function so the C# test
/// doesn't need to know which of the method or the nested type was renamed
/// by the generator's collision detection — the free function resolves the
/// method on the Swift side before it reaches C#.
public func invokeNavigatorRoute(navigator: Navigator, destination: String) -> String {
    return navigator.route(to: destination)
}
