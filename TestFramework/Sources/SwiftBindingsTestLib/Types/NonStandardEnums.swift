// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - UInt16-Backed Enum (Starscream SecurityErrorCode pattern)

/// Enum with UInt16 raw value.
public enum SecurityError: UInt16 {
    case none = 0
    case badCertificate = 1
    case pinningFailed = 2
    case invalidChain = 3
}

// MARK: - Int64-Backed Enum (BonMot Ligatures pattern)

/// Enum with Int64 raw value.
public enum FeatureFlag: Int64 {
    case disabled = 0
    case enabled = 1
    case experimental = 2
}

// MARK: - UInt32-Backed Enum

/// Enum with UInt32 raw value.
public enum Permission: UInt32 {
    case none = 0
    case read = 1
    case write = 2
    case execute = 4
}
