// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Existential-return ARC leak fixtures
//
// Swift functions returning `any P` / `(any P)?` transfer the existential at +1
// (the C# caller owns the release obligation). The generated marshalling reads the
// container out of the return slot and constructs a wrapper — a `{Protocol}Proxy`
// for opaque existentials, or the `AnyError` value struct for `any Error`. These
// fixtures embed a `LifetimeTracker`-counted class inside the existential so a
// missing release surfaces as a non-zero live count after the wrapper is disposed
// and the GC has drained, rather than merely "does not crash".

/// `LifetimeTracker`-counted CLASS conforming to `Renderable` (declared in
/// Protocols/OptionalExistentialProperties.swift). Returned as `(any Renderable)?`
/// / `any Renderable`, it flows through the opaque 5-word existential container and
/// is wrapped in the generated `RenderableProxy`. A class conformer stores its
/// reference INLINE in the container's first payload word, so a value-witness
/// Destroy of the container (via the existential's own metadata) ARC-releases this
/// instance. If the proxy adopts the container without ever destroying it, this
/// instance never deinits → leak.
public final class TrackedRenderable: Renderable {
    public let tag: Int32

    public init(tag: Int32) {
        self.tag = tag
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }

    public func render() -> String { "TrackedRenderable(\(tag))" }
}

/// Returns `(any Renderable)?` wrapping a freshly-allocated tracked class (or nil).
/// Drives the indirect (sret) `Optional<existential>` return → `RenderableProxy`
/// Swift-backed construction in `OptionalProjection`.
public func makeTrackedRenderableOptional(_ produce: Bool, tag: Int32) -> (any Renderable)? {
    return produce ? TrackedRenderable(tag: tag) : nil
}

/// Returns a non-optional `any Renderable` wrapping a tracked class. Drives the
/// `ExistentialProjection.GetReturnPlan` proxy-construction path (no Optional
/// wrapper) — the same adopt-without-release shape as the optional path.
public func makeTrackedRenderable(tag: Int32) -> any Renderable {
    return TrackedRenderable(tag: tag)
}

/// Returns a `RenderableHolder` (declared in Protocols/OptionalExistentialProperties.swift)
/// whose `primary: (any Renderable)?` stored property holds a tracked class conformer. This
/// drives the decomposed optional-existential PROPERTY GETTER — a DISTINCT owned-return
/// emission mechanism from the standalone functions above. The getter reads the existential
/// out of the decomposed `(payload, hasValue)` buffer at +1 (Swift's getter returns owned),
/// then frees that buffer, so the wrapping proxy is the sole surviving retain and must release
/// on Dispose/finalize. Each `holder.primary` read therefore lays down a fresh +1 on the SAME
/// tracked instance; if the getter's proxy does not adopt/release, those +1s outlive the
/// holder and the instance never deinits.
public func makeTrackedRenderableHolder(tag: Int32) -> RenderableHolder {
    return RenderableHolder(primary: TrackedRenderable(tag: tag))
}

/// Value-type (struct) conformer whose stored `TrackedRef` (declared in
/// MemoryManagement/LeakDetection.swift) is `LifetimeTracker`-counted. Five refs
/// push the struct past the 3-word inline buffer of an opaque existential, so it is
/// stored BOXED inside `any Renderable`. Destroying the container therefore must go
/// through the existential's value-witness table (which releases the box), not a
/// bare release of the first payload word — this fixture guards that the proxy
/// release path handles the boxed-payload case, not only inline class refs.
public struct BoxedTrackedRenderable: Renderable {
    public let a: TrackedRef
    public let b: TrackedRef
    public let c: TrackedRef
    public let d: TrackedRef
    public let e: TrackedRef

    public init(tag: Int32) {
        self.a = TrackedRef(tag: tag)
        self.b = TrackedRef(tag: tag)
        self.c = TrackedRef(tag: tag)
        self.d = TrackedRef(tag: tag)
        self.e = TrackedRef(tag: tag)
    }

    public func render() -> String { "BoxedTrackedRenderable(\(a.tag))" }
}

/// Returns `(any Renderable)?` wrapping a BOXED value-type conformer (five embedded
/// `TrackedRef`s). Drives the proxy release path for a boxed existential payload.
public func makeBoxedTrackedRenderableOptional(_ produce: Bool, tag: Int32) -> (any Renderable)? {
    return produce ? BoxedTrackedRenderable(tag: tag) : nil
}

/// `LifetimeTracker`-counted CLASS conforming to `Error`. Returned as `(any Error)?`,
/// it is a 1-word class-bound existential (a retained reference) wrapped by the
/// generated marshalling in the `AnyError` value struct. `AnyError` is blittable and
/// passed by value with no release path, so this instance never deinits → leak. This
/// fixture exists to MEASURE that leak; the `AnyError` value-struct cannot own a
/// deterministic-release +1 across bitwise copies, so the fix is an ownership-model
/// decision rather than a localized projection change.
public final class TrackedError: Error {
    public let tag: Int32

    public init(tag: Int32) {
        self.tag = tag
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// Returns `(any Error)?` wrapping a freshly-allocated tracked class (or nil).
public func makeTrackedErrorOptional(_ produce: Bool, tag: Int32) -> (any Error)? {
    return produce ? TrackedError(tag: tag) : nil
}
