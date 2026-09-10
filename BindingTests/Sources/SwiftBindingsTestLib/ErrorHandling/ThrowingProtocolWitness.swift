// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// F42 forward-throw carriage through the witness-dispatch protocol proxy. A Swift type conforms to a
// protocol with a SYNC `throws` requirement; C# obtains it as the existential `IThrowingWitness` and
// calls the requirement through the generated `ThrowingWitnessProxy`, whose error path routes through
// the module's synchronous error classifier, so the surfaced exception is `SwiftException<TError>`
// for a registered error type and carries the live error box on `.ErrorHandle` — identical to the
// canonical free-function/method throw path.

/// Error surfaced by the throwing witness requirement when given a negative input.
public enum ThrowingWitnessError: Error {
    case negative
}

/// Protocol with sync `throws` requirements — drives the witness-dispatch proxy's throwing path.
public protocol ThrowingWitness {
    func tagOrThrow(_ value: Int32) throws -> Int32

    /// Always throws a class error embedding a `LifetimeTracker`-counted ref, so a proxy throw
    /// that leaks or double-releases the Swift error box is observable rather than merely "did
    /// not crash".
    func trackedThrow(_ code: Int32) throws -> Int32
}

final class ThrowingWitnessConformer: ThrowingWitness {
    func tagOrThrow(_ value: Int32) throws -> Int32 {
        if value < 0 {
            throw ThrowingWitnessError.negative
        }
        return value &+ 1
    }

    func trackedThrow(_ code: Int32) throws -> Int32 {
        throw SyncCascadeTrackedClassError(code: code)
    }
}

/// Vends the conformer as an existential so C# wraps it in the generated `ThrowingWitnessProxy` and
/// dispatches `tagOrThrow` through the witness table.
public func makeThrowingWitness() -> any ThrowingWitness {
    ThrowingWitnessConformer()
}
