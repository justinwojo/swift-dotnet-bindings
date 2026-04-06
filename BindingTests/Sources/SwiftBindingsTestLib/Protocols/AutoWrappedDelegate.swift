// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Auto-wrapped delegate regression
//
// Covers justinwojo/swift-dotnet-bindings#16 (GDPerformanceView repro): users assign
// a plain C# class implementing the generated protocol interface to a `delegate`
// property. Before the fix the setter's GetOrCreate call threw InvalidCastException
// because the C# value implemented neither ISwiftExistentialConvertible nor
// IExistentialBoxable. The generator now emits an auto-wrap fallback that constructs
// the hidden {Protocol}Proxy transparently, so these patterns must all round-trip
// without the test ever mentioning AutoWrappedMonitorDelegateProxy.

/// Class-bound delegate protocol — the same shape Swift APIs use for observers
/// (Cocoa delegate pattern, GDPerformanceView's PerformanceMonitorDelegate, etc.).
public protocol AutoWrappedMonitorDelegate: AnyObject {
    /// Called by the monitor with a scalar report value.
    func monitorDidUpdate(value: Int32)
}

/// Monitor class with a `weak var delegate` property (exactly the reporter's shape).
///
/// `weak` is the idiomatic Swift pattern but irrelevant to the bug: both `weak` and
/// strong delegate properties flow through the same generated setter. The
/// {Protocol}Proxy self-roots via SwiftObjectRegistry.RegisterStrong, so callbacks
/// keep firing even though Swift only holds a weak reference.
public class AutoWrappedMonitor {
    public weak var delegate: AutoWrappedMonitorDelegate?
    public var strongDelegate: AutoWrappedMonitorDelegate?
    private var counter: Int32 = 0

    /// Tracks which delegate slot serviced the most recent `fire()` call:
    /// 0 = no slot fired (proxy was collected before Swift could dispatch)
    /// 1 = weak `delegate` slot fired
    /// 2 = strong `strongDelegate` slot fired
    /// Exposed so the C# tests can prove which path actually serviced a call —
    /// otherwise a strong-delegate fallback would silently mask a regression in
    /// the weak path's proxy lifetime.
    public var lastNotifiedSlot: Int32 = 0

    public init() {}

    /// Constructor-parameter path: accepts an existential at init time. Uses
    /// strong storage (via a second property) so the C# test can drop its local
    /// reference and still receive callbacks.
    public init(initialDelegate: AutoWrappedMonitorDelegate) {
        self.delegate = initialDelegate
        self.strongDelegate = initialDelegate
    }

    /// Triggers the delegate callback via the **weak** `delegate` slot only.
    /// Deliberately does NOT fall back to `strongDelegate`: a regression in the
    /// weak-slot proxy lifetime must surface as `lastNotifiedSlot == 0`, not be
    /// silently masked by a strong fallback. Returns Void (not Int32) so the
    /// generator emits `Fire` as an imperative method, not a `GetFire` getter.
    public func fire(step: Int32 = 1) {
        counter += step
        if let d = delegate {
            lastNotifiedSlot = 1
            d.monitorDidUpdate(value: counter)
        } else {
            lastNotifiedSlot = 0
        }
    }

    /// Strong-slot variant: notifies via `strongDelegate` exclusively. Lets tests
    /// that explicitly want strong-storage semantics avoid implicit reliance on
    /// the weak slot.
    public func fireStrong(step: Int32 = 1) {
        counter += step
        if let d = strongDelegate {
            lastNotifiedSlot = 2
            d.monitorDidUpdate(value: counter)
        } else {
            lastNotifiedSlot = 0
        }
    }

    /// The most-recently-fired counter value. Exposes the side-effect of `fire(step:)`
    /// for the C# test assertions.
    public var lastFiredValue: Int32 {
        return counter
    }

    /// Method-parameter path: pass a one-shot delegate directly into a function
    /// call (not stored). This exercises the MethodSignature.GetCallArgumentString
    /// emit site alongside the property setter path above.
    public func fireOnce(_ delegate: AutoWrappedMonitorDelegate, value: Int32) {
        delegate.monitorDidUpdate(value: value)
    }
}

// MARK: - Multi-protocol auto-wrap regression

/// Second class-bound delegate protocol used to verify the auto-wrap proxy cache
/// is keyed per-protocol, not just per-impl. A single C# class can implement
/// `IAutoWrappedMonitorDelegate` AND `AutoWrappedSecondaryDelegate` simultaneously,
/// and each protocol needs its own proxy instance with its own protocol witness
/// table — coalescing them would call the wrong vtable on the Swift side.
public protocol AutoWrappedSecondaryDelegate: AnyObject {
    /// Distinct method name so the C# implementation can record which protocol
    /// path actually serviced the call.
    func secondaryDidNotify(value: Int32)
}

/// Container that exposes both protocols as separate setter properties so a
/// single C# instance can be assigned to both. After assignment, calling
/// `fireBoth(value:)` exercises both witness tables and the test asserts that
/// both methods were dispatched on the same managed implementation.
public class AutoWrappedDualMonitor {
    public weak var primary: AutoWrappedMonitorDelegate?
    public weak var secondary: AutoWrappedSecondaryDelegate?

    public init() {}

    /// Dispatches via both witness tables in sequence. Both proxies must point
    /// at the same managed implementation, but each must use its own witness
    /// table — the cache must NOT return the same proxy/container for both.
    public func fireBoth(value: Int32) {
        primary?.monitorDidUpdate(value: value)
        secondary?.secondaryDidNotify(value: value)
    }
}
