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
// isolation the init requires, so the wrapper would fail to compile. A direct
// CallConvSwift call from C# to the Swift-native `cfC` allocating init also crashes
// on NativeAOT — the metatype lands in x20 but the actor isolation contract isn't
// established across the foreign-runtime boundary.
//
// Instead, the binding generator emits the constructor as a static async factory:
// `public static Task<T> CreateAsync(...)`. The Swift wrapper becomes
// `Task { let result = try await Type.init(...) }` — the implicit actor hop at the
// `await` lands the init on the actor's executor. The synchronous `new T(...)`
// projection remains skipped under SWIFTBIND022; the async factory is additive.
//
// The C# side never crosses the actor boundary directly — it only invokes a `@_cdecl`
// wrapper symbol and receives a Cdecl callback when the Task completes. That keeps
// the design NativeAOT-safe (prior approaches that used CallConvSwift directly to the
// actor-isolated init compiled and passed on Mono/sim but crashed on device).

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

/// A class isolated to the custom global actor with default-parameter init. The init
/// reaches C# as `static Task<GlobalActorIsolatedClass> CreateAsync(string, int)` —
/// the Swift wrapper schedules a `Task { try await GlobalActorIsolatedClass.init(...) }`
/// and the implicit actor hop at the await lands the init on the actor's executor.
/// `describe()` exercises an actor-isolated method on the same type, which the binding
/// surfaces as an async method as well.
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

/// Throwing init on a custom-global-actor-isolated class. Same async-factory pipeline
/// as `GlobalActorIsolatedClass`: the C# binding surfaces it as
/// `static Task<GlobalActorIsolatedThrowingClass> CreateAsync(int value, bool failIf)`,
/// and a Swift-side throw surfaces in C# as a faulted Task whose exception message
/// carries the Swift `String(describing: error)` text.
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

/// Configuration struct used as a non-C#-mappable default. The default expression
/// `GlobalActorConfig()` calls a Swift initializer — C# can't express that as an inline
/// parameter default, so the binding generator has to emit a trimmed `CreateAsync`
/// overload that lets Swift apply the default at the call site. Marked Sendable so it
/// can be passed across the actor isolation boundary in Swift 6.
public struct GlobalActorConfig: Sendable {
    public let depth: Int32
    public init(depth: Int32 = 7) {
        self.depth = depth
    }
}

/// Custom-global-actor-isolated class whose init has a non-C#-mappable default
/// (`config: GlobalActorConfig = GlobalActorConfig()`). Validates that the
/// default-parameter overload pipeline emits trimmed `CreateAsync` overloads for
/// actor-isolated async constructors when the trailing defaults can't fold into inline
/// C# parameter defaults. The class stores `config.depth` as a primitive so the test
/// can read back which default was applied without round-tripping the struct through
/// the boundary.
@BindingsTestGlobalActor
public class GlobalActorIsolatedDefaultArgClass {
    public let label: String
    public let depth: Int32

    public init(label: String, config: GlobalActorConfig = GlobalActorConfig()) {
        self.label = label
        self.depth = config.depth
    }
}
