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
