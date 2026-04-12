// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - AvailabilityPropagation (fixes #1, #2, #12)
//
// Synthetic mirror of the Apple-framework availability shapes exercised by
// fixes b51d2ff6 (#1), fcd0ca9c/eafe252d/52ddafa4 (#2), and 26f764f1 (#12).
// The Session 3 WeatherKit smoke already pins fix #1 at runtime on a real
// `DayWeather.HighTemperatureTime` property; this file is the synthetic
// counterpart so a regression shows up in `nuke binding-tests` long before
// the WeatherKit snapshot is re-generated.
//
// The guarantees each shape below must establish in the generated C#:
//
//   1. Accessor-level @available(iOS 18, *) on a property whose enclosing
//      type is @available(iOS 16, *) produces a property with the
//      strictly-tighter [SupportedOSPlatform("ios18.0")] attribute. Without
//      fix #1 the emitted property would inherit only the enclosing type's
//      ios16.0 floor and a consumer could call the property from iOS 16
//      with no CA1416 diagnostic.
//
//   2. Per-case @available on an enum produces the matching
//      [SupportedOSPlatform] annotation on the emitted C# enum field. Before
//      fix #2 the case-level attribute was dropped entirely, so a consumer
//      could switch on a future-only case at the enclosing type's floor.
//
//   3. The @_silgen_name wrapper that the generator emits for an extension
//      method must inherit the @available(iOS X, *) of the extension itself,
//      not just the method. Fix #12 (26f764f1) moved the @available line to
//      the `extension ... {` line in the generated Swift wrapper so the
//      wrapper compiles under an iOS 18 SDK that gated the extended type.

// MARK: Fix #1 — Accessor-tighter-than-type property

/// Payload type that only exists at iOS 18. Used as the return type of an
/// iOS 16 struct's iOS 18 property — the property accessor must carry a
/// tighter availability than the enclosing type.
@available(iOS 18.0, macOS 15.0, tvOS 18.0, *)
public struct FuturePayload {
    public let value: Int32

    public init(value: Int32) {
        self.value = value
    }

    public func describe() -> String {
        return "FuturePayload(\(value))"
    }
}

/// Enclosing type available at iOS 16. The `futurePayload` property has an
/// accessor-level @available tightening it to iOS 18, mirroring the real
/// `WeatherKit.DayWeather.highTemperatureTime` shape.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public struct VersionedContainer {
    public let label: String

    public init(label: String) {
        self.label = label
    }

    /// Property whose getter is gated to iOS 18 even though the enclosing
    /// type is iOS 16. Fix #1 must propagate the tighter floor to the
    /// emitted C# property.
    @available(iOS 18.0, macOS 15.0, tvOS 18.0, *)
    public var futurePayload: FuturePayload {
        return FuturePayload(value: 18)
    }

    /// Plain iOS 16 method used to prove the rest of the type still works
    /// when the reflection-based availability assertion runs.
    public func greet() -> String {
        return "hello from \(label)"
    }
}

// MARK: Fix #2 — Per-case enum availability

/// Enum with staged availability per case. The enclosing enum is iOS 16; two
/// of the cases have tighter per-case availability. The emitted C# enum
/// fields must carry matching [SupportedOSPlatform] attributes — without
/// fix #2 the per-case attributes were silently dropped and a consumer
/// could switch on a future-only case without a CA1416 diagnostic.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public enum StagedFeature: Int32 {
    case legacy = 0

    @available(iOS 17.0, macOS 14.0, tvOS 17.0, *)
    case enhanced = 1

    @available(iOS 18.0, macOS 15.0, tvOS 18.0, *)
    case experimental = 2
}

/// Returns the raw value of a StagedFeature. Runs at the iOS 16 baseline so
/// the test can call it without crashing on the simulator's SDK floor.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public func stagedFeatureRawValue(_ feature: StagedFeature) -> Int32 {
    return feature.rawValue
}

// MARK: Fix #12 — @_silgen_name wrapper inherits @available-gated extension

/// Base type available at iOS 16. A matching iOS 18 extension below adds a
/// method — the generator must emit the @_silgen_name wrapper inside an
/// extension block whose own @available line matches iOS 18, not the type's
/// iOS 16 floor. Before fix #12 the wrapper inherited only the method's
/// @available attribute, and the extended type could reference an iOS 18
/// symbol under an iOS 16 guard, failing to compile.
@available(iOS 16.0, macOS 13.0, tvOS 16.0, *)
public struct AvailabilityBase {
    public let label: String

    public init(label: String) {
        self.label = label
    }
}

@available(iOS 18.0, macOS 15.0, tvOS 18.0, *)
extension AvailabilityBase {
    /// Extension method on an iOS 18-gated extension. The emitted Swift
    /// wrapper must wrap this in `@available(iOS 18, *) extension ... { ... }`
    /// or it will fail to compile on an iOS 16 floor.
    public func futureExtensionMethod() -> String {
        return "future:\(label)"
    }
}
