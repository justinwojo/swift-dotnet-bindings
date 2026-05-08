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

// MARK: - Phase 4 Layer 5 breadth fixtures

/// Plain `async throws` function that throws a complex enum (associated values).
/// Exercises the Layer 5 ownership-transfer branch of the cascade dispatcher:
/// MarshalFromSwift<ParseError2> hands the buffer to a SafeHandle, so the C#
/// helper must NOT free in the per-case finally. Asserting against
/// `ParseError2.unexpectedEOF(at: 42)` rather than the first case rules out a
/// "default discriminant happened to match" false positive in the tag check.
public func plainThrowsAsyncParse(input: String) async throws -> Int32 {
    try? await Task.sleep(nanoseconds: 1_000_000)
    if input.isEmpty { throw ParseError2.unexpectedEOF(at: 42) }
    if input == "overflow" { throw ParseError2.overflow }
    if input.first == "!" { throw ParseError2.malformed(reason: "leading punctuation: \(input)") }
    return Int32(input.count)
}

/// Plain `async throws` function that throws a non-frozen struct error.
/// Exercises the struct-shaped ownership-transfer branch of the cascade.
public func plainThrowsAsyncLoadConfig(path: String) async throws -> Int32 {
    try? await Task.sleep(nanoseconds: 1_000_000)
    if path.isEmpty { throw PlainThrowsConfigError(path: path, lineNumber: 0) }
    if path == "/etc/bad" { throw PlainThrowsConfigError(path: path, lineNumber: 7) }
    return Int32(path.count)
}

/// Plain `async throws` function that throws an Error from outside the module's
/// registry (Foundation `NSError`). The cascade has no `as?` arm for `NSError`,
/// so it falls through to id 0 with nil buffer — the C# side surfaces a bare
/// `SwiftException` (not `SwiftException<T>`). The Swift error description is
/// preserved on the `.Message` field via `String(describing:)`.
public func plainThrowsAsyncFallthroughToUntyped() async throws -> Int32 {
    try? await Task.sleep(nanoseconds: 1_000_000)
    throw NSError(
        domain: "SwiftBindingsTestLib.UnregisteredDomain",
        code: 7777,
        userInfo: [NSLocalizedDescriptionKey: "fallthrough-sentinel-7777"])
}

/// Plain `async throws` function that throws a class-shaped Error
/// (`PlainThrowsScanError`). Exercises the Layer 5 class-pointer-direct cascade
/// shape: Swift sends a +1 retained class pointer over the wire (no carrier
/// buffer), and C# `MarshalFromSwift<PlainThrowsScanError>` constructs the
/// SwiftObject taking ownership of the retain — nothing to `SBW_Free`.
public func plainThrowsAsyncScan(input: String) async throws -> Int32 {
    try? await Task.sleep(nanoseconds: 1_000_000)
    if input.isEmpty {
        throw PlainThrowsScanError(code: 404, detail: "empty input rejected")
    }
    if input == "denied" {
        throw PlainThrowsScanError(code: 403, detail: "scanning denied for: \(input)")
    }
    return Int32(input.count)
}
