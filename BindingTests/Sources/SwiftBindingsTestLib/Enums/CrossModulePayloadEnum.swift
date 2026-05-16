// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import SwiftBindingsTestLibDependency

// Reproducer for the cross-module variant of
// bug-0.10.0-enum-case-payload-extractor-missing.md (S-3 in
// sdk-0.11.0-residual-gaps.md). Stripe's StripeFinancialConnections emits an
// enum whose `.completed(payload:)` case carries a type owned by a *different*
// module (`FinancialConnections.FinancialConnectionsSession`), and the
// validation pass found the extractor + factory were missing from the
// generated C# while the `failed(error:)` case (whose payload is the erased
// `any Swift.Error` -> `AnyError`) still emitted. The same-module variant in
// `ClassPayloadEnum.swift` works; this fixture locks the cross-module path so
// any future regression in TypeDatabase lookup, projection, or guard wiring
// surfaces as a missing entry in BindingTests rather than as a silent drop.

/// Result-shape enum carrying a cross-module **class** payload
/// (`DependencyService` from `SwiftBindingsTestLibDependency`). Mirrors the
/// Stripe `FinancialConnections.Result.completed(session:)` shape: labeled
/// success payload + no-payload cancel + erased-error failure.
public enum CrossModuleClassResult {
    case completed(session: DependencyService)
    case canceled
    case failed(error: any Swift.Error)
}

/// Result-shape enum carrying a cross-module **frozen-struct** payload.
public enum CrossModuleFrozenStructResult {
    case completed(point: DependencyPoint)
    case canceled
    case failed(error: any Swift.Error)
}

/// Result-shape enum carrying a cross-module **non-frozen-struct** payload
/// (`DependencyConfig` is a non-`@frozen` struct with a `String` field, so it
/// projects through `ClassWithOpaquePayload` on the C# side and exercises the
/// `InitializeWithCopy` heap path in `EnumHandler.Marshalling`).
public enum CrossModuleNonFrozenStructResult {
    case completed(config: DependencyConfig)
    case canceled
    case failed(error: any Swift.Error)
}

/// Concrete `Swift.Error` used to construct the `.failed(error:)` payloads above.
/// The generated C# extractor erases it to `Swift.Foundation.AnyError`, so the
/// runtime tests assert on `TryGetFailed(out _)` rather than the concrete type.
public struct CrossModulePayloadError: Swift.Error {
    public let message: String
    public init(message: String) {
        self.message = message
    }
}

// MARK: - Factories

public func makeCrossModuleClassCompleted(name: String) -> CrossModuleClassResult {
    return .completed(session: DependencyService(name: name))
}

public func makeCrossModuleClassCanceled() -> CrossModuleClassResult {
    return .canceled
}

public func makeCrossModuleClassFailed(message: String) -> CrossModuleClassResult {
    return .failed(error: CrossModulePayloadError(message: message))
}

public func makeCrossModuleFrozenStructCompleted(x: Double, y: Double) -> CrossModuleFrozenStructResult {
    return .completed(point: DependencyPoint(x: x, y: y))
}

public func makeCrossModuleFrozenStructCanceled() -> CrossModuleFrozenStructResult {
    return .canceled
}

public func makeCrossModuleFrozenStructFailed(message: String) -> CrossModuleFrozenStructResult {
    return .failed(error: CrossModulePayloadError(message: message))
}

public func makeCrossModuleNonFrozenStructCompleted(name: String, version: Int32) -> CrossModuleNonFrozenStructResult {
    return .completed(config: DependencyConfig(name: name, version: version))
}

public func makeCrossModuleNonFrozenStructCanceled() -> CrossModuleNonFrozenStructResult {
    return .canceled
}

public func makeCrossModuleNonFrozenStructFailed(message: String) -> CrossModuleNonFrozenStructResult {
    return .failed(error: CrossModulePayloadError(message: message))
}
