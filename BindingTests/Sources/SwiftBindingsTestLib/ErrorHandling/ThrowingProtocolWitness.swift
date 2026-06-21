// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// F42 forward-throw carriage through the witness-dispatch protocol proxy. A Swift type conforms to a
// protocol with a SYNC `throws` requirement; C# obtains it as the existential `IThrowingWitness` and
// calls the requirement through the generated `ThrowingWitnessProxy`, whose error path routes through
// `SwiftMarshal.ThrowSwiftError` so the surfaced `SwiftException` carries the live error box on
// `.ErrorHandle` — identical to the canonical free-function/method throw path.

/// Error surfaced by the throwing witness requirement when given a negative input.
public enum ThrowingWitnessError: Error {
    case negative
}

/// Protocol with a sync `throws` requirement — drives the witness-dispatch proxy's throwing path.
public protocol ThrowingWitness {
    func tagOrThrow(_ value: Int32) throws -> Int32
}

final class ThrowingWitnessConformer: ThrowingWitness {
    func tagOrThrow(_ value: Int32) throws -> Int32 {
        if value < 0 {
            throw ThrowingWitnessError.negative
        }
        return value &+ 1
    }
}

/// Vends the conformer as an existential so C# wraps it in the generated `ThrowingWitnessProxy` and
/// dispatches `tagOrThrow` through the witness table.
public func makeThrowingWitness() -> any ThrowingWitness {
    ThrowingWitnessConformer()
}
