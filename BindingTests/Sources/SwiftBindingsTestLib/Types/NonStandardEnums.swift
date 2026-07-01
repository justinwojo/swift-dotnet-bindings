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

// MARK: - @objc Int-Backed Enum With Large, Non-Sequential Raw Values

/// @objc Int-backed enum whose explicit raw values are large and non-sequential — a common
/// real-world shape for SDK error-code enums (e.g. `case wrongPassword = 17009`). The C#
/// member must carry the actual Swift raw value, NOT the declaration-order ordinal (0,1,2),
/// and the scalar must round-trip across the @_cdecl boundary in both directions.
///
/// `@objc` is load-bearing here: the Swift compiler only preserves explicit enum raw values
/// in the textual `.swiftinterface` (the generator's source of truth) for `@objc` enums. A
/// plain `enum: Int` has its `= 17009` stripped to a bare `case wrongPassword`, leaving no
/// recoverable raw value, so the generator can only fall back to declaration-order ordinals
/// for it. The bridged (NS_ENUM) form, whose in-memory representation IS the raw value, is
/// the case that both carries the value and benefits from preserving it.
@objc public enum AuthErrorCodeLike: Int {
    case wrongPassword = 17009
    case userNotFound = 17011
    case networkError = 17020
}

/// Round-trips an `AuthErrorCodeLike` through @_cdecl: the C# scalar is converted to the
/// Swift case (the param-conversion switch) and the result back to a scalar (the return
/// switch). A correct fix returns the same case the caller passed.
public func echoAuthErrorCode(_ code: AuthErrorCodeLike) -> AuthErrorCodeLike {
    return code
}

/// Returns a fixed case so the enum→scalar return switch is exercised independently of any
/// input value. The caller asserts the scalar equals the Swift raw value (17009).
public func wrongPasswordCode() -> AuthErrorCodeLike {
    return .wrongPassword
}
