// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Unsupported closure shapes (closure-parameter skip renders APIs unreachable)
//
// These shapes are canonical adversarial closure signatures that each hit a
// distinct rejection branch in `ClosureHandler.IsSupportedClosure` /
// `IsSupportedClosureReturnType`; together they pin the *current* state of
// `// Unsupported: ... closure signature not yet supported` markers so any future fix
// is forced to ratchet `build/baselines/skip-surface-baseline.json` downward in the same commit.
//
// Shape catalog:
//   OptionalExistentialReturn   — closure return is `(any Error)?`
//   AsyncThrowingClosureParam   — async+throwing closure parameter
//   ArrayOfExistentialReturn    — closure returning `[any P]`
//   SendableOptionalExistential — optional existential with @Sendable
//   AsyncVoidReturn             — `@escaping (Args) async -> Void` (not baseline-async)
//
// All five shapes degrade today. Fixing each is a separate Closure-handler session
// (per-shape evidence + indirect-return marshalling). Layer B's job here is to keep
// the count visible.

public protocol UnsupportedClosureSignal {
    func describe() -> String
}

public struct UnsupportedClosureSignalDefault: UnsupportedClosureSignal {
    public init() {}
    public func describe() -> String { return "default" }
}

public struct UnsupportedClosureRequest {
    public let id: Int32
    public init(id: Int32) { self.id = id }
}

public enum UnsupportedClosureFault: Error {
    case generic
}

/// Shape (1): closure return is `Optional<any Protocol>`.
/// Rejected because `IsSupportedClosureReturnType` recurses into the bound generic
/// parameter and `_existentialHandler.IsExistential(genericParam)` returns true → bail.
public class UnsupportedClosureOptionalExistentialReturn {
    public init() {}

    public func validate(
        _ check: @escaping (UnsupportedClosureRequest) -> (any UnsupportedClosureSignal)?
    ) -> Bool {
        return check(UnsupportedClosureRequest(id: 1)) != nil
    }
}

/// Shape (2): closure return is `[any Protocol]`. Rejected through the same
/// Optional<existential> branch — Array's element is an existential generic parameter.
public class UnsupportedClosureArrayOfExistentialReturn {
    public init() {}

    public func enumerate(
        _ collect: @escaping (UnsupportedClosureRequest) -> [any UnsupportedClosureSignal]
    ) -> Int32 {
        return Int32(collect(UnsupportedClosureRequest(id: 2)).count)
    }
}

/// Shape (3): @Sendable closure with Optional<any Protocol> return.
/// `@Sendable` is already a no-op for marshalling (ClosureHandler.IsSendable detects
/// but doesn't reject); the rejection still fires on the Optional<existential> return.
/// Pinning this distinct fixture documents that fixing the existential-return path
/// MUST also work when @Sendable is present.
public class UnsupportedClosureSendableOptionalExistential {
    public init() {}

    public func install(
        _ validator: @escaping @Sendable (UnsupportedClosureRequest) -> (any UnsupportedClosureSignal)?
    ) -> Bool {
        return validator(UnsupportedClosureRequest(id: 3)) != nil
    }
}

/// Shape (4): async-throwing closure parameter (UnsupportedSignature: "Async-throwing
/// closure parameter cannot be bridged"). The async wrapper's
/// withCheckedThrowingContinuation harness handles return-side async-throws but the
/// parameter-side bridge isn't wired — closure-arg async-throws signatures fall back
/// to the generic skip path.
public class UnsupportedClosureAsyncThrowingParam {
    public init() {}

    public func runRequest(
        _ work: @escaping (UnsupportedClosureRequest) async throws -> Int32
    ) async -> Int32 {
        do {
            return try await work(UnsupportedClosureRequest(id: 4))
        } catch {
            return -1
        }
    }
}

/// Shape (5): `@escaping (Args) async -> Void` — an async closure with a `Void`
/// return. This is NOT a baseline-async closure: the baseline-async bridge keys
/// off a non-void blittable return that `withCheckedContinuation` can carry back,
/// so a `-> Void` async closure has nothing to resume with and must be rejected by
/// `IsSupportedClosure`. Before the fix it slipped past the async guard (the guard
/// only rejected non-void async-non-throwing closures), routed to the legacy
/// escaping-closure path, and emitted an undeclared `handlerBox` (CS0103) plus a
/// callback that silently discarded the returned `Task`. Mirrors the real-world
/// `YouTubePlayerKit.OpenURLAction.init(handler: @escaping (URL, YouTubePlayer) async -> Void)`
/// that surfaced this under `nuke validate`. Both the constructor and the method
/// forms are pinned: the constructor reaches the closure-param skip gate through a
/// different early-return than an instance method, and the constructor form is the
/// exact shape that regressed.
public class UnsupportedClosureAsyncVoidReturn {
    private var onOpen: ((UnsupportedClosureRequest) async -> Void)?
    private var deferred: ((UnsupportedClosureRequest, UnsupportedClosureSignalDefault) async -> Void)?

    // Reachable constructor — keeps the emitted type instantiable so the skip of
    // the async-void constructor below is observable as "member dropped", not
    // "whole type unreachable".
    public init() {}

    // Async-void closure constructor parameter — skipped (single-arg form,
    // YouTubePlayerKit OpenURLAction.init(handler:) shape).
    public init(handler: @escaping (UnsupportedClosureRequest) async -> Void) {
        self.onOpen = handler
    }

    // Async-void closure method parameter — skipped (multi-arg form).
    public func onChange(
        _ handler: @escaping (UnsupportedClosureRequest, UnsupportedClosureSignalDefault) async -> Void
    ) {
        self.deferred = handler
    }
}
