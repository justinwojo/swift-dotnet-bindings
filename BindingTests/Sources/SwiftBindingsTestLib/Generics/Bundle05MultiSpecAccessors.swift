// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Bundle 05 #3 regression — MultiSpecialization drops generic property accessors
//
// Properties defined on `extension Foo where Param == Concrete` blocks
// of a generic type were being skipped wholesale with skip reason
// `MultiSpecialization` whenever more than one `where Param ==
// Concrete` block existed for the same parent type. The canonical case
// is StoreKit2's `VerificationResult<SignedType>.jwsRepresentation`,
// `.signature`, etc. — declared on extensions over the realized
// `SignedType ∈ {Transaction, AppTransaction, RenewalInfo}` family and
// dropped from the binding entirely, blocking server-side App Store
// receipt verification end-to-end.
//
// The fix routes the per-specialization properties through
// `ConstrainedExtensionEmitter`, which surfaces each as a closed-
// generic C# extension method (`GetAlphaTag(this Bundle05Container<…>
// self)`) backed by its own per-specialization PInvoke entry point.
// Each Swift specialization is monomorphized into its own mangled
// symbol; the C# extension methods carry the closed-generic receiver
// type so there's no name collision at the call site.
//
// This fixture pairs DIFFERENT property names on each specialization to
// avoid colliding with the
// `ConstrainedExtensionDedup` (alpha/beta `markerLabel`) fixture, which
// covers the same-name conflict-skip path. Distinct names exercise the
// straight surface-emission path that the multispec fix needs to keep
// healthy across both Mono JIT and NativeAOT.

// MARK: Marker types

/// Frozen-struct marker for the `Bundle05Container` alpha specialization.
public struct Bundle05SpecKeyA {
    public init() {}
}

/// Frozen-struct marker for the `Bundle05Container` beta specialization.
public struct Bundle05SpecKeyB {
    public init() {}
}

// MARK: Generic carrier

/// Generic carrier struct that hosts per-specialization properties via
/// `where Key == Concrete` extensions below. The unconstrained `id`
/// property establishes a baseline the test can use to verify the
/// merged C# class itself round-trips correctly — independent of the
/// per-specialization accessors.
public struct Bundle05Container<Key> {
    public let id: Int32

    public init(id: Int32) {
        self.id = id
    }
}

// MARK: Non-frozen struct payload

/// Non-frozen public struct used as the return type of an alpha-only
/// constrained-extension accessor below. Deliberately NOT marked
/// `@frozen` so the binding emit picks the
/// `CEReturnShape.NonFrozenStruct` indirect-result path — the same
/// path StoreKit2's `VerificationResult.headerData` (Foundation.Data)
/// flows through. This is the path the Bundle 05 ownership fix
/// (ConstrainedExtensionEmitter NonFrozenStruct shape, success-path
/// no-Free → catch-only Free) actually changed; without a fixture
/// here the round-1 use-after-free / double-free would not be caught
/// by Layer-B Mono-JIT or NativeAOT runtime tests.
public struct Bundle05DescriptorPayload {
    public let id: Int32
    public let label: String

    public init(id: Int32, label: String) {
        self.id = id
        self.label = label
    }
}

// MARK: Per-specialization accessors

/// Alpha-specialization-only property — string return shape, the
/// dominant case in the StoreKit2 repro (`jwsRepresentation`).
extension Bundle05Container where Key == Bundle05SpecKeyA {
    public var alphaTag: String {
        return "alpha-\(id)"
    }

    /// Alpha-only non-frozen struct return — exercises the
    /// `CEReturnShape.NonFrozenStruct` path in
    /// `ConstrainedExtensionEmitter`. The returned payload's buffer is
    /// allocated by the C# wrapper, populated by the Swift `@_cdecl`
    /// indirect-result thunk, then handed to `SwiftMarshal.MarshalFromSwift<T>`
    /// which wraps it in a SafeHandle that owns the buffer thereafter.
    /// Pre-fix the wrapper freed the buffer in `finally` after
    /// `MarshalFromSwift`, leaving the returned object with a dangling
    /// payload (use-after-free / double-free on disposal). Post-fix
    /// the wrapper frees only on the catch path (mirroring
    /// `ExtensionMarshallingHelper.cs` ReturnKind.NonFrozenStruct).
    public var alphaDescriptor: Bundle05DescriptorPayload {
        return Bundle05DescriptorPayload(id: id, label: "alpha-descriptor-\(id)")
    }
}

/// Beta-specialization-only property — distinct name from the alpha
/// extension to exercise the straight surface-emission path (no name
/// collision, no dedup-skip). The Swift specialization is monomorphized
/// into its own mangled symbol per the realized `Key`.
extension Bundle05Container where Key == Bundle05SpecKeyB {
    public var betaTag: String {
        return "beta-\(id)"
    }
}

// MARK: Closed-generic factories

/// Alpha-specialized factory so the C# binding has a reachable
/// instantiation point that links against the type. Without a
/// reachable closed instantiation the generator may skip emission of
/// the per-specialization extension method.
public func makeBundle05ContainerAlpha(_ id: Int32) -> Bundle05Container<Bundle05SpecKeyA> {
    return Bundle05Container<Bundle05SpecKeyA>(id: id)
}

/// Beta-specialized factory — same purpose as the alpha factory, for
/// the beta specialization.
public func makeBundle05ContainerBeta(_ id: Int32) -> Bundle05Container<Bundle05SpecKeyB> {
    return Bundle05Container<Bundle05SpecKeyB>(id: id)
}
