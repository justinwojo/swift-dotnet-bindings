// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Same-named initializers that a default-omitting wrapper's call can never reach, which the
// call-pinning replay must not count as rivals:
//
// - `PinnedInitDerived` repeats its superclass's `required init(value:label:)`. A call on the
//   subclass reaches the subclass's own initializer, so `PinnedInitDerived(value:)` binds even
//   though the two declarations differ in their result type (each initializer returns its own
//   class).
// - `PinnedTransition` has a second initializer in `extension PinnedTransition where Value ==
//   PinnedEndpointA`. A wrapper specialized for `PinnedEndpointB` calls
//   `PinnedTransition<PinnedEndpointB>(to:)`, which that initializer is not a member of. The
//   specialization for `PinnedEndpointA` does meet it, and stays declined.
//
// Every default is computed, so no wrapper can spell it out: the default-omitting member exists
// only when no rival takes the reduced call.

import Foundation

open class PinnedInitBase {
    public static var standardLabel: String { "standard" }

    public let value: Int
    public let label: String
    /// Which declaration ran: 1 = `PinnedInitBase.init`, 2 = `PinnedInitDerived.init`.
    public let initializer: Int32

    public required init(value: Int, label: String = PinnedInitBase.standardLabel) {
        self.value = value
        self.label = label
        self.initializer = 1
    }

    init(value: Int, label: String, initializer: Int32) {
        self.value = value
        self.label = label
        self.initializer = initializer
    }
}

public final class PinnedInitDerived: PinnedInitBase {
    public required init(value: Int, label: String = PinnedInitBase.standardLabel + "-derived") {
        super.init(value: value, label: label, initializer: 2)
    }
}

public protocol PinnedEndpoint {
    var endpointTag: Int { get }
}

public struct PinnedEndpointA: PinnedEndpoint {
    public let endpointTag: Int
    public init(endpointTag: Int) { self.endpointTag = endpointTag }
}

public struct PinnedEndpointB: PinnedEndpoint {
    public let endpointTag: Int
    public init(endpointTag: Int) { self.endpointTag = endpointTag }
}

public enum PinnedTransitionDefaults {
    public static var timing: Int { 40 }
    public static var mode: Int { 7 }
}

public struct PinnedTransition<Value: PinnedEndpoint> {
    /// `plain:<tag>:<timing>` from the unconstrained initializer, `mode:<tag>:<mode>:<timing>` from
    /// the `Value == PinnedEndpointA` one.
    public let summary: String

    public init(to target: Value, timing: Int = PinnedTransitionDefaults.timing) {
        summary = "plain:\(target.endpointTag):\(timing)"
    }
}

extension PinnedTransition where Value == PinnedEndpointA {
    public init(to target: Value, mode: Int = PinnedTransitionDefaults.mode, timing: Int = PinnedTransitionDefaults.timing) {
        summary = "mode:\(target.endpointTag):\(mode):\(timing)"
    }
}
