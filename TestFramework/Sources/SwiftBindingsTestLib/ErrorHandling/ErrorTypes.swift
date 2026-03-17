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
