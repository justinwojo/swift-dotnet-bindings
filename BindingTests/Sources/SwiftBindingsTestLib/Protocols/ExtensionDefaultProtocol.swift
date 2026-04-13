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

    /// Computed property with default — not a protocol requirement.
    public var shouldDisplayTip: Bool {
        return true
    }
}

// MARK: - Concrete conformer for testing

/// Concrete type conforming to TipLike, used to verify proxy dispatch.
public class WelcomeTip: TipLike {
    public var tipId: String { return "welcome" }
    public var tipTitle: String { return "Welcome!" }
    public var tipMessage: String? { return "Get started with our app." }
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
