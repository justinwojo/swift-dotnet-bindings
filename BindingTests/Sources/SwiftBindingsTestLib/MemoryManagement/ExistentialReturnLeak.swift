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
/// it is a 1-word class-bound existential (a retained reference) wrapped by the generated
/// marshalling in the `AnyError` reference type. On a Swift→C# owned transfer the `AnyError`
/// adopts the box's +1 (`ownsContainer: true`) and releases it on Dispose/finalize, so this
/// instance deinits once the wrapper is disposed and the GC drains.
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

/// Returns a non-optional `any Error` wrapping a tracked class. Drives the direct
/// (non-optional) existential-return projection (`ExistentialProjection.GetReturnPlan` well-known
/// branch) — a DISTINCT owned-return emission mechanism from the `(any Error)?` optional path
/// above (which routes through `OptionalProjection`). The returned existential transfers at +1,
/// so the wrapping `AnyError` must adopt the box and release it on Dispose/finalize.
public func makeTrackedError(tag: Int32) -> any Error {
    return TrackedError(tag: tag)
}

/// Named enum carrying `any Error` in an associated value — the `Result`-shaped failure
/// surface. The generated `TryGetFailed(out AnyError)` accessor extracts the payload by
/// value-witness-copying the whole enum (retaining the boxed error at +1) into a buffer it
/// never destroys, then wrapping the box pointer in `AnyError`. Each extraction therefore
/// hands the consumer a fresh +1 that the wrapper must release on Dispose, distinct from the
/// enum's own stored +1. This is a different emission mechanism from the standalone
/// `(any Error)?` return above (enum-payload extraction, not a direct return slot).
public enum TrackedErrorBox {
    case empty
    case failed(any Error)
}

/// Returns a `.failed` carrying a freshly-allocated tracked error. The enum owns one +1 on
/// the box; each `TryGetFailed` extraction lays down an additional +1.
public func makeTrackedErrorBoxFailure(tag: Int32) -> TrackedErrorBox {
    return .failed(TrackedError(tag: tag))
}

/// Named enum carrying a NON-Error opaque existential (`any Renderable`) in an associated value —
/// the proxy-extraction analogue of `TrackedErrorBox`. Where `any Error` projects to the well-known
/// `AnyError`, `any Renderable` projects to the generated `RenderableProxy`, so this exercises a
/// DISTINCT marshalling branch (`EnumHandler.Marshalling.cs` proxy path, not the well-known path).
/// The generated `TryGetShown(...)` value-witness-copies the whole enum (retaining the boxed
/// conformer at +1) into a buffer it never destroys, then wraps the container in the proxy. Each
/// extraction lays a fresh +1 the proxy must adopt (`ownsContainer: true`) and release on Dispose,
/// distinct from the enum's own stored +1.
public enum TrackedRenderableBox {
    case empty
    case shown(any Renderable)
}

/// Returns a `.shown` carrying a freshly-allocated tracked renderable. The enum owns one +1 on the
/// existential; each `TryGetShown` extraction lays down an additional +1 the proxy must release.
public func makeTrackedRenderableBoxShown(tag: Int32) -> TrackedRenderableBox {
    return .shown(TrackedRenderable(tag: tag))
}

/// `LifetimeTracker`-counted CLASS conforming to BOTH `Nameable` and `Ageable` (declared in
/// Protocols/Composition.swift). Returned as `any Nameable & Ageable`, it flows through an
/// EC2 COMPOSITION existential container — two witness-table words, a 3-word inline value
/// buffer, and a metadata word. The conforming value is a single class instance regardless
/// of protocol count (the extra protocol only adds a witness table), stored INLINE in the
/// container's first payload word. A value-witness Destroy of the EC2 container — via the
/// existential's own metadata, resolved by protocol count — ARC-releases this one instance.
/// Before the EC2+ ownership fix the generated composition proxy adopted the container with
/// an empty `Dispose()`, orphaning the payload's +1.
public final class TrackedNameableAgeable: Nameable, Ageable {
    private let nameValue: String
    private let ageValue: Int32

    public var name: String { nameValue }
    public var age: Int32 { ageValue }

    public init(tag: Int32) {
        self.nameValue = "Tracked\(tag)"
        self.ageValue = tag
        recordTrackedAllocation()
    }

    deinit {
        recordTrackedDeallocation()
    }
}

/// Returns `any Nameable & Ageable` (an EC2 composition existential) wrapping a tracked class.
/// Drives the composition-proxy owned-return path (`ExistentialProjection.GetReturnPlan` →
/// `new AgeableAndNameableProxy(...)`). The proxy must adopt the container at +1 and release
/// it on Dispose/finalize, mirroring the single-protocol (EC1) ownership mechanism.
public func makeTrackedNameableAgeable(tag: Int32) -> any Nameable & Ageable {
    return TrackedNameableAgeable(tag: tag)
}

/// Returns `(any Nameable & Ageable)?` wrapping a tracked class (or nil). Drives the
/// decomposed OPTIONAL composition-existential return — a distinct owned-return emission
/// site from the non-optional path, also routed through the EC2 composition proxy.
public func makeTrackedNameableAgeableOptional(_ produce: Bool, tag: Int32) -> (any Nameable & Ageable)? {
    return produce ? TrackedNameableAgeable(tag: tag) : nil
}

/// Value-type (struct) conformer to BOTH `Nameable` and `Ageable` whose five stored
/// `TrackedRef`s push it past the 3-word inline buffer of an EC2 composition existential, so
/// it is stored BOXED — the container's first payload word holds a pointer to the heap box.
/// Disposing the composition proxy must release the EC2 container through its value-witness
/// table (which releases the box and its five embedded refs), NOT a bare release of the first
/// payload word. This guards that the EC2+ release path handles the boxed-payload case, not
/// only inline class refs — the inline-vs-boxed distinction lives in the existential's own VWT
/// (driven by the payload's metadata), independent of the witness-table word count.
public struct BoxedTrackedNameableAgeable: Nameable, Ageable {
    private let a: TrackedRef
    private let b: TrackedRef
    private let c: TrackedRef
    private let d: TrackedRef
    private let e: TrackedRef

    public var name: String { "Boxed\(a.tag)" }
    public var age: Int32 { a.tag }

    public init(tag: Int32) {
        self.a = TrackedRef(tag: tag)
        self.b = TrackedRef(tag: tag)
        self.c = TrackedRef(tag: tag)
        self.d = TrackedRef(tag: tag)
        self.e = TrackedRef(tag: tag)
    }
}

/// Returns `any Nameable & Ageable` (EC2) wrapping a BOXED value-type conformer (five embedded
/// `TrackedRef`s). Drives the composition-proxy owned-return release path for a boxed payload.
public func makeBoxedTrackedNameableAgeable(tag: Int32) -> any Nameable & Ageable {
    return BoxedTrackedNameableAgeable(tag: tag)
}
