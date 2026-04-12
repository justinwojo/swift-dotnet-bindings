// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// MARK: - TipKitSmokeTip (Session 6 — real-framework PAT fallback pin)
//
// Session 6 end-to-end smoke fixture for the Apple-framework direct-mode
// pipeline on TipKit. Gated by the `TIPKIT_SMOKE` compile symbol, which
// Nuke threads in via `-D` on BOTH the dylib compile AND the ABI JSON dump
// (build/Build.BindingTests.cs::CompileModuleSlice). If either side drops
// the define, the compiled C# runtime test file cannot reference
// `TestLibFunctions.ReadTipKitSmokeIdentifier` and the simulator run will
// fail at compile time — that double-sided check is half of the smoke's
// purpose, alongside exercising the PAT-fallback code path on a real
// Apple framework.
//
// Why a synthetic conformer: TipKit ships no publicly-constructible
// `Tip`-conforming type. `Tips.TipGroup.currentTip` and the various
// internal tips require TipKit session configuration + entitlements.
// A hermetic, metadata-only conformer in the test library keeps the
// smoke test free of `Tips.configure(...)`, session state, and network
// calls while still exercising the same code path that consumer projects
// hit when they author their own tips.

#if TIPKIT_SMOKE
import TipKit
import SwiftUI

/// Minimal `TipKit.Tip`-conforming struct. `title` is the only member
/// that lacks a protocol-extension default; everything else
/// (`id`, `message`, `image`, `rules`, `actions`, `options`) is
/// defaulted in the `extension TipKit.Tip` block shipped with the SDK.
@available(iOS 17.0, macOS 14.0, macCatalyst 17.0, tvOS 17.0, *)
public struct TipKitSmokeMinimalTip: Tip {
    public init() {}

    public var title: Text { Text("TipKit smoke title") }
}

/// Free function that takes an existential `TipKit.Tip` at the parameter
/// position. The generator must resolve `TipKit.Tip` through the
/// xcframework type database and lower this signature — the concrete
/// C# lowering is what the C# smoke test pins via reflection. Body
/// dispatches to `.id` (provided by the `Identifiable` conformance and
/// the `extension TipKit.Tip` default) so the runtime-dispatch half of
/// fix #7 has a concrete target to route back to the underlying type.
@available(iOS 17.0, macOS 14.0, macCatalyst 17.0, tvOS 17.0, *)
public func readTipKitSmokeIdentifier(_ tip: any Tip) -> String { tip.id }
#endif
