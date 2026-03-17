// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Comparison Operators

/// Frozen struct for testing comparison operator emission.
@frozen
public struct ComparableValue: Equatable, Comparable {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public static func == (lhs: ComparableValue, rhs: ComparableValue) -> Bool {
        return lhs.value == rhs.value
    }

    public static func < (lhs: ComparableValue, rhs: ComparableValue) -> Bool {
        return lhs.value < rhs.value
    }

    // Note: >, <=, >= are automatically synthesized from == and < by the binding generator.
}

// MARK: - Custom Equality Logic

/// Frozen struct with custom equality that uses a tolerance of 5.
/// Two values are considered equal if their difference is within the tolerance.
/// This tests that the binding generator correctly emits custom == operators
/// rather than relying on default memberwise equality.
@frozen
public struct ApproximatelyEqual: Equatable {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public static func == (lhs: ApproximatelyEqual, rhs: ApproximatelyEqual) -> Bool {
        return abs(Int(lhs.value) - Int(rhs.value)) <= 5
    }
}

// MARK: - Non-Frozen Struct Equality (Alamofire HTTPHeader pattern)

/// Non-frozen struct with Equatable — takes the @_cdecl wrapper path for equality
/// (different from frozen structs which use CallConvSwift).
/// Real-world pattern: Alamofire HTTPHeader ==, KeychainAccess AuthenticationPolicy ==.
public struct Tag: Equatable {
    public var key: String
    public var value: String

    public init(key: String, value: String) {
        self.key = key
        self.value = value
    }
}
