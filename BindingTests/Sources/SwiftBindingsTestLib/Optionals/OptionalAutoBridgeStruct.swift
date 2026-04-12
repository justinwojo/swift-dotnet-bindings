// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#if canImport(AuthenticationServices) && !os(tvOS) && !os(watchOS)
import AuthenticationServices

// MARK: - OptionalAutoBridgeStruct (fix #13, compile-only)
//
// Pins the Optional<T> lowering path where T lives in an AutoBridge module
// that has no `valueTypes` overrides in apple-frameworks.json. Fix #13
// (WrapperEmitter.Marshalling) gates this lowering on
// `IsObjCModuleType(innerNamed) && !IsKnownSwiftValueType(innerNamed, ...)`.
// Before the fix, the marshaler dropped the Optional wrapper on an
// AutoBridge-module type at parameter/return position and the generated
// C# wrapper tried to pass the inner type directly, tripping a Swift
// compile error in the bridge layer.
//
// NOTE: must use a *real* AuthenticationServices type rather than a synthetic
// struct: AutoBridge classification in apple-frameworks.json is module-level,
// and SwiftBindingsTestLib is not in that list. A synthetic struct declared
// locally would never hit the IsObjCModuleType guard and would exercise a
// different (frozen-struct) Optional path. The fixture must go through the
// real codepath fix #13 is meant to protect, which means importing a real
// AutoBridge module and naming a type the generator will classify the same
// way a real consumer's code would.
//
// ASAuthorizationPublicKeyCredentialParameters is the chosen type because:
//   • It is public AS surface with a single-argument hermetic initializer
//     (`init(algorithm: ASCOSEAlgorithmIdentifier)`) — no entitlements,
//     no network, no runtime state to stand up.
//   • It is NOT in AS's valueTypes list (AS has no valueTypes entries at
//     all), so `!IsKnownSwiftValueType` returns true and the Optional
//     lowering dispatches through the fix #13 branch.
//   • Its iOS availability floor (iOS 15 / macOS 12) is below the test
//     library's SDK floor, so the symbol resolves on the simulator.
//
// tvOS is explicitly excluded: ASAuthorizationPublicKeyCredentialParameters
// does not exist on tvOS. The file's `#if !os(tvOS)` guard and the Swift
// `@available(tvOS, unavailable)` attribute both enforce this so the
// generated C# attribute set does not claim tvOS support for the wrapper
// (which would trip CA1416 against the .NET AS binding's own platform list).
//
// This fixture is *compile-only*: the C# side has no runtime test. The
// observable behavior is "the generator emits valid C# and the bridge
// compiles." If fix #13 regresses, `nuke binding-tests` fails at bridge
// build time. Runtime bridging of an AS ObjC class from the test app's
// C# harness would require cross-language interop scaffolding that is
// well beyond the scope of pinning fix #13.

/// Takes `Optional<T>` where T is an AuthenticationServices type that the
/// generator must classify as an ObjC-bridged module type. Returns the same
/// value unchanged — the generator's Optional lowering is what we are
/// pinning, not the behavior of the `params` object itself.
@available(iOS 15.0, macOS 12.0, *)
@available(tvOS, unavailable)
@available(watchOS, unavailable)
public func roundTripOptionalASCredentialParameters(
    _ params: ASAuthorizationPublicKeyCredentialParameters?
) -> ASAuthorizationPublicKeyCredentialParameters? {
    return params
}

/// Companion that takes the AS type at a non-Optional position so the
/// generator's emission for the type can be compared against the Optional
/// path above. If this compiles but the Optional version does not, fix #13
/// has specifically regressed on the Optional branch of the lowering.
@available(iOS 15.0, macOS 12.0, *)
@available(tvOS, unavailable)
@available(watchOS, unavailable)
public func identityASCredentialParameters(
    _ params: ASAuthorizationPublicKeyCredentialParameters
) -> ASAuthorizationPublicKeyCredentialParameters {
    return params
}

#endif
