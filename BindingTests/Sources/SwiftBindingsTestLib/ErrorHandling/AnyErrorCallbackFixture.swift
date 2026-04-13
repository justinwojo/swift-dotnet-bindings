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
