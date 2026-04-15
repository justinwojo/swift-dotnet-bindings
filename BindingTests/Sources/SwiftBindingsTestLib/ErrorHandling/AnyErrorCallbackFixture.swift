// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - AnyError Runtime Test Helpers
//
// These @_cdecl functions create error existential containers that C# tests
// can use to exercise AnyError.LocalizedDescription without going through
// the closure callback path (which lacks a @_cdecl wrapper for existential
// parameters and crashes via CallConvSwift).

/// Writes a MathError.divisionByZero into the buffer as an `any Error` existential container.
/// The buffer must be at least 5 machine words (40 bytes on arm64).
@_cdecl("SBW_Test_CreateMathErrorContainer")
public func sbw_test_createMathErrorContainer(_ bufferPtr: UnsafeMutableRawPointer) {
    let error: any Error = MathError.divisionByZero
    bufferPtr.initializeMemory(as: (any Error).self, repeating: error, count: 1)
}

/// Writes an NSError with a localized description into the buffer as an `any Error` container.
@_cdecl("SBW_Test_CreateNSErrorContainer")
public func sbw_test_createNSErrorContainer(_ bufferPtr: UnsafeMutableRawPointer) {
    let error: any Error = NSError(
        domain: "TestDomain",
        code: 42,
        userInfo: [NSLocalizedDescriptionKey: "Test error description"]
    )
    bufferPtr.initializeMemory(as: (any Error).self, repeating: error, count: 1)
}

/// Writes a ValidationError.tooLong(maxLength: 100) into the buffer.
/// Tests that associated-value enum cases produce meaningful descriptions.
@_cdecl("SBW_Test_CreateValidationErrorContainer")
public func sbw_test_createValidationErrorContainer(_ bufferPtr: UnsafeMutableRawPointer) {
    let error: any Error = ValidationError.tooLong(maxLength: 100)
    bufferPtr.initializeMemory(as: (any Error).self, repeating: error, count: 1)
}

// MARK: - Closure Callback with `any Error`
//
// Exercises the MCB pipeline for `any Swift.Error` closure parameters
// (Fix 3 from ship-blockers.md). The generator emits an SBW_MCB_ @_cdecl
// wrapper that wraps the existential container with withUnsafePointer and
// hands an ExistentialContainer1 pointer to the C# callback, which
// reconstructs a Swift.AnyError.

public final class AnyErrorCallbackFixture {
    public init() {}

    /// Invokes `callback` synchronously with `MathError.divisionByZero`.
    public func reportMathError(callback: (any Error) -> Void) {
        callback(MathError.divisionByZero)
    }

    /// Invokes `callback` synchronously with `ValidationError.tooLong(maxLength: 50)`.
    /// Exercises an enum case with an associated value.
    public func reportValidationError(callback: (any Error) -> Void) {
        callback(ValidationError.tooLong(maxLength: 50))
    }

    /// Invokes `callback` synchronously with an NSError carrying a
    /// localized description. Verifies the existential round-trip works
    /// for ObjC-bridged errors as well as pure Swift enums.
    public func reportNSError(callback: (any Error) -> Void) {
        let error: any Error = NSError(
            domain: "CallbackDomain",
            code: 7,
            userInfo: [NSLocalizedDescriptionKey: "Callback error description"]
        )
        callback(error)
    }

    // MARK: - Session 2 Pattern A: `(any Error)?` closure parameter
    //
    // Mirrors `PaymentSheet.FlowController.update(intentConfiguration:completion:)`
    // where Stripe delivers either a nil error (success) or an existential
    // error (failure) through a single closure parameter.

    /// Invokes `callback` with `nil` (success) or a `MathError` (failure),
    /// depending on `shouldSucceed`. Exercises `(any Error)?` as a single
    /// closure parameter.
    public func reportOptionalError(shouldSucceed: Bool, callback: ((any Error)?) -> Void) {
        if shouldSucceed {
            callback(nil)
        } else {
            callback(MathError.divisionByZero)
        }
    }

    // MARK: - Session 2 Pattern A (3-arg): `(T, U, (any Error)?)` closure parameter
    //
    // Exercises Optional<any Error> in the trailing slot of a multi-arg closure,
    // mirroring the Stripe pattern `(STPIssuingCardPin?, STPPinStatus, (any Error)?)`
    // with blittable stand-ins for the first two slots (Optional<class> and Optional<Int>
    // in closure args are a separate pattern tracked for future work).

    /// Invokes `callback` with three arguments: a pin code (Int32), a status (Int32),
    /// and an optional error. Success (kind 0) → (1234, 1, nil); failure (kind 1) →
    /// (0, 0, MathError); other (default) → (0, 2, ValidationError).
    public func reportPinDetails(kind: Int32, callback: (Int32, Int32, (any Error)?) -> Void) {
        switch kind {
        case 0: callback(1234, 1, nil)                         // success
        case 1: callback(0, 0, MathError.divisionByZero)       // error
        default: callback(0, 2, ValidationError.tooLong(maxLength: 10))
        }
    }

    // MARK: - Session 2 Pattern B: `Result<T, any Error>` closure parameter
    //
    // Mirrors Stripe's completion handler shape:
    //   `(Result<PaymentSheet.FlowController, any Error>) -> Void`
    // Success carries a concrete value (Int32 here); failure carries an existential error.
    // The Swift adapter wraps the Result enum via `withUnsafePointer(to:)` so the C#
    // callback receives a stack-lifetime pointer; the C# side heap-copies into a
    // `SwiftResult<Int32, ExistentialContainer1>` owned via SafeHandle.

    /// Invokes `callback` with either `.success(42)` or `.failure(MathError.divisionByZero)`.
    public func reportResult(shouldSucceed: Bool, callback: (Result<Int32, any Error>) -> Void) {
        if shouldSucceed {
            callback(.success(42))
        } else {
            callback(.failure(MathError.divisionByZero))
        }
    }
}
