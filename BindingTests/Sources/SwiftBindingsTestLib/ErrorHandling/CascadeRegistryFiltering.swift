// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Cascade-registry filtering fixtures
//
// Regression fixtures for the cascade dispatcher's registry filter
// (`ErrorEnumRegistryEmitter.IsRegisterable`). Three shapes:
//
// 1. `@_spi` Error type — must be skipped from the registry. Otherwise the
//    cascade dispatcher emits `case N: { global::Module.SpiError _typed = ... }`
//    referencing a type the C# emitter never produced, which fails to compile
//    (CS0234). At runtime, throwing the SPI type from a public throwing function
//    must surface as the untyped `SwiftException` (not `SwiftException<TError>`).
//
// 2. Error type nested inside an open-generic parent — must be skipped from the
//    registry. The dispatcher renders module-qualified names verbatim, so a
//    `Outer<T>.Inner` reference would emit as `global::Module.Outer.Inner`
//    (CS0305 "using the generic type 'Outer<T>' requires 1 type argument").
//    No runtime arm needed beyond compile-time validation; the fixture just
//    needs to exist so generation reaches the registry filter.
//
// 3. `@usableFromInline internal` Error type — must be skipped from the registry.
//    The C# emitter DOES bind these because they appear in `@inlinable` signatures,
//    but the cascade dispatcher sits in a separate Swift wrapper module whose plain
//    `import {Module}` only resolves `public` declarations. Emitting
//    `as? Module.InternalType` in the cascade therefore produces "module X has no
//    member named Y" at the swift compile step (the failure mode that surfaced
//    before the IsModuleInternal filter landed). Runtime arm asserts the throw lands
//    on the bare-SwiftException fallthrough — same shape as the SPI test.

// 1. @_spi SPI-only Error enum — invisible to public consumers. The whole
//    declaration is annotated `@_spi(_)` so it is stripped from the
//    swiftinterface public surface; HandleBaseDecl skips it from C# emission.
//    The cascade-registry filter must catch the same case.
@_spi(InternalCascade)
public enum SpiOnlyCascadeError: Error {
    case unauthorized
    case notProvisioned
}

// Public throwing function whose body raises the SPI error. The wrapper layer
// catches `any Error` and runs the cascade — the cascade has no arm for
// `SpiOnlyCascadeError` (it was filtered out), so it falls through to id 0
// and the C# side observes a bare `SwiftException` with the Swift error
// description preserved.
public func plainThrowsAsyncSpiCascadeFallthrough() async throws -> Int32 {
    try? await Task.sleep(nanoseconds: 1_000_000)
    throw SpiOnlyCascadeError.unauthorized
}

// 2. Error type nested inside an open-generic parent. The parent struct is
//    public and generic; the nested enum conforms to Error. Without the
//    cascade-registry generic-parent filter, the C# side would try to emit
//    `global::SwiftBindingsTestLib.GenericCascadeOuter.NestedError` and fail
//    with CS0305 because GenericCascadeOuter requires a type argument.
public struct GenericCascadeOuter<TValue> {
    public let value: TValue

    public init(value: TValue) {
        self.value = value
    }

    public enum NestedError: Error {
        case missingValue
        case validationFailed(reason: String)
    }
}

// Open-generic Error-conforming struct itself (not nested) — the
// `typeDecl.IsGeneric` arm of the registry filter. Same CS0305 outcome
// without the filter. Field is named `data` (not `payload`) to avoid
// collision with the emitter's internal `_payload` SafeHandle exposure.
public struct GenericCascadeError<TPayload>: Error {
    public let data: TPayload

    public init(data: TPayload) {
        self.data = data
    }
}

// 3. `@usableFromInline internal` Error enum. The C# emitter binds it (so it can
//    be referenced from inlined signatures), but the cascade dispatcher's plain
//    `import` cannot name it — the same wrapper-visibility asymmetry that triggers
//    the IsModuleInternal filter.
@usableFromInline
internal enum InlinableInternalCascadeError: Error {
    case unauthorized
    case timedOut
}

// Public throwing function whose body raises the internal error. Public
// signature reaches consumers; the `Error` upcast at the throw site means the
// public surface never names the internal type. Without the IsModuleInternal
// filter, the cascade dispatcher would emit
// `as? SwiftBindingsTestLib.InlinableInternalCascadeError` and the swift
// wrapper compile would fail with "no type named InlinableInternalCascadeError
// in module SwiftBindingsTestLib".
public func plainThrowsAsyncInlinableInternalCascadeFallthrough() async throws -> Int32 {
    try? await Task.sleep(nanoseconds: 1_000_000)
    throw InlinableInternalCascadeError.unauthorized
}
