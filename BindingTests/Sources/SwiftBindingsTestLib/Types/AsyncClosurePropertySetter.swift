// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// Regression: Stripe's StripePaymentSheet.ConfirmHandler shape. A setter-only
// property whose Swift type is Optional<closure-returning-Task>. Before the
// PropertyHandler skip, the generator emitted an async-throwing P/Invoke that
// referenced an undeclared `valueHandle` and typed the parameter as
// Swift.AnyType (asyncBridgeEligible is false on accessor frames). The async
// bridge synthesizes Swift closures for *invocation* inside an async outer
// method frame — it cannot construct a stored async closure value from a C#
// (funcPtr, context) pair via a sync property setter.
//
// These properties are intentionally non-bindable. The fixture exists to lock
// in the skip behaviour: the surrounding type still binds, the property
// reports as skipped (UnsupportedClosure), and no broken P/Invoke or
// marshalling code lands in the generated C#.

public class AsyncClosurePropertySetterHolder {
    // Non-baseline async-throwing closure (Bool arg places it outside the
    // baseline GetAsyncThrowingArgCategory set, but the property still must
    // not emit broken code).
    public var confirmHandler: ((Int32, Bool) async throws -> String)?

    // Baseline-shape async-throwing closure: still unsupportable as a stored
    // property because Swift has no construction path from a C# function
    // pointer for an async closure value.
    public var primitiveHandler: ((Int32) async throws -> Int32)?

    // Async non-throwing closure with primitive return — the non-throwing
    // baseline shape from the bridge. Same storage limitation applies.
    public var factory: (() async -> Int32)?

    public init() {
        self.confirmHandler = nil
        self.primitiveHandler = nil
        self.factory = nil
    }

    // Non-async closure property — must continue to bind. Acts as a regression
    // baseline so the broader closure-property path stays green.
    public var observer: ((Int32) -> Void)?

    public func triggerObserver(_ value: Int32) {
        observer?(value)
    }
}

// Stripe's StripePaymentSheet.ConfirmHandler shape: an async-closure stored
// property living on a *nested* type. Round-1 (commit 2ca32c33) added an
// `if (closureTypeSpec.IsAsync) skip` predicate to PropertyHandler but only
// exercised it via flat-class unit tests; Stripe's `ConfirmHandler` lives on
// `PaymentSheet.IntentConfiguration` and continued to emit `Swift.AnyType` on
// the P/Invoke setter. This fixture pins the maximum case for round 2: the
// nested-type setter-only / nested-type sibling-non-async / nested-type async
// throwing combinations must all skip cleanly while the non-async sibling on
// the same nested type keeps binding.

// Helper concrete class used as a closure parameter (mirrors Stripe's
// STPPaymentMethod argument — a non-blittable reference type).
public class AsyncClosureNestedArg {
    public let label: String
    public init(_ label: String) { self.label = label }
}

public class AsyncClosurePropertySetterOuter {
    // Struct-nested-in-class — Stripe's `PaymentSheet.IntentConfiguration`
    // is a struct nested inside a class. The struct path runs through
    // FrozenStructHandler / NonFrozenStructHandler (not ClassHandler), and
    // the round-1 fix at PropertyHandler must fire on that emit path too.
    public struct IntentConfigurationNested {
        // Maximum case: stored property whose closure type matches Stripe's
        // ConfirmHandler exactly — a class arg + Bool arg + async throwing
        // String return.
        public var confirmHandler: ((AsyncClosureNestedArg, Bool) async throws -> String)?

        // Setter-only stored property: explicit `set` accessor with no
        // public getter. PropertyHandler must skip this nested-type
        // setter-only async property cleanly without emitting a
        // Swift.AnyType P/Invoke.
        private var _setterOnlyHandler: (() async -> Int32)? = nil
        public var setterOnlyHandler: (() async -> Int32)? {
            set { _setterOnlyHandler = newValue }
            get { return nil }
        }

        // Async non-throwing variant on the same nested type.
        public var asyncNonThrowingFactory: (() async -> Int32)?

        // Sibling non-async closure on the SAME nested type — must continue
        // to bind. Acts as a regression baseline so the nested-type async
        // skip doesn't over-fire and drop unrelated closure properties.
        public var siblingNonAsyncObserver: ((Int32) -> Void)?

        public init() {
            self.confirmHandler = nil
            self._setterOnlyHandler = nil
            self.asyncNonThrowingFactory = nil
            self.siblingNonAsyncObserver = nil
        }

        public mutating func triggerSiblingObserver(_ value: Int32) {
            siblingNonAsyncObserver?(value)
        }
    }

    // Outer class must still bind so consumers can reach the nested type.
    public init() {}

    // Factory exposing the nested struct — Stripe's IntentConfiguration is
    // surfaced through a containing class's API.
    public func makeIntentConfiguration() -> IntentConfigurationNested {
        return IntentConfigurationNested()
    }
}
