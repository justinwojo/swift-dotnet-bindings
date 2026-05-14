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
