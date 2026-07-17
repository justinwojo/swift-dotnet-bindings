// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Nested-Type-of-Own-Generic-Parent Returned From an Extension Property (bug f)
//
// Mirrors Time's real `ClockStrikes<U>` shape exactly: a generic struct's OWN
// extension (not a protocol-extension-default) declares a computed property whose
// return type is the host's OWN nested type, parameterized by the host's OWN generic
// parameter (`ClockStrikes<U>.Values`). The concrete-specialization engine
// (`ConcreteProtocolSpecializationEmitter`) substitutes the generic parameter
// throughout the synthesized concrete signature via `SubstituteTypeSpec`; pre-fix,
// `SubstituteTypeSpec` did not recurse into `NamedTypeSpec.InnerType`, so a nested-type
// reference collapsed to the bare outer generic (`ClockStrikes<Conformer>` instead of
// `ClockStrikes<Conformer>.Values`) and the synthesized wrapper referenced a
// non-existent flattened type — 9 compile errors in Time's real corpus repro.
//
// No async sibling: tried both an async computed property and a plain async func here
// and both hit gates unrelated to this bug. An async property is rejected outright —
// async CSM categorically excludes accessors (`method.IsAccessor`, checked in both the
// closed-conformer path in `ConcreteProtocolSpecializationEmitter.Async.cs` and the
// generic-parent path in `AsyncGenericParent.cs`) as a separate, pre-existing "Phase A
// scope" limitation. An async func instead reaches `TryEmitParentOnlyAsyncOverload`
// (`AsyncGenericParent.cs`), a narrower, hand-rolled path whose own doc comment scopes
// it tight to the shape `MusicLibraryRequest<T>.response()` needs and which doesn't
// substitute a nested-InnerType return — a third, independent gap, in a code path this
// bug's fix (`SubstituteTypeSpec`/`ResolvePublicCSharpType`) never touches. Neither is
// this bug; flagged for a future session rather than re-scoped in here.

/// Constraint protocol with two concrete conformers, giving the specializer something
/// to enumerate over.
public protocol NestedReturnUnit {
    var label: String { get }
}

@frozen
public struct NestedReturnSeconds: NestedReturnUnit {
    public init() {}
    public var label: String { "seconds" }
}

@frozen
public struct NestedReturnMinutes: NestedReturnUnit {
    public init() {}
    public var label: String { "minutes" }
}

/// Generic host struct with a nested `Values` type — the `ClockStrikes<U>` /
/// `ClockStrikes<U>.Values` shape.
@frozen
public struct NestedReturnHost<Unit: NestedReturnUnit> {
    public let seed: Int32
    public init(seed: Int32) { self.seed = seed }

    /// Nested type returned by the extension properties below, parameterized by the
    /// SAME generic parameter as the enclosing host. Deliberately no explicit `public
    /// init` — Swift only auto-synthesizes an `internal` memberwise initializer (never
    /// `public`), which keeps this type's OWN constructor off the generator's public-
    /// surface binding entirely and isolates the fixture to the CSM property-return
    /// path under test (the nested type's constructor-wrapper emission is an
    /// unrelated, pre-existing surface this fixture isn't targeting).
    ///
    /// Deliberately NOT `@frozen`: a `@frozen` nested type routes its own property
    /// getter through the generator's "frozen struct, value-semantics self" P/Invoke
    /// path (`PInvokeEmitter.GetResolvedParentTypeName`), which resolves the self type
    /// to the type's bare last-dot-segment name (`Values`) — correct only when the
    /// P/Invoke static class is C#-nested inside its enclosing type. It never is (the
    /// generator always emits `{Host}_{Nested}_PInvoke` at namespace scope), so a
    /// `@frozen` nested type's own getter binds `SwiftSelf<Values>` where `Values`
    /// doesn't resolve (CS0246) — a real, separate generator gap from bug (f)'s CSM
    /// return-type issue this fixture targets. Non-frozen routes through the untyped
    /// `SwiftSelf` + opaque-payload path instead, which has no such qualification
    /// concern, so this fixture isolates cleanly to the CSM path under test.
    public struct Values {
        public let count: Int32
    }
}

extension NestedReturnHost {
    /// Sync computed property returning `NestedReturnHost<Unit>.Values` — the exact
    /// nested-of-own-generic-parent return shape that collapsed to the bare outer
    /// generic pre-fix.
    public var values: NestedReturnHost<Unit>.Values {
        return NestedReturnHost<Unit>.Values(count: seed)
    }
}

/// Factory returning a `Seconds`-parameterized host, so the concrete specializer has
/// a `NestedReturnHost<NestedReturnSeconds>` conformer to specialize `values` over.
public func makeNestedReturnHostSeconds(seed: Int32) -> NestedReturnHost<NestedReturnSeconds> {
    return NestedReturnHost(seed: seed)
}

/// Second conformer factory — Time's repro specializes over multiple conformers
/// (Nanosecond/Second/Minute/Hour/etc.), so a single-conformer fixture wouldn't
/// exercise the per-conformer substitution loop the same way.
public func makeNestedReturnHostMinutes(seed: Int32) -> NestedReturnHost<NestedReturnMinutes> {
    return NestedReturnHost(seed: seed)
}
