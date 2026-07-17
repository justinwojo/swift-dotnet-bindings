// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - Mutating-Get-Only Property on a Generic Struct (bug g)
//
// Resolver's exact repro shape (2 sites): a generic struct-backed lazy/memoized
// property with a `mutating get`. Because the parent is GENERIC, its getter wrapper
// threads metadata/PWT params and is emitted by `PropertyWrapperEmitter`'s GENERIC
// static-dispatch path (`EmitGenericStaticGetterWrapper`) — a DIFFERENT code path than
// both the non-generic getter wrapper (which already accommodated mutating getters)
// and the protocol witness-dispatch path
// (`Protocols/MutatingGetterOnlyProtocol.swift`, `WitnessDispatchEmitter`). Pre-fix,
// this generic path unconditionally bound the reconstructed receiver as
// `let obj = selfPtr...pointee`; calling a `mutating get` on an immutable `let`
// binding is a swiftc compile error ("cannot use mutating getter on immutable value
// of type '...'"). The fix binds `var obj` when the property's getter is mutating (or
// a setter exists), mirroring the pre-existing non-generic-path accommodation.

/// Generic struct with a `mutating get`-only computed property. The generic
/// parameter and its backing property are named `Element`/`element` (not
/// `Payload`/`payload`) deliberately — a `ClassWithOpaquePayload` projection already
/// reserves a `Payload` member for its native-handle accessor, and naming a Swift
/// property `payload` would collide with that reserved name (CS0102), an unrelated
/// naming concern this fixture isn't targeting.
public struct MutatingGetterOnlyGenericCounter<Element> {
    private var calls: Int32
    public let element: Element

    public init(element: Element, initial: Int32 = 0) {
        self.element = element
        self.calls = initial
    }

    /// Memoized: each read increments the call counter, requiring a `mutating get`.
    public var snapshot: Int32 {
        mutating get {
            calls += 1
            return calls
        }
    }
}

/// Factory so the generic parent is closed over a concrete `Element` (`Int32`) —
/// the metadata/PWT-threading getter wrapper needs a resolvable generic argument.
public func makeMutatingGetterOnlyGenericCounter(seed: Int32) -> MutatingGetterOnlyGenericCounter<Int32> {
    return MutatingGetterOnlyGenericCounter(element: seed, initial: 0)
}
