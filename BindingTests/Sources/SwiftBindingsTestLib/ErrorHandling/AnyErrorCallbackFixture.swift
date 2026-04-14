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
}
