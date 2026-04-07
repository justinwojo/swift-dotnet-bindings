// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Proxy lifetime fixture
//
// Supports BindingTests/RuntimeTestsApp/Lifetime/ProxyLifetimeTests.cs, which
// exercises the impl-anchored EveryProtocol release path introduced by the
// auto-wrap proxy lifetime fix. Kept separate from AutoWrappedDelegate.swift
// so the existing auto-wrap regression tests remain undisturbed.

/// Receiver protocol used by the lifetime harness. Kept to a single blittable
/// method so the generator's protocol proxy is trivially dispatchable on both
/// Mono (simulator) and NativeAOT (device). The C# tests assert liveness via
/// SwiftObjectRegistry.StrongCount rather than relying on callback counts.
public protocol ProxyLifetimeReceiver: AnyObject {
    func ping(value: Int32)
}

/// Harness that stores the receiver in a strong slot and can release it from
/// either the main thread or a background dispatch queue. Mirrors the minimal
/// shape needed by the lifetime tests — deliberately unrelated to
/// AutoWrappedMonitor so unrelated regressions cannot mask lifetime bugs.
public class ProxyLifetimeHarness {
    public var receiver: ProxyLifetimeReceiver?

    public init() {}

    /// One-shot call path: invokes the receiver's ping without storing it.
    /// Used to exercise the "method parameter that Swift does not retain"
    /// scenario — the test drops the impl after this call and expects the
    /// impl-anchored ProxyLifetimeTracker to release the +1 on the next GC.
    public func pingOnce(_ receiver: ProxyLifetimeReceiver, value: Int32) {
        receiver.ping(value: value)
    }

    /// Clears the stored receiver on a background queue. Used by the
    /// cross-thread release test: the final Swift release of the existential
    /// happens on the dispatch queue's worker thread, which means the
    /// EveryProtocol.deinit callback — and therefore the C# reverse-P/Invoke
    /// into OnEveryProtocolDeinit — runs off the main thread. Mono and
    /// NativeAOT have historically had different tolerances for reverse
    /// P/Invoke from arbitrary native threads; this test catches regressions.
    public func clearReceiverOnBackgroundQueue() {
        DispatchQueue.global(qos: .userInitiated).sync {
            // The sync + nil assignment guarantees the release happens on this
            // queue's worker thread, not the caller's thread. The test waits
            // for the method to return before asserting.
            self.receiver = nil
        }
    }
}
