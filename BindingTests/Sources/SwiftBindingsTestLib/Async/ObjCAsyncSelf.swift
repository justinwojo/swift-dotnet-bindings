// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async instance methods on an @objc:NSObject self (issue #40)
//
// When an async instance method lives on a Swift class, the generated async wrapper keeps
// `self` alive across the Task continuation by retaining the self pointer into the call
// holder and releasing it in the completion callback. For a pure-Swift self that retain/
// release is swift_retain/swift_release; for an `@objc … : NSObject`-rooted self it MUST be
// the isa-dispatching swift_unknownObjectRetain / swift_unknownObjectRelease (objc_retain /
// objc_release under the hood). Native swift_retain on an NSObject-rooted self touches the
// wrong refcount word — the self can be deallocated out from under the in-flight
// continuation (use-after-free) or its count skewed.
//
// `ObjCAsyncSelf` feeds the shared LifetimeTracker counters in init/deinit so the C# side can
// assert ARC balance of `self` across the await boundary, not merely the absence of a crash.

/// `@objc … : NSObject` class with async instance methods (with and without parameters), so
/// the generated wrapper exercises both the with-params and no-params self-retain branches.
@objc public class ObjCAsyncSelf: NSObject {
    public let base: Int32

    public init(base: Int32) {
        self.base = base
        recordTrackedAllocation()
        super.init()
    }

    deinit {
        recordTrackedDeallocation()
    }

    /// Async instance method WITH parameters — exercises the with-params self-retain branch.
    public func computeAsync(factor: Int32) async -> Int32 {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return base * factor
    }

    /// Async instance method WITHOUT parameters — exercises the no-params self-retain branch.
    public func pingAsync() async -> Int32 {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return base
    }
}
