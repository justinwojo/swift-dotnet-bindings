// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Error Enums

/// Validation error enum.
public enum ValidationError: Error {
    case empty
    case tooLong(maxLength: Int32)
    case invalidFormat(String)
}

/// Math error enum.
public enum MathError: Error {
    case divisionByZero
    case overflow
    case negativeInput
}

// MARK: - S2: Simple Int32 Raw Value Error (Valet KeychainError pattern)

/// Error enum with Int32 raw value — projected as a simple C# enum.
/// The SBW_ExtractTypedError_* wrapper extracts this from traditional `throws`.
public enum StorageError: Int32, Error {
    case notFound = -1
    case accessDenied = -2
    case corrupt = -3
}

// MARK: - Phase 4 breadth fixtures (Layer 5 cascade ownership audit)

/// Complex enum (cases with associated values) conforming to Error. Exercises
/// the Layer 5 ownership-transfer branch of the cascade dispatcher: Swift's
/// MarshalFromSwift hands the buffer to a SafeHandle for complex enums, so the
/// C# helper must NOT free in the per-case finally. The cascade Swift body
/// allocates the buffer via VWT-aware initializeMemory; the C# helper's
/// per-case `catch { SBW_Free; throw; }` covers the marshal-failure path.
public enum ParseError2: Error {
    case malformed(reason: String)
    case unexpectedEOF(at: Int32)
    case overflow
}

/// Struct conforming to Error. Non-frozen by default (no `@frozen` attribute)
/// so it goes through the resilience boundary on the C# side and is treated
/// as ownership-transfer by the Layer 5 dispatcher.
public struct PlainThrowsConfigError: Error {
    public let path: String
    public let lineNumber: Int32

    public init(path: String, lineNumber: Int32) {
        self.path = path
        self.lineNumber = lineNumber
    }
}

/// Class conforming to Error — exercises the Layer 5 class-pointer-direct
/// cascade shape. Swift hands a +1 retained class pointer (no carrier buffer)
/// to C#; `MarshalFromSwift<T>`'s `NewFromPayload` takes ownership of the
/// retain. There is nothing to `SBW_Free` for this shape — the SafeHandle's
/// finalizer balances the retain.
public class PlainThrowsScanError: Error {
    public let code: Int32
    public let detail: String

    public init(code: Int32, detail: String) {
        self.code = code
        self.detail = detail
    }
}

// (Fallthrough-to-untyped is exercised by throwing a Foundation type — e.g.
// NSError — that lives outside SwiftBindingsTestLib's registry. See
// `plainThrowsAsyncFallthroughToUntyped` in ThrowingFunctions.swift.)

/// Frozen struct with a reference-typed (`String`) field, conforming to Error.
/// Frozen + heap-typed field → `IsFrozenStructProjectedAsClass` on the C# side
/// (`ClassWithBufferStruct` shape). This fires the Layer 5
/// `BufferCopiedNeedsVwtDestroy` cascade shape: the generated frozen-struct
/// `NewFromPayload` does an `InitializeWithCopy` from the wire carrier into a
/// fresh `NativeMemory.Alloc` buffer owned by the SafeHandle, leaving the
/// source carrier with +1 retains on the `String`'s heap allocation. The
/// cascade dispatcher must run a VWT `Destroy` on the wire buffer before
/// `SBW_Free` to release those retains and the carrier itself.
@frozen
public struct PlainThrowsFrozenWithMemoryError: Error {
    public let resourceName: String
    public let attempts: Int32

    public init(resourceName: String, attempts: Int32) {
        self.resourceName = resourceName
        self.attempts = attempts
    }
}
