// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Result<(), E> fixture — mirrors Kingfisher's CacheStoreResult shape where a
// side-effect completion reports success as Void and failure as a concrete Error.
//
// Before the fix, HasNonSwiftObjectGenericArg returned true for the empty-tuple
// Success arg (all tuples block emission under the ISwiftObject constraint),
// so the property and free-function variants were silently tombstoned.
// SwiftResult<TSuccess, TFailure> has no ISwiftObject constraint, so the
// projection handles marshalling; the gate just needs to let the member through.

import Foundation

/// Error enum used as the Failure type on Result<(), _> fixtures.
public enum StoreWriteError: Error {
    case diskFull
    case permissionDenied(path: String)
}

/// Struct carrying a `Swift.Result<(), StoreWriteError>` property. Mirrors
/// Kingfisher.CacheStoreResult.diskCacheResult: a cache-write outcome whose
/// success path is value-less and whose failure path carries a concrete error.
public struct CacheWriteOutcome {
    public let result: Result<(), StoreWriteError>

    public init(result: Result<(), StoreWriteError>) {
        self.result = result
    }

    /// Convenience factory: successful write (success = Void).
    public static func successfulWrite() -> CacheWriteOutcome {
        return CacheWriteOutcome(result: .success(()))
    }

    /// Convenience factory: failed write carrying a StoreWriteError.
    public static func failedWrite(_ error: StoreWriteError) -> CacheWriteOutcome {
        return CacheWriteOutcome(result: .failure(error))
    }
}

/// Free function returning `Result<(), StoreWriteError>` — verifies return-direction
/// projection separately from the property path.
public func makeCacheWriteResult(shouldSucceed: Bool) -> Result<(), StoreWriteError> {
    if shouldSucceed {
        return .success(())
    } else {
        return .failure(.diskFull)
    }
}
