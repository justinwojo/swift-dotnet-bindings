// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Emoji in Enum Case Names
// Pattern caught in Valet validation (24 errors).
// Swift allows emoji in identifiers; C# does not.
// Generator's SanitizeIdentifierChars replaces emoji with underscores.
// Note: ⚠️ (U+26A0 + U+FE0F) and ⏳ (U+23F3) fail to compile as Swift identifiers.
// Emoji from Supplementary Multilingual Plane (U+1Fxxx) work: 🚫, 🔶, 🔄.

/// Enum with emoji in case names — generator must sanitize to valid C# identifiers.
public enum ValidationStatus: Int32 {
    case success = 0
    case error🚫 = 1       // emoji in case name (U+1F6AB)
    case warning🔶 = 2     // emoji in case name (U+1F536)
    case pending🔄 = 3     // emoji in case name (U+1F504)
}

/// Function to verify emoji-sanitized enum cases round-trip correctly.
public func describeValidationStatus(_ status: ValidationStatus) -> String {
    switch status {
    case .success: return "success"
    case .error🚫: return "error"
    case .warning🔶: return "warning"
    case .pending🔄: return "pending"
    @unknown default: return "unknown"
    }
}

/// Get the raw value of a ValidationStatus to verify enum value mapping.
public func validationStatusRawValue(_ status: ValidationStatus) -> Int32 {
    return status.rawValue
}
