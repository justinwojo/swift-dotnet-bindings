// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - MCB Struct Self-Reconstruction Test

/// Complex enum (has associated values) — triggers MethodClosureBridge path
/// when used as a closure argument type.
public enum TransformOutcome {
    case completed(result: Int32)
    case failed(errorCode: Int32)
}

/// Inspects a TransformOutcome and returns the associated Int32 value.
public func outcomeValue(_ outcome: TransformOutcome) -> Int32 {
    switch outcome {
    case .completed(let result): return result
    case .failed(let code): return code
    }
}

/// Whether a TransformOutcome is the completed case.
public func outcomeIsCompleted(_ outcome: TransformOutcome) -> Bool {
    if case .completed = outcome { return true }
    return false
}

/// Non-frozen struct with a closure-bearing instance method.
/// Tests MCB struct self-reconstruction: the generated Swift wrapper must use
/// `self_.assumingMemoryBound(to:).pointee` (NOT `Unmanaged<T>` which requires AnyObject).
/// Without the fix, this produces: error: 'Unmanaged' requires that 'DataTransformer' be a class type
public struct DataTransformer {
    public let factor: Int32

    public init(factor: Int32) {
        self.factor = factor
    }

    /// Instance method with closure taking a complex enum arg — triggers MCB path.
    /// The struct parent (DataTransformer) requires assumingMemoryBound for self-reconstruction.
    public func process(completion: @escaping (TransformOutcome) -> Void) {
        if factor > 0 {
            completion(.completed(result: factor * 2))
        } else {
            completion(.failed(errorCode: -1))
        }
    }
}

/// Class with the same closure pattern — for contrast testing.
/// Classes use Unmanaged<T> for self-reconstruction (existing behavior).
public class ClassTransformer {
    public let factor: Int32

    public init(factor: Int32) {
        self.factor = factor
    }

    /// Same MCB pattern but on a class parent — uses Unmanaged<T>.
    public func process(completion: @escaping (TransformOutcome) -> Void) {
        if factor > 0 {
            completion(.completed(result: factor * 2))
        } else {
            completion(.failed(errorCode: -1))
        }
    }
}
