// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Throwing Functions (Tier 1)

/// Divides two Int32 values; throws on division by zero.
public func divide(a: Int32, b: Int32) throws -> Int32 {
    guard b != 0 else {
        throw MathError.divisionByZero
    }
    return a / b
}

/// Validates a string is non-empty and under a max length.
public func validate(_ input: String, maxLength: Int32 = 100) throws -> String {
    guard !input.isEmpty else {
        throw ValidationError.empty
    }
    guard input.count <= Int(maxLength) else {
        throw ValidationError.tooLong(maxLength: maxLength)
    }
    return input
}

// MARK: - Struct with Throwing Methods

/// Struct with instance and static throwing methods.
public struct ThrowingStruct {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    /// Instance throwing method.
    public func divideBy(_ divisor: Int32) throws -> Int32 {
        guard divisor != 0 else {
            throw MathError.divisionByZero
        }
        return value / divisor
    }

    /// Instance throwing method with validation.
    public func validatePositive() throws -> Int32 {
        guard value > 0 else {
            throw MathError.negativeInput
        }
        return value
    }

    /// Static throwing method.
    public static func safeDivide(_ a: Int32, _ b: Int32) throws -> Int32 {
        guard b != 0 else {
            throw MathError.divisionByZero
        }
        return a / b
    }
}

// MARK: - S2: Traditional Throws with Typed Error (Valet SecureEnclaveValet pattern)

/// Free function that throws a StorageError (Int32 raw value Error enum).
/// The wrapper layer generates SBW_ExtractTypedError_* to extract the typed error.
public func loadFromStorage(key: String) throws -> String {
    if key.isEmpty { throw StorageError.notFound }
    if key == "restricted" { throw StorageError.accessDenied }
    return "stored:\(key)"
}

/// Struct with throwing methods that use StorageError.
public struct SecureStore {
    public init() {}

    public func retrieve(key: String) throws -> String {
        guard !key.isEmpty else {
            throw StorageError.notFound
        }
        return "value-for-\(key)"
    }
}

// MARK: - Phase 4 plain-throws → SwiftException<TError> cascade

/// Plain `async throws` (NOT typed-throws) free function that throws MathError on
/// division by zero, and `MathError.overflow` for `a == Int32.min, b == -1`.
/// Exercises the per-module cascade dispatcher: MathError is registered in the
/// error-type registry, so the C# side should surface SwiftException<MathError>
/// rather than the untyped SwiftException fallback. The cascade test asserts
/// against `.overflow` (raw value 1) rather than `.divisionByZero` (raw value 0)
/// so the assertion can distinguish a real cascade payload from a default-zero
/// `Nullable<TError>` fallback.
public func plainThrowsAsyncDivide(a: Int32, b: Int32) async throws -> Int32 {
    try? await Task.sleep(nanoseconds: 1_000_000)
    if a == Int32.min && b == -1 { throw MathError.overflow }
    guard b != 0 else { throw MathError.divisionByZero }
    return a / b
}

/// Plain `async throws` free function that throws StorageError (Int32-rawvalue enum)
/// to confirm the cascade resolves multiple registered error types correctly.
public func plainThrowsAsyncRetrieve(key: String) async throws -> Int32 {
    try? await Task.sleep(nanoseconds: 1_000_000)
    if key.isEmpty { throw StorageError.notFound }
    if key == "restricted" { throw StorageError.accessDenied }
    return Int32(key.count)
}
