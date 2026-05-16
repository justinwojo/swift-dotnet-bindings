// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Legacy SwiftClosureData escaping-closure lifetime fixture
//
// Supports BindingTests/RuntimeTestsApp/Lifetime/EscapingClosureLifetimeTests.cs.
// Reproduces the Nuke ImagePipeline.loadData(didReceiveData:) shape — a sync
// method that takes an `@escaping (Int32) -> Void` closure and stores it for
// later invocation. Without the `_SBClosureCtx` owner-token plumbing on the
// legacy SwiftClosureData path, the C# delegate's GCHandle leaks for the
// lifetime of the process because Swift's release of the stored closure has
// no notification channel back to managed code.
//
// The harness exposes `clearStreamingCallback()` so the test can force Swift
// to release the closure (and therefore the box), then GC + finalize to
// observe whether the underlying managed delegate becomes collectible.

/// Holds an escaping `(Int32) -> Void` callback in a stored property so Swift
/// retains it past the call boundary — exactly the shape that flows through
/// the legacy SwiftClosureData escaping path in generated bindings.
public final class StreamingCallbackHarness {
    private var streamingCallback: ((Int32) -> Void)?

    public init() {}

    /// Stores the callback for later use. The closure outlives this call.
    public func setStreamingCallback(_ callback: @escaping (Int32) -> Void) {
        self.streamingCallback = callback
    }

    /// Fires the stored callback once with the supplied value (or no-op if
    /// none is set). Lets the test observe that the closure remains valid
    /// across calls without being collected prematurely.
    public func fire(value: Int32) {
        self.streamingCallback?(value)
    }

    /// Drops Swift's strong reference to the stored callback. After this
    /// returns, Swift's ARC releases the `_SBClosureCtx` box (when present),
    /// firing its deinit which upcalls the C# free trampoline and frees the
    /// captured GCHandle. The test then GC + finalize-queue spins to confirm
    /// the managed delegate becomes collectible.
    public func clearStreamingCallback() {
        self.streamingCallback = nil
    }
}
