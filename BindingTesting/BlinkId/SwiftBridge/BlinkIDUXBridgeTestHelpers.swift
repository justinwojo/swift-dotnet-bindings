// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Test-only helper functions for BlinkIDUX bridge validation.
// These are NOT auto-generated — they provide test hooks
// into the auto-generated bridge session classes.

import UIKit
import SwiftUI
import BlinkID
import BlinkIDUX

// MARK: - NoInternetView Test Helpers

/// Fires the retry callback for a NoInternetView session.
/// Used to validate callback wiring without user interaction.
@_cdecl("SBW_TEST_BlinkIDUX_NoInternetView_FireRetry")
public func SBW_TEST_BlinkIDUX_NoInternetView_FireRetry(
    _ handle: UnsafeMutableRawPointer?
) {
    SBW_onMainThread {
        guard let handle = handle,
              SBW_BlinkIDUX_NoInternetView_liveHandles.contains(handle) else { return }
        let session = Unmanaged<SBW_BlinkIDUX_NoInternetView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        // Fire the retry callback via the session's stored callback
        if let cb = session.retryActionCallback {
            let ud = session.retryActionUserData
            DispatchQueue.main.async { cb(ud) }
        }
    }
}

// MARK: - BlinkIDUXView Test Helpers

/// Cancels the analyzer, triggering the result callback with code 2 (cancelled).
@_cdecl("SBW_TEST_BlinkIDUX_BlinkIDUXView_Cancel")
public func SBW_TEST_BlinkIDUX_BlinkIDUXView_Cancel(
    _ handle: UnsafeMutableRawPointer?
) {
    SBW_onMainThread {
        guard let handle = handle,
              SBW_BlinkIDUX_BlinkIDUXView_liveHandles.contains(handle) else { return }
        let session = Unmanaged<SBW_BlinkIDUX_BlinkIDUXView_Session>
            .fromOpaque(handle).takeUnretainedValue()
        Task {
            await session.analyzer.cancel()
        }
    }
}

/// Returns the number of live BlinkIDUXView session handles.
/// Test-only: used to verify no handles are leaked after null-callback or error-path tests.
@_cdecl("SBW_TEST_BlinkIDUX_BlinkIDUXView_LiveHandleCount")
public func SBW_TEST_BlinkIDUX_BlinkIDUXView_LiveHandleCount() -> Int {
    return SBW_onMainThread {
        SBW_BlinkIDUX_BlinkIDUXView_liveHandles.count
    }
}
