// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Family-F (`@available` emission) — Layer A coverage
//
// Synthetic mirrors of the five sub-shapes documented in
// `bug-0.10.0-spurious-obsolete-on-recommended-overload.md`. The bug doc
// lists each shape against a real-world library; this file recreates the
// minimum Swift surface area for each so a regression shows up in
// `nuke binding-tests` long before a `nuke validate` sweep would catch it.
//
//   F-1 (Nuke):           Deprecation on one overload must NOT broadcast
//                         to its sibling overloads.
//   F-2 (StripeApplePay): @available on a protocol member without an
//                         explicit `public` modifier must survive — the
//                         pre-fix regex parser dropped it because the
//                         modifier gate skipped bare protocol requirements.
//   F-3 (Lottie):         @available(*, deprecated) on an enum case must
//                         flow through to the lowered C# factory method.
//   F-4 (StoreKit2):      Two overloads with distinct `@available(iOS X.Y, *)`
//                         versions must keep their distinct versions —
//                         pre-fix `AddRange` accumulation merged them.
//   F-5 (MusicKit):       `visionOS` in the `@available` clause must lower
//                         to `[SupportedOSPlatform("visionos1.0")]`. The
//                         pre-fix PlatformMapping table omitted visionOS
//                         and the emitter silently dropped the clause.

// MARK: F-1 — Deprecation NOT broadcast across overload set

/// Two overloads of `lookup(...)`. Only the `String` variant is deprecated;
/// the `Int32` variant is the recommended API and must NOT carry `[Obsolete]`
/// in the lowered C# binding. Pre-fix, the C# emitter looked the deprecation
/// up by `printedName` (`lookup(_:)`) and broadcast `[Obsolete]` to both.
public struct OverloadDeprecationCarrier {
    public init() {}

    /// Recommended overload — must remain `[Obsolete]`-free.
    public func lookup(_ token: Int32) -> Int32 {
        return token * 2
    }

    /// Deprecated overload — only this one carries `[Obsolete]` in C#.
    @available(*, deprecated, message: "Use the Int32 variant of lookup(_:) instead")
    public func lookup(_ label: String) -> Int32 {
        return Int32(label.count)
    }
}

// MARK: F-2 — covered by unit test only
//
// F-2 is `@available` survival on protocol members declared without an
// explicit access modifier (the @objc optional func / bare protocol
// requirement shape from StripeApplePay). It's covered exhaustively by
// the .NET-side unit test
// `GetAvailabilityAnnotations_F2_ProtocolRequirementWithoutAccessModifier`
// in `SwiftInterfaceAccessParserTests.cs` — the parser fix is the entirety
// of the change, and the unit test asserts the full parser-output contract.
//
// A BindingTests fixture for F-2 is intentionally omitted. The auto-emitted
// protocol-proxy class for any `@available`-gated protocol surfaces a
// *separate* generator gap: the proxy class type and its constructors
// don't inherit the protocol's @available, so CA1416 fires on the proxy's
// internal call sites under the `RuntimeTestsApp` iOS-15 baseline. That
// proxy gap is a follow-on to Family-F, not a manifestation of the F-2
// parser bug — adding a F-2 BindingTests fixture would conflate the two.

// MARK: F-3 — Enum-case `@available(*, deprecated)` propagates to factory method

/// Enum lowered to factory-method form in C#. The enclosing enum is
/// available everywhere the package is; only `progressAt(_:)` is
/// deprecated. The C# factory `PlaybackTransport.ProgressAt(double)` (or
/// however the emitter PascalCases it) must carry `[Obsolete]`; the other
/// factory methods must NOT.
///
/// Naming note: the fixture is called `PlaybackTransport` rather than the
/// natural `PlaybackMode` because `PlaybackMode` is already used by
/// `Types/NestedEnums.swift`.
public enum PlaybackTransport {
    case stopped
    case playing(speed: Double)

    @available(*, deprecated, message: "Use frameAt(_:) instead")
    case progressAt(_ value: Double)

    case frameAt(_ value: Int32)
}

/// Round-trip helper so the Swift code is non-trivially exercised at
/// runtime — keeps the case payloads from being optimized away.
public func describePlaybackTransport(_ mode: PlaybackTransport) -> String {
    switch mode {
    case .stopped: return "stopped"
    case .playing(let s): return "playing:\(s)"
    case .progressAt(let v): return "progress:\(v)"
    case .frameAt(let f): return "frame:\(f)"
    }
}

// MARK: F-4 — Distinct `@available` versions on overloads stay distinct

/// Two overloads of `commit(...)` gated to *different* iOS versions. The
/// fix must produce one C# overload with `[SupportedOSPlatform("ios17.0")]`
/// and a second with `[SupportedOSPlatform("ios18.0")]`. Pre-fix, the
/// emitter ran `AddRange` on both versions and broadcast the merged set
/// across both overloads, so a consumer targeting iOS 17 could compile a
/// call to the iOS 18 overload (and crash at runtime on iOS 17).
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public struct VersionedOverloadCarrier {
    public init() {}

    @available(iOS 17.0, macOS 14.0, tvOS 17.0, *)
    public func commit(token: Int32) -> Int32 {
        return token + 17
    }

    @available(iOS 18.0, macOS 15.0, tvOS 18.0, *)
    public func commit(label: String) -> Int32 {
        return Int32(label.count) + 18
    }
}

// MARK: F-5 — visionOS clause survives lowering

/// Type whose `@available` list explicitly names visionOS. The emitted C#
/// type must carry `[SupportedOSPlatform("visionos1.0")]` alongside the
/// other platform attributes. Pre-fix, PlatformMapping had no entry for
/// visionOS and the emitter silently dropped the clause across ~every
/// MusicKit type.
@available(iOS 15.0, macOS 12.0, tvOS 15.0, visionOS 1.0, *)
public struct VisionPlatformCarrier {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func describe() -> String {
        return "vision:\(value)"
    }
}
