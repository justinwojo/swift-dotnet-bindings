// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Custom Global Actor Pattern (Nuke 13.0.2 repro)
//
// Reproduces the @<Library>Actor + extension-default-param pattern that broke Nuke 13.0.2:
// the wrapper generator emitted `extension Type { static func _dbw_init_*(...) }` which
// inherited the type's actor isolation. The synchronous @_cdecl wrapper then failed to
// compile with: "call to global actor 'XActor'-isolated static method '_dbw_init_*()' in
// a synchronous nonisolated context."
//
// SWIFTBIND022 skip: constructors on these types are skipped with an actor-isolation
// warning, leaving the rest of the binding compilable. Other (non-init) members of the
// type still surface in the bindings.

/// A custom global actor declared with the @globalActor attribute.
@globalActor
public actor BindingsTestGlobalActor {
    public static let shared = BindingsTestGlobalActor()
}

/// A class isolated to the custom global actor with default-parameter init.
///
/// The default-param overload pattern produces an extension `_dbw_init_*`
/// that inherits the actor isolation, which is what triggers SWIFTBIND022.
@BindingsTestGlobalActor
public class GlobalActorIsolatedClass {
    public let label: String
    public let count: Int32

    public init(label: String = "default", count: Int32 = 0) {
        self.label = label
        self.count = count
    }

    public func describe() -> String {
        return "\(label):\(count)"
    }
}
