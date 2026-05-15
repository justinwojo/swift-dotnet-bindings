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

// MARK: Foundation value-type return-shape accessors
//
// Cover the three Foundation frozen value-type return shapes the
// multispecialization fix needs after Bundle 05 — `Date` (Double-by-value
// at the ABI boundary; epoch-arithmetic to System.DateTimeOffset on the
// C# side), `UUID` (16-byte indirect-result; reinterpreted as
// System.Guid), and `Data` (16-byte indirect-result; projected as byte[]
// via Swift.Foundation.Data.ToByteArray()). These are exactly the shapes
// blocking StoreKit2's `VerificationResult<SignedType>.signedDate /
// .deviceVerificationNonce / .headerData|payloadData|signatureData|...`
// after the SignedType case shipped earlier in 0.10.0. Each is bound to
// the alpha specialization so the existing fixture infrastructure (alpha
// factory + alpha-only properties) carries them without adding new
// generic types.
extension Bundle05Container where Key == Bundle05SpecKeyA {
    /// Foundation.Date round-trip — the C# side should receive a
    /// System.DateTimeOffset whose `.UtcDateTime` matches the Swift
    /// reference epoch + (id) seconds. Validates that the P/Invoke
    /// returns `Double` directly (no indirect-result buffer) and that
    /// the C# emit applies `SwiftEpoch.AddSeconds(...)`.
    public var alphaSignedDate: Date {
        // Reference date 2001-01-01 UTC + id seconds; cleanly addresses
        // both positive and zero offsets so the test can assert on
        // `.UtcDateTime.Year == 2001` after epoch arithmetic.
        return Date(timeIntervalSinceReferenceDate: TimeInterval(id))
    }

    /// Foundation.UUID round-trip — a deterministic UUID built from the
    /// `id`, so the test can assert the exact 16-byte value returned by
    /// the indirect-result wrapper. Validates the
    /// `*(System.Guid*)buffer` cast + finally-Free shape.
    ///
    /// The leading 12 bytes carry a distinct, monotonically increasing
    /// pattern (0x10..0x1B) so a byte-swap regression inside the
    /// little-endian Int32/Int16 fields of System.Guid (bytes[0..3],
    /// bytes[4..5], bytes[6..7]) becomes detectable. The trailing 4
    /// bytes are id-dependent so the test can also pin the parameterized
    /// portion against the factory input.
    public var alphaDeviceVerificationNonce: UUID {
        let bytes: uuid_t = (0x10, 0x11, 0x12, 0x13,
                             0x14, 0x15, 0x16, 0x17,
                             0x18, 0x19, 0x1A, 0x1B,
                             UInt8((Int(id) >> 24) & 0xFF),
                             UInt8((Int(id) >> 16) & 0xFF),
                             UInt8((Int(id) >>  8) & 0xFF),
                             UInt8( Int(id)        & 0xFF))
        return UUID(uuid: bytes)
    }

    /// Foundation.Data round-trip — returns a Data populated with a
    /// known byte pattern. Validates the
    /// `(*(Swift.Foundation.Data*)buffer).ToByteArray()` cast on the C#
    /// side and the `.initializeMemory(as: Foundation.Data.self, ...)`
    /// emission on the Swift side.
    public var alphaHeaderData: Data {
        // Length bound to id; trailing byte = (id & 0xFF). The runtime
        // test can assert both the count and that bytes[count-1] == id.
        let count = max(1, Int(id))
        var bytes = [UInt8](repeating: 0xAB, count: count)
        bytes[count - 1] = UInt8(Int(id) & 0xFF)
        return Data(bytes)
    }
}

// MARK: Per-specialization methods (Fix J — multispec methods)
//
// Layer B coverage for the constrained-extension METHOD path in
// `ConstrainedExtensionEmitter`. Properties cover the
// `*Verification.signedDate` / `.headerData` / etc. surface; methods cover
// the WeatherKit `*Query.temperature()` / MusicKit `MusicLibraryRequest<T>`
// no-arg accessors. Initial method scope is zero-argument sync non-throwing
// (instance and static); methods with parameters, closures, async / throws,
// and mutating / structured-result variants stay out of scope and are
// tracked as Fix J follow-ups.
//
// Both specializations get one instance method (this-extended on the closed
// generic) and one static factory (no `this` — emits on the per-spec
// extensions class). String + primitive return shapes exercise both
// `CEReturnShape.String` (Utf8Slice) and `CEReturnShape.Primitive` for
// methods, mirroring the property-side coverage above.
//
// Void-return on a constrained extension is intentionally NOT covered:
// the emitter currently skips it ("Unsupported: parameter or return type
// not yet supported — unsupported return type"), and the canonical
// WeatherKit static-factory / MusicKit no-arg accessor shapes that the
// narrow J scope targets all return non-Void concrete types. Adding void
// here would surface as a [Skip]-only fixture with no live coverage.

extension Bundle05Container where Key == Bundle05SpecKeyA {
    /// Zero-arg sync non-throwing INSTANCE method, String return — exercises
    /// `TryEmitMethodExtension` + `CEReturnShape.String` at the method site.
    /// Pre-Fix-J this would have been dropped wholesale because
    /// `ConstrainedExtensionEmitter` only iterated `typeDecl.Properties`.
    public func computeAlphaLabel() -> String {
        return "alpha-label-\(id)"
    }

    /// Zero-arg sync non-throwing STATIC method, primitive return —
    /// canonical WeatherKit static-factory shape. Emits on the per-spec
    /// extensions class itself (`SwiftBindingsTestLib_DBundle05Container_SwiftBindingsTestLib_DBundle05SpecKeyAExtensions
    /// .DefaultAlphaRank()`), no `this` receiver. The fixed integer literal
    /// lets the test assert the exact value reached the C# side through the
    /// `@_cdecl` static call.
    public static func defaultAlphaRank() -> Int32 {
        return 17
    }
}

extension Bundle05Container where Key == Bundle05SpecKeyB {
    /// Beta-side instance method, distinct name from alpha to exercise the
    /// straight method-emission path (no name collision, no dedup-skip).
    public func computeBetaLabel() -> String {
        return "beta-label-\(id)"
    }

    /// Beta-side static factory — both alpha and beta static factories must
    /// be independently reachable through their own per-spec mangled symbols.
    public static func defaultBetaRank() -> Int32 {
        return 23
    }
}

// MARK: Open-generic-return carrier (Fix J — payloadValue shape)
//
// `payloadValue` shape coverage: the property/method lives on the
// unconstrained base extension and references the parent's open generic
// parameter. The emitter substitutes the concrete specialization at
// extension-method emit time so each closed-generic instantiation gets a
// typed accessor (`GetCarriedPayload(this Bundle05PayloadCarrier<Concrete>
// self) -> Concrete`). `Bundle05DescriptorPayload` is the concrete because
// it is non-frozen — frozen value-type structs are not yet supported by
// `ExtensionMarshallingHelper.ClassifyReturnType` at the substituted
// return slot.

/// Generic carrier that stores its parameterized value so the
/// open-generic-return property has a real backing field to project. The
/// `init` is unconstrained (any `T`) so the closed factory below can
/// construct a `Bundle05DescriptorPayload`-specialized instance.
//
// `stored` is intentionally `internal`, not `public`: a public storage
// `let` would also be re-surfaced by `FindOpenGenericReturnProperties`,
// adding a `GetStored` C# specialization that duplicates the
// `carriedPayload` fixture's emission path with no extra J coverage. The
// constrained / unconstrained extensions below live in the same module
// so internal access is sufficient for `anchorTag` and `carriedPayload`
// to read it.
public struct Bundle05PayloadCarrier<T> {
    let stored: T
    public init(_ stored: T) {
        self.stored = stored
    }
}

extension Bundle05PayloadCarrier where T == Bundle05DescriptorPayload {
    /// Anchor — `FindOpenGenericReturnProperties` only re-surfaces the
    /// open-generic-return property when at least one constrained
    /// specialization (property OR method) exists for the same type.
    /// Without an anchor the open-generic-return surface stays unreachable
    /// because there is no concrete type to substitute the parent param
    /// with. Returns a String to also exercise `CEReturnShape.String` on
    /// the constrained-extension path for this fixture.
    public var anchorTag: String {
        return "anchor-\(stored.id)"
    }
}

extension Bundle05PayloadCarrier {
    /// Open-generic-return property — `payloadValue` shape. The return spec
    /// is the parent's open generic parameter `T`; the emitter substitutes
    /// `T` with each anchored concrete specialization
    /// (`Bundle05DescriptorPayload` here) at emit time. Pre-Fix-J this
    /// surface skipped under `AnyTypeFallback` because the projected return
    /// was the open generic parameter itself.
    public var carriedPayload: T {
        return stored
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

/// Closed factory for the open-generic-return carrier — produces a
/// `Bundle05PayloadCarrier<Bundle05DescriptorPayload>` so the binding has a
/// reachable closed instantiation to specialize against.
public func makeBundle05PayloadCarrierWithDescriptor(_ id: Int32) -> Bundle05PayloadCarrier<Bundle05DescriptorPayload> {
    return Bundle05PayloadCarrier(Bundle05DescriptorPayload(id: id, label: "carried-\(id)"))
}
