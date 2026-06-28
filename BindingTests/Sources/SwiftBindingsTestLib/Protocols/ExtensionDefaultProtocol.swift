// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Extension Default Protocol Pattern (TipKit.Tip analogue)

/// Protocol with required properties and extension default methods.
/// Tests that extension default methods that might fail ABI parsing
/// don't block proxy generation via MissingRequirements.
/// This pattern matches TipKit.Tip which has property requirements
/// (title, message, etc.) and extension defaults (invalidate, resetEligibility).
public protocol TipLike {
    /// Required string property (like Tip.id)
    var tipId: String { get }

    /// Required description property (like Tip.title but without SwiftUI.Text)
    var tipTitle: String { get }

    /// Required optional property (like Tip.message)
    var tipMessage: String? { get }
}

/// Extension providing default implementations for non-required convenience methods.
/// These are NOT protocol requirements — conforming types inherit them automatically.
extension TipLike {
    /// Default implementation — not a protocol requirement.
    public func invalidateTip() {
        // no-op default
    }

    /// Default implementation — not a protocol requirement.
    public func resetTipEligibility() {
        // no-op default
    }

    /// Read-only computed property default — not a protocol requirement.
    /// Surfaced on concrete conformers as a synthetic getter method (GetShouldDisplayTip()).
    /// Derives from instance state so the round-trip proves real self-dispatch, not a constant.
    public var shouldDisplayTip: Bool {
        return !tipId.isEmpty
    }

    /// Second read-only default with a non-Bool primitive return — proves the synthetic
    /// getter round-trips an arbitrary value, not just a boolean.
    public var tipPriorityScore: Int32 {
        return Int32(tipId.count)
    }

    /// Effectful (`get throws`) extension-default getter — must be DROPPED, not surfaced.
    /// A synthetic free-function getter wrapper can't honor the `throws` effect, so
    /// emitting one would produce an invalid non-throwing wrapper around a throwing
    /// member and fail wrapper compilation. The swiftinterface printer emits the
    /// `throws` keyword on its own accessor line, so the drop must happen on the
    /// structured accessor AST (it's invisible to a first-line-only signature scan).
    /// Its presence here keeps the compile gate red if that drop ever regresses.
    public var throwingPriorityScore: Int32 {
        get throws { return Int32(tipId.count) }
    }
}

// MARK: - Concrete conformers for testing

/// Concrete type conforming to TipLike, used to verify proxy dispatch.
/// Non-empty tipId → shouldDisplayTip == true, tipPriorityScore == 7.
public class WelcomeTip: TipLike {
    public var tipId: String { return "welcome" }
    public var tipTitle: String { return "Welcome!" }
    public var tipMessage: String? { return "Get started with our app." }
}

/// Second conformer whose empty tipId flips the state-derived defaults — gives the
/// extension-default getters a discriminating (false / 0) case, not a constant.
public class EmptyTip: TipLike {
    public var tipId: String { return "" }
    public var tipTitle: String { return "" }
    public var tipMessage: String? { return nil }
}

// MARK: - Consumer that accepts any TipLike (existential parameter)

/// Accepts a TipLike existential, testing proxy auto-wrapping.
public func getTipTitle(_ tip: any TipLike) -> String {
    return tip.tipTitle
}

/// Accepts a TipLike existential and returns its message.
public func getTipMessage(_ tip: any TipLike) -> String? {
    return tip.tipMessage
}

/// Returns a concrete WelcomeTip for testing.
public func createWelcomeTip() -> WelcomeTip {
    return WelcomeTip()
}

/// Returns a concrete EmptyTip for testing the false / zero getter case.
public func createEmptyTip() -> EmptyTip {
    return EmptyTip()
}
