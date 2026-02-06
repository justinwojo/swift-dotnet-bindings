// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import UIKit
import SwiftUI
import BlinkIDUX

/// C function pointer type for the retry callback.
public typealias RetryCallbackFn = @convention(c) (UnsafeMutableRawPointer?) -> Void

/// Holds a UIHostingController<NoInternetView> and the retry callback wiring.
final class NoInternetSession {
    let hostingController: UIHostingController<NoInternetView>
    private let retryCallback: RetryCallbackFn?
    private let userData: UnsafeMutableRawPointer?

    init(retryCallback: RetryCallbackFn?,
         userData: UnsafeMutableRawPointer?) {
        self.retryCallback = retryCallback
        self.userData = userData

        let cb = retryCallback
        let ud = userData
        let view = NoInternetView(retryAction: {
            DispatchQueue.main.async {
                cb?(ud)
            }
        })
        self.hostingController = UIHostingController(rootView: view)
    }

    /// Fires the stored retry callback asynchronously on the main queue,
    /// matching the production NoInternetView retryAction dispatch path.
    /// No-op if the callback was null at creation time.
    func fireRetry() {
        guard let cb = retryCallback else { return }
        let ud = userData
        DispatchQueue.main.async {
            cb(ud)
        }
    }
}

// MARK: - Handle tracking

/// Tracks live session handles so stale/null/double-freed pointers
/// return nil instead of causing undefined behavior.
/// All access is serialized on the main thread via onMainThread().
private var liveHandles = Set<UnsafeMutableRawPointer>()

/// Runs the given block on the main thread. If already on main,
/// executes immediately. Otherwise uses dispatch_sync.
@discardableResult
private func onMainThread<T>(_ block: () -> T) -> T {
    if Thread.isMainThread {
        return block()
    }
    return DispatchQueue.main.sync { block() }
}

/// Validates a handle is non-null and tracked. Returns the session
/// if valid, nil otherwise. Must be called on the main thread.
private func validateHandle(_ handle: UnsafeMutableRawPointer?) -> NoInternetSession? {
    guard let handle = handle, liveHandles.contains(handle) else {
        return nil
    }
    return Unmanaged<NoInternetSession>.fromOpaque(handle).takeUnretainedValue()
}

// MARK: - Exported C functions

@_cdecl("SBW_BlinkIDUX_NoInternetView_Create")
public func SBW_BlinkIDUX_NoInternetView_Create(
    _ retryCallback: RetryCallbackFn?,
    _ userData: UnsafeMutableRawPointer?
) -> UnsafeMutableRawPointer? {
    return onMainThread {
        let session = NoInternetSession(retryCallback: retryCallback, userData: userData)
        let handle = Unmanaged.passRetained(session).toOpaque()
        liveHandles.insert(handle)
        return handle
    }
}

@_cdecl("SBW_BlinkIDUX_NoInternetView_GetViewController")
public func SBW_BlinkIDUX_NoInternetView_GetViewController(
    _ handle: UnsafeMutableRawPointer?
) -> UnsafeMutableRawPointer? {
    return onMainThread {
        guard let session = validateHandle(handle) else { return nil }
        return Unmanaged.passUnretained(session.hostingController).toOpaque()
    }
}

@_cdecl("SBW_BlinkIDUX_NoInternetView_Free")
public func SBW_BlinkIDUX_NoInternetView_Free(
    _ handle: UnsafeMutableRawPointer?
) {
    onMainThread {
        guard let handle = handle, liveHandles.remove(handle) != nil else { return }
        Unmanaged<NoInternetSession>.fromOpaque(handle).release()
    }
}

// MARK: - Test-only helper

@_cdecl("SBW_TEST_BlinkIDUX_NoInternetView_FireRetry")
public func SBW_TEST_BlinkIDUX_NoInternetView_FireRetry(
    _ handle: UnsafeMutableRawPointer?
) {
    onMainThread {
        guard let session = validateHandle(handle) else { return }
        session.fireRetry()
    }
}
