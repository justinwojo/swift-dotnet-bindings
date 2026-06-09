// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import SwiftBindingsTestLibDependency

// SDK 0.11.0 R2 — nested type DECLARED INSIDE an extension of a
// FOREIGN-MODULE type, then used as an enum-case payload in this module.
//
// Stripe shape reproduction:
//   StripeFinancialConnections.swiftinterface declares:
//     extension StripeCore.StripeAPI {
//       public struct FinancialConnectionsSession { ... }
//       public struct BankAccountToken { ... }
//     }
//   ...and an enum whose case payloads reference those nested types.
//
// Before the emitter fix, CrossModuleExtensionEmitter only recursed nested
// types for the struct-receiver path, so class-receiver extensions silently
// dropped the nested-type definitions. The downstream enum cases then lost
// their factories (no Completed(...)) and extractors (no TryGetCompleted),
// matching the StripeFinancialConnections regression seen in 0.11.0 R2.

// MARK: - Nested types declared inside extensions of foreign types

extension DependencyService {
    /// Nested struct declared inside an extension of a foreign class.
    /// The emitter must mirror this into the current module as
    /// `DependencyService.HostedPayload` and emit downstream usages
    /// (factories, extractors, property types) against the mirrored name.
    public struct HostedPayload {
        public let label: String
        public let count: Int32

        public init(label: String, count: Int32) {
            self.label = label
            self.count = count
        }

        public func describe() -> String {
            return "\(label)#\(count)"
        }
    }

    /// Second nested struct on the same foreign class — locks the multi-nested
    /// recursion path (Stripe declares both `FinancialConnectionsSession` and
    /// `BankAccountToken` inside the same `extension StripeAPI`).
    public struct HostedToken {
        public let identifier: Int32

        public init(identifier: Int32) {
            self.identifier = identifier
        }
    }
}

extension DependencyPoint {
    /// Nested struct declared inside an extension of a foreign FROZEN STRUCT.
    /// Exercises the value-type-receiver branch of CrossModuleExtensionEmitter's
    /// nested-type recursion (the class branch is covered above).
    public struct HostedTag {
        public let value: Int32

        public init(value: Int32) {
            self.value = value
        }
    }
}

// MARK: - Enum cases referencing the cross-module nested types

/// Enum whose `.completed(payload:)` case carries a nested struct declared
/// inside an extension of a foreign class. Mirrors the Stripe `Result.completed`
/// shape exactly: labeled single-payload + payload type lives in *this* module
/// even though its containing type lives in another module.
public enum CrossModuleNestedHostedResult {
    case completed(payload: DependencyService.HostedPayload)
    case canceled
    case failed(error: any Swift.Error)
}

/// Two-top-level-associated-value shape: the case has two SEPARATE labeled
/// payloads at top level (NOT a labeled inner tuple under one outer label).
/// The Swift ABI prints this as `completed(session:, token:)` directly, so
/// the emitter does NOT consult `EnumCaseDecl.OuterTupleLabel` — that path
/// is exercised by `CrossModuleNestedOuterTupleResult` below. Both shapes
/// share the cross-module nested payload types declared inside
/// `extension DependencyService`, but they hit different emission branches.
public enum CrossModuleNestedTokenResult {
    case completed(session: DependencyService.HostedPayload, token: DependencyService.HostedToken?)
    case canceled
    case failed(error: any Swift.Error)
}

/// Same shape as `CrossModuleNestedHostedResult` but the payload type is
/// nested inside an extension of a frozen STRUCT in the foreign module.
public enum CrossModuleNestedFrozenStructResult {
    case completed(tag: DependencyPoint.HostedTag)
    case canceled
    case failed(error: any Swift.Error)
}

/// The TRUE labeled-outer-tuple shape: a single `payload:` label wrapping a
/// labeled inner tuple. The Swift ABI prints this case as
/// `completed(payload: (session: T, token: U?))`, so the parser must capture
/// `payload` as `EnumCaseDecl.OuterTupleLabel` and the emitter must rebuild
/// the surface call as `Completed(session, token)` (positional unwrap of the
/// inner tuple — NOT `Completed(payload)` taking a synthesized tuple struct,
/// and NOT `Completed(session, token)` from two top-level associated values).
/// `CrossModuleNestedTokenResult` above uses two separate associated values
/// and does NOT exercise `OuterTupleLabel`; this enum is the one that does.
public enum CrossModuleNestedOuterTupleResult {
    case completed(payload: (session: DependencyService.HostedPayload, token: DependencyService.HostedToken?))
    case canceled
    case failed(error: any Swift.Error)
}

// MARK: - Factories

public func makeCrossModuleNestedHostedCompleted(label: String, count: Int32) -> CrossModuleNestedHostedResult {
    return .completed(payload: DependencyService.HostedPayload(label: label, count: count))
}

public func makeCrossModuleNestedHostedCanceled() -> CrossModuleNestedHostedResult {
    return .canceled
}

public func makeCrossModuleNestedHostedFailed(message: String) -> CrossModuleNestedHostedResult {
    return .failed(error: CrossModulePayloadError(message: message))
}

public func makeCrossModuleNestedTokenCompleted(
    label: String,
    count: Int32,
    tokenId: Int32
) -> CrossModuleNestedTokenResult {
    return .completed(
        session: DependencyService.HostedPayload(label: label, count: count),
        token: DependencyService.HostedToken(identifier: tokenId))
}

public func makeCrossModuleNestedTokenCompletedNoToken(
    label: String,
    count: Int32
) -> CrossModuleNestedTokenResult {
    return .completed(
        session: DependencyService.HostedPayload(label: label, count: count),
        token: nil)
}

public func makeCrossModuleNestedTokenCanceled() -> CrossModuleNestedTokenResult {
    return .canceled
}

public func makeCrossModuleNestedFrozenStructCompleted(value: Int32) -> CrossModuleNestedFrozenStructResult {
    return .completed(tag: DependencyPoint.HostedTag(value: value))
}

public func makeCrossModuleNestedFrozenStructCanceled() -> CrossModuleNestedFrozenStructResult {
    return .canceled
}

public func makeCrossModuleNestedOuterTupleCompleted(
    label: String,
    count: Int32,
    tokenId: Int32
) -> CrossModuleNestedOuterTupleResult {
    return .completed(payload: (
        session: DependencyService.HostedPayload(label: label, count: count),
        token: DependencyService.HostedToken(identifier: tokenId)))
}

public func makeCrossModuleNestedOuterTupleCanceled() -> CrossModuleNestedOuterTupleResult {
    return .canceled
}
