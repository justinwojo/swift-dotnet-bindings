// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - @MainActor-Isolated Method on a Generic Struct (bug d)
//
// Factory/FactoryKit's exact repro shape: a generic container (`Factory<T>`-adjacent)
// with a `@MainActor`-isolated instance method (`resolveOnMainActor()`, 4 sites). ANY
// method on a generic STRUCT (not just ones referencing the parent's generic parameter)
// routes through `GenericDispatchEmitter`'s static-dispatch/type-erasure path
// (`GenericDispatchEmitter.NeedsStaticDispatch`: "All generic struct methods need
// static dispatch") to avoid a CallConvSwift crash on NativeAOT — a DIFFERENT emission
// path than the ordinary instance-method wrapper.
//
// Pre-fix, `MethodWrapperEmitter.EmitGenericStaticDispatchMethod` applied the
// `@MainActor` annotation ONLY to the @_cdecl entry point, not to the type-erasure
// dispatch shim's protocol requirement + witness (`static func dispatch(...)` on the
// synthesized `_SBW_P_*` protocol and its conformance extension) that the @_cdecl
// function calls through. Swift 6 requires the CALLER to share the isolation context of
// an isolated member — the shim's call site is nonisolated, so calling into the
// isolated witness through an unannotated protocol requirement is rejected at compile
// time. The fix threads `WrapperValidation.NeedsMainActorAnnotation` through to the
// protocol requirement and witness declarations as well.

/// Generic container whose instance methods are forced through the static-dispatch
/// path purely by being a generic struct (no generic-parameter-in-signature
/// requirement for this trigger).
public struct MainActorGenericContainer<T> {
    public let value: T
    public init(value: T) {
        self.value = value
    }

    /// `@MainActor`-isolated instance method on the generic struct — the exact shape
    /// that hit the missing-annotation gap on the type-erasure dispatch shim.
    @MainActor
    public func resolveOnMainActor() -> Int32 {
        return 1
    }
}

/// Factory so the C# binding can construct a closed `MainActorGenericContainer<Int32>`
/// without needing generic-constructor support in this fixture.
public func makeMainActorGenericContainer(seed: Int32) -> MainActorGenericContainer<Int32> {
    return MainActorGenericContainer(value: seed)
}
