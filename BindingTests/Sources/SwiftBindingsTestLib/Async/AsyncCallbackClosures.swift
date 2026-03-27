// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async MCB Callback Tests (R2 regression)

/// Exercises the Kingfisher CalculateDiskStorageSize pattern:
/// method with @escaping closure taking Result<T, Error> — triggers MCB bridge.
/// The Mono JIT assertion `!ji->async` fires when the MCB callback runs.
public class ResultCallbackProcessor {
    public init() {}

    /// Completion callback with Result<Int32, Error> — triggers MCB bridge pattern.
    public func processWithResult(completion: @escaping (Result<Int32, Error>) -> Void) {
        completion(.success(42))
    }

    /// Simulates Kingfisher's calculateDiskStorageSize — Result<UInt, Error>.
    public func calculateSize(completion: @escaping (Result<UInt, Error>) -> Void) {
        completion(.success(1024))
    }

    /// Multiple sequential calls — tests callback stability.
    public func processMultiple(count: Int32, completion: @escaping (Result<Int32, Error>) -> Void) {
        for i in 0..<count {
            completion(.success(i))
        }
    }
}
