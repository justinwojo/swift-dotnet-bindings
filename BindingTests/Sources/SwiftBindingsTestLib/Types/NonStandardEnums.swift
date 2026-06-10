// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Single-Case Enum (zero runtime size — BUG-2 coverage)

/// Single-case enum with String raw value. Swift optimizes single-case enums
/// to zero size (TypeMetadata.Size == 0), which crashes marshalling if emitted.
/// The generator should skip this type entirely.
/// Single-case String-backed enum: Swift optimizes to zero size, which crashes marshalling.
public enum SingleCaseMode: String {
    case photo
}

/// Single-case enum with Int32 raw value — contrast to SingleCaseMode.
/// Int-backed enums are safe even with 1 case because C# enum uses the raw value
/// as backing (4 bytes), not Swift's zero-size layout. Should be emitted normally.
public enum SingletonFlag: Int32 {
    case active = 1
}

/// Two-case enum with String raw value — contrast to SingleCaseMode.
/// This SHOULD be emitted normally since it has >1 case.
public enum DualCaseMode: String {
    case photo
    case video
}

// MARK: - UInt16-Backed Enum

/// Enum with UInt16 raw value.
public enum SecurityError: UInt16 {
    case none = 0
    case badCertificate = 1
    case pinningFailed = 2
    case invalidChain = 3
}

// MARK: - Int64-Backed Enum

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
