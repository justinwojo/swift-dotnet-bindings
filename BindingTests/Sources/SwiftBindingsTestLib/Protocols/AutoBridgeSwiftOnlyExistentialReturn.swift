// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Session 5b regression fixture
//
// Mirrors RealityKit's `MultipeerConnectivityService.Owner(Entity) -> any
// RealityFoundation.SynchronizationPeerID` suppression pattern in BindingTests.
// Until Session 5b, `TypeDatabaseExtensions.IsObjCModuleType` classified every
// non-value-type from an auto-bridge module as ObjC even when the type's name
// didn't match the module's declared `objcPrefixes`. That stripped the protocol
// out of `ExistentialHandler.GetEffectiveProtocols`, dropped the effective count
// to 0, returned `"object"` from `GetPublicExistentialType`, and tripped
// `B6 UnsupportedExistential` in `MemberEmissionValidator` — the method went
// missing from the generated bindings entirely.
//
// `Foundation.LocalizedError` is the BindingTests-side mirror: Foundation is
// auto-bridge with `objcPrefixes: ["NS"]`, and `LocalizedError` is a Swift-only
// protocol whose name doesn't match the prefix. Pre-fix, a method returning
// `any LocalizedError` was suppressed; post-fix it must emit so the runtime
// test below can call it.

/// Swift class returning an `any LocalizedError` existential. The test asserts
/// the method is reachable from C# (proves it wasn't suppressed) and the
/// underlying value's `errorDescription` round-trips through the existential.
public final class AutoBridgeSwiftOnlyExistentialOwner {
    private let _description: String

    public init(description: String) {
        self._description = description
    }

    /// Returns `any LocalizedError`. The signature was the trip-wire for the
    /// `IsObjCModuleType` over-classification bug.
    public func owner() -> any LocalizedError {
        return AutoBridgeBugReproError(description: _description)
    }
}

/// Concrete LocalizedError used as the existential payload.
public struct AutoBridgeBugReproError: LocalizedError {
    public let description: String
    public init(description: String) {
        self.description = description
    }
    public var errorDescription: String? {
        return description
    }
}
