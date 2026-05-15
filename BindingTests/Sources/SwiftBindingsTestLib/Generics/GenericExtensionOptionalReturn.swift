// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - GenericExtensionOptionalReturn — S3-B fixture (round 2)
//
// Round 2 regression lock for ObjectMapper's `Mapper<N>.map(...) -> N?` shape.
// Round 1 (R1-S5, commit d3543268) added selector-aware emit of @_cdecl wrappers
// for distinct-selector siblings on generic-extension methods. The new fixture
// for that round, `GenericIndexableCollection`, only exercised methods with
// non-optional concrete returns. The MusicKit cells passed, but the same
// `_SBW_GSM_<hash>` emit path then mis-renders an Optional return inside the
// protocol-based static dispatch extension body when the inner type references
// a parent generic param — the wrapper compile fails because the inferred
// `let result: N = obj.method(...)` declaration drops the Optional wrap that
// the dispatched method actually returns.
//
// Real-world signal: ObjectMapper's `Mapper<N: BaseMappable>` has a family of
// `map(...) -> N?` overloads. The downstream sweep at 0.11.0 produced
// `error: value of optional type 'N?' must be unwrapped to a value of type 'N'`
// on the generated Swift wrapper.
//
// The fixture below covers every adjacent shape the same emit path is
// responsible for — straight `T?`, sibling non-optional control, `[T]?`,
// and `T?` with a non-optional sibling on the same method name (overload
// disambiguation). Each of these round-trips end-to-end from C#.
//
// `mapAsyncThrowing` is included as a **negative-coverage anchor**: today
// the generator marks it Unsupported ("Async callback references parent
// generic type parameters in return type"), so no @_cdecl wrapper is
// emitted and no C# binding exists to call. When that gate eventually
// lifts, a runtime test should be added alongside.

@frozen
public struct GenericOptionalReturnElement {
    public let tag: String
    public init(tag: String) { self.tag = tag }
}

public final class GenericExtensionOptionalReturnMapper<N> {
    // Internal pre-built value used by the maps below to construct an N for
    // a positive test (so we don't need to construct an arbitrary N inside
    // the generic body).
    private let storedValue: N?

    public init(storedValue: N?) {
        self.storedValue = storedValue
    }

    // Maximum case 1 — the exact ObjectMapper shape: generic-parent class
    // method whose return type is Optional<N>. The pre-fix wrapper emits
    // `let result: N = obj.map(...)` and the Swift compile fails.
    public func map(returnNil: Bool) -> N? {
        return returnNil ? nil : storedValue
    }

    // Negative-case sibling: same emit path, non-optional return. Must
    // continue to compile after the fix — the predicate must not over-fire.
    public func mapRequired() -> N {
        // Crash if mis-used; we always pass a non-nil storedValue when
        // round-tripping this method from C#.
        return storedValue!
    }

    // Maximum case 2 — Optional<Array<N>> return shape (`[N]?`). Outer
    // Optional with one-level Array<N> inside; exercises the
    // IsArrayOfParentGeneric branch of IsBareOrSimplyParameterizedNamedTypeSpec.
    public func mapArrayOptional(returnNil: Bool) -> [N]? {
        return returnNil ? nil : (storedValue.map { [$0] } ?? [])
    }

    // Negative-coverage anchor — async throws T?. Generator currently marks
    // this Unsupported (parent-generic-typed async return); no @_cdecl
    // wrapper is emitted today. Kept here so the fixture documents the
    // expected future-coverage shape — when the generator gate lifts, a
    // runtime test should land alongside.
    public func mapAsyncThrowing(returnNil: Bool) async throws -> N? {
        if returnNil { throw GenericExtensionMapperError.requested }
        return storedValue
    }

    // Maximum case 4 — overload-disambiguation. Two `lookup` overloads
    // share the base name but differ in label AND optional-ness of return.
    // Round 1's selector-aware fix must keep both wrappers; round 2's fix
    // must render the Optional return on the first without affecting the
    // non-optional sibling.
    public func lookup(byOptional flag: Bool) -> N? {
        return flag ? storedValue : nil
    }

    public func lookup(byRequired flag: Bool) -> N {
        return storedValue!
    }
}

public enum GenericExtensionMapperError: Error {
    case requested
}

// Factory used by C# tests to construct the generic mapper without naming
// the type parameter on the binding side (the bound generic is materialized
// inside Swift via this entry point).
public func makeGenericExtensionOptionalReturnMapper(
    tag: String?
) -> GenericExtensionOptionalReturnMapper<GenericOptionalReturnElement> {
    if let tag {
        return GenericExtensionOptionalReturnMapper<GenericOptionalReturnElement>(
            storedValue: GenericOptionalReturnElement(tag: tag))
    }
    return GenericExtensionOptionalReturnMapper<GenericOptionalReturnElement>(
        storedValue: nil)
}
