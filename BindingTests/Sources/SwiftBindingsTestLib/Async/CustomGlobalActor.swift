// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Custom Global Actor Pattern
//
// A class annotated with a custom @globalActor (e.g., @<Library>Actor) inherits the
// global actor's isolation on every member — including its inits. Swift 6 rejects a
// synchronous @_cdecl wrapper that calls such an init: there is no compiler-supported
// way to enter @<Actor> global-actor isolation from a nonisolated context. The
// `<Actor>.shared.assumeIsolated { _ in init() }` form propagates *instance*-actor
// isolation, which Swift treats as a different domain from the @<Actor> *global-actor*
// isolation the init requires, so the wrapper would fail to compile.
//
// Instead, when the actor's TypeDecl is reachable in the bound module the binding
// generator emits the constructor directly in C# (with the SB0001 [Obsolete] safety
// warning) and routes it through CallConvSwift to the Swift-native init. This works at
// runtime when the C# caller is already on the actor's executor — the documented Swift
// contract for synchronous actor-isolated entry. The fixture below makes that contract
// trivial to honor in the runtime test app: BindingsTestGlobalActor delegates its
// serial executor to MainActor, so calls from the main thread (the standard iOS
// UIApplication entry point) land on the actor's executor without hopping. A real-world
// @globalActor with its own queue would crash if constructed off-actor, which is the
// documented runtime contract on consumers.
//
// When the actor's TypeDecl is NOT reachable (cross-module imported actor) the
// constructor falls through to the SWIFTBIND022 wholesale skip — the rest of the
// binding still compiles, just without that init.

/// A custom global actor declared with the @globalActor attribute. The serial executor
/// delegates to MainActor so the Swift-native init lands on the actor's executor when
/// the C# caller is on the main thread — the only context the runtime test app
/// guarantees.
@globalActor
public actor BindingsTestGlobalActor {
    public static let shared = BindingsTestGlobalActor()

    public nonisolated var unownedExecutor: UnownedSerialExecutor {
        MainActor.sharedUnownedExecutor
    }
}

/// A class isolated to the custom global actor with default-parameter init. Both the
/// primary init and the default-parameter overload reach C# via direct CallConvSwift
/// to the Swift-native init (no @_cdecl wrapper — Swift 6 doesn't allow synchronous
/// entry into custom global-actor isolation). `describe()` exercises an actor-isolated
/// method on the same type, which the binding surfaces as an async method.
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

/// Throwing init on a custom-global-actor-isolated class. Same emission path as
/// `GlobalActorIsolatedClass`: direct CallConvSwift to the Swift-native init, no
/// @_cdecl wrapper. The throw surfaces to C# as a managed exception via the existing
/// SwiftError plumbing on CallConvSwift inits.
@BindingsTestGlobalActor
public class GlobalActorIsolatedThrowingClass {
    public let value: Int32

    public init(value: Int32 = 0, failIf negative: Bool = false) throws {
        if negative && value < 0 {
            throw NSError(domain: "GlobalActorIsolatedThrowingClass", code: -1,
                          userInfo: [NSLocalizedDescriptionKey: "negative value rejected"])
        }
        self.value = value
    }
}
