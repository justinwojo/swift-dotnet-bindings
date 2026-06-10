// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Control Hierarchy Pattern
// Tests the pattern where UIControl subclasses have:
// - Bool state properties (isOn)
// - State change methods with parameters (setIsOn(animated:shouldFireHaptics:))
// - Inherited play/stop behavior from a base class
// - Optional animation in constructor

/// Base animated control — base class in a two-level UIControl subclass hierarchy.
public class AnimatedControlBase {
    public var animationName: String?
    public var speed: Double = 1.0
    public var isAnimating: Bool = false

    public init() {
        self.animationName = nil
    }

    public init(animationName: String?) {
        self.animationName = animationName
    }

    /// Start playing the animation.
    public func play() {
        isAnimating = true
    }

    /// Stop the animation.
    public func stop() {
        isAnimating = false
    }

    /// Current state description.
    public func stateDescription() -> String {
        let name = animationName ?? "none"
        return "anim=\(name), playing=\(isAnimating), speed=\(speed)"
    }
}

/// Toggle switch control — subclass with isOn state and animation.
public final class ToggleSwitch: AnimatedControlBase {
    public var isOn: Bool = false
    private var _onChangeCount: Int32 = 0

    public override init() {
        super.init()
    }

    public init(animationName: String?, initialState: Bool) {
        super.init(animationName: animationName)
        self.isOn = initialState
    }

    /// Set the toggle state, optionally with animation.
    /// Models: AnimatedSwitch.setIsOn(_:animated:shouldFireHaptics:)
    public func setIsOn(_ isOn: Bool, animated: Bool, shouldFireHaptics: Bool = true) {
        let changed = self.isOn != isOn
        self.isOn = isOn
        if changed {
            _onChangeCount += 1
        }
        if animated {
            play()
        }
    }

    /// Number of times the state has changed.
    public var changeCount: Int32 {
        return _onChangeCount
    }

    /// Configure the on/off frame ranges.
    /// Models: AnimatedSwitch.setProgressForState(fromProgress:toProgress:state:)
    public func setProgressForState(fromProgress: Double, toProgress: Double, isOnState: Bool) -> String {
        let stateName = isOnState ? "on" : "off"
        return "\(stateName): \(fromProgress)->\(toProgress)"
    }
}

/// Tap button control — subclass with tap-count tracking.
public final class TapButton: AnimatedControlBase {
    public var tapCount: Int32 = 0

    public override init() {
        super.init()
    }

    public override init(animationName: String?) {
        super.init(animationName: animationName)
    }

    /// Simulate a button tap.
    public func performTap() {
        tapCount += 1
        play()
    }

    /// Whether the button is currently enabled.
    public var isEnabled: Bool {
        return animationName != nil
    }
}
