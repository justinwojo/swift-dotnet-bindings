// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// A generic struct whose unconstrained initializers are re-declared in a
// `where Value == Concrete` extension with one more defaulted parameter, typed by a type nested in
// the generic struct. Swift resolves a call that leaves the defaults out to the more specialized
// extension initializer, so the unconstrained initializers' specializations for that concrete type
// are refused, and the extension initializers bind instead: `FromMotionTransform(to)` and
// `(to, by)` call them. The other conformer still binds the unconstrained initializers. Each
// initializer records its route, so a member bound to the wrong declaration fails.

public protocol MotionAnimatable {}

public struct MotionTransform: MotionAnimatable, Equatable {
    public var x: Double
    public init(x: Double) { self.x = x }
}

public struct MotionScalar: MotionAnimatable {
    public var value: Double
    public init(value: Double) { self.value = value }
}

public struct MotionTiming: Hashable {
    public var rate: Int
    public init(rate: Int) { self.rate = rate }
    public static var linear: MotionTiming { MotionTiming(rate: 1) }
}

public struct MotionFromTo<Value: MotionAnimatable> {
    public enum TransformMode: Equatable {
        case parent
        case local
        public static var `default`: TransformMode { .local }
    }

    public let to: Value?
    public let by: Value?
    public let route: String
    public let timingRate: Int

    public init(from: Value? = nil, to: Value, timing: MotionTiming = .linear, isAdditive: Bool = false) {
        self.to = to; by = nil; timingRate = timing.rate; route = "generic.to"
    }

    public init(to: Value, by: Value, timing: MotionTiming = .linear, isAdditive: Bool = false) {
        self.to = to; self.by = by; timingRate = timing.rate; route = "generic.toBy"
    }
}

extension MotionFromTo where Value == MotionTransform {
    public init(from: Value? = nil, to: Value, mode: MotionFromTo<Value>.TransformMode = .default, timing: MotionTiming = .linear, isAdditive: Bool = false) {
        self.to = to; by = nil; timingRate = timing.rate
        route = mode == .local ? "transform.to.local" : "transform.to.parent"
    }

    public init(to: Value, by: Value, mode: MotionFromTo<Value>.TransformMode = .default, timing: MotionTiming = .linear, isAdditive: Bool = false) {
        self.to = to; self.by = by; timingRate = timing.rate
        route = mode == .local ? "transform.toBy.local" : "transform.toBy.parent"
    }
}
