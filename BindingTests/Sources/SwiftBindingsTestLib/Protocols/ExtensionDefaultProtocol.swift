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

// MARK: - Read-only extension-default property on a GENERIC conformer (RealityKit FromToByAction<T> shape)
//
// The conformers above (WelcomeTip / EmptyTip) are non-generic, so their extension-default
// getters flow through ProtocolExtensionEmitter's free-function path. A GENERIC conforming type
// takes a DIFFERENT route: the synthetic zero-parameter getter is specialized per concrete type
// argument by the concrete-specialization (CSM) parent-generic emitter
// (ConcreteProtocolSpecializationEmitter). That emitter historically rendered the getter as a
// method CALL — `__self.isReversible()` — calling a Bool value like a function, so swiftc rejected
// the whole wrapper ("cannot call value of non-function type 'Bool'") and the SDK gave up
// (SWIFTBIND051). This is exactly RealityFoundation `FromToByAction<Value>.isReversible` /
// `.isAdditive`. The getter must be READ, not invoked. This fixture keeps the compile gate red if
// that regresses — the wrapper for these specializations won't compile.

/// Constraint protocol whose concrete conformers (below) drive the CSM specialization dimension —
/// one specialization of the generic parent is emitted per conformer. It carries a real
/// requirement (like RealityKit's `AnimatableData`, the shape this fixture mirrors) rather than
/// being an empty marker: an empty constraint protocol has zero vtable slots, so Swift emits no
/// `_vtable` struct while C# still emits a (degenerate) vtable mirror — an incidental
/// `vtable-cs-only` parity asymmetry orthogonal to the CSM property-getter regression under test.
public protocol CsmAnimatableValue {
    var componentCount: Int32 { get }
}

public struct CsmJointValue: CsmAnimatableValue {
    public let componentCount: Int32
    public init() { self.componentCount = 3 }
}

public struct CsmWeightValue: CsmAnimatableValue {
    public let componentCount: Int32
    public init() { self.componentCount = 1 }
}

/// Protocol carrying two read-only extension-default Bool properties (the surfaced synthetic
/// getters). They derive from instance state so the round-trip proves real self-dispatch.
public protocol CsmReversibleAction {
    var stepCount: Int32 { get }
}

extension CsmReversibleAction {
    /// Read-only extension-default Bool property — surfaced as a synthetic getter.
    public var isReversible: Bool { stepCount > 0 }

    /// Second read-only extension-default Bool property — flips independently of the first.
    public var isAdditive: Bool { stepCount % 2 == 0 }
}

/// Generic type conforming to `CsmReversibleAction`. Its concrete specializations
/// (`CsmFromToBy<CsmJointValue>`, `CsmFromToBy<CsmWeightValue>`) route the extension-default
/// getters through the parent-generic CSM path — the code path that regressed.
public struct CsmFromToBy<Value: CsmAnimatableValue>: CsmReversibleAction {
    public let stepCount: Int32
    public let value: Value

    public init(stepCount: Int32, value: Value) {
        self.stepCount = stepCount
        self.value = value
    }
}

/// Builds a `CsmFromToBy<CsmJointValue>` with the given step count — gives the C# side a concrete
/// specialization to exercise without needing to name the open generic.
public func makeJointAction(stepCount: Int32) -> CsmFromToBy<CsmJointValue> {
    return CsmFromToBy(stepCount: stepCount, value: CsmJointValue())
}
