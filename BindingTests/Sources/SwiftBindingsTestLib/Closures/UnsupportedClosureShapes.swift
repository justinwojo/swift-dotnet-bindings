// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Unsupported closure shapes (Bug 0.10.0 — gap-closure-parameter-skip-renders-apis-unreachable)
//
// These shapes are the canonical adversarial closure signatures the Nuke / Lottie /
// StoreKit2 / MusicKit / WeatherKit consumer audits surfaced. Each pattern hits a
// distinct rejection branch in `ClosureHandler.IsSupportedClosure` /
// `IsSupportedClosureReturnType`; together they pin the *current* state of
// `// Unsupported: ... closure signature not yet supported` markers so any future fix
// is forced to ratchet `build/baselines/skip-surface-baseline.json` downward in the same commit.
//
// Mapping to consumer-library sites:
//   Shape OptionalExistentialReturn  →  Nuke `DataLoader.init(validate: …(URLResponse) -> (any Error)?)`
//                                       Lottie `LottieLogger.init(_ logger: (…))` (similar shape family)
//   Shape AsyncThrowingClosureParam   →  Nuke `ImageRequest.init` (UnsupportedSignature → same family)
//   Shape ArrayOfExistentialReturn    →  StoreKit2 `Status.all` style (closure returning [any P])
//   Shape SendableOptionalExistential →  Nuke `DataLoader.init` exact signature, with @Sendable
//
// All four shapes degrade today. Fixing each is a separate Closure-handler session
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

/// Shape (1): closure return is `Optional<any Protocol>`. Mirrors Nuke
/// `DataLoader.validate: (URLResponse) -> (any Error)?`. Rejected today because
/// `IsSupportedClosureReturnType` recurses into the bound generic parameter and
/// `_existentialHandler.IsExistential(genericParam)` returns true → bail.
public class UnsupportedClosureOptionalExistentialReturn {
    public init() {}

    public func validate(
        _ check: @escaping (UnsupportedClosureRequest) -> (any UnsupportedClosureSignal)?
    ) -> Bool {
        return check(UnsupportedClosureRequest(id: 1)) != nil
    }
}

/// Shape (2): closure return is `[any Protocol]`. Mirrors StoreKit2
/// `currentEntitlements` / `Status.all` style enumerations. Rejected through the
/// same Optional<existential> branch — Array's element is an existential generic
/// parameter.
public class UnsupportedClosureArrayOfExistentialReturn {
    public init() {}

    public func enumerate(
        _ collect: @escaping (UnsupportedClosureRequest) -> [any UnsupportedClosureSignal]
    ) -> Int32 {
        return Int32(collect(UnsupportedClosureRequest(id: 2)).count)
    }
}

/// Shape (3): @Sendable closure with Optional<any Protocol> return — exact Nuke
/// `DataLoader.init(validate: @escaping @Sendable (URLResponse) -> (any Error)?)` shape.
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

/// Shape (4): async-throwing closure parameter. Mirrors Nuke `ImageRequest.init`
/// (UnsupportedSignature, "Async-throwing closure parameter cannot be bridged").
/// The async wrapper's withCheckedThrowingContinuation harness handles return-side
/// async-throws but the parameter-side bridge isn't wired — closure-arg async-throws
/// signatures fall back to the generic skip path.
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
