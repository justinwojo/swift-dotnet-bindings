// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import UIKit
import SwiftUI
import BlinkID
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

// MARK: - BlinkIDUXView Bridge (Step 2)

/// C function pointer type for the async factory ready callback.
/// Parameters: (handle, userData)
public typealias ReadyCallbackFn = @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer?) -> Void

/// C function pointer type for the async factory error callback.
/// Parameters: (messagePtr, messageLen, userData)
public typealias ErrorCallbackFn = @convention(c) (UnsafePointer<UInt8>, Int, UnsafeMutableRawPointer?) -> Void

/// C function pointer type for the scanning result callback.
/// Parameters: (resultCode, userData) — codes: 0=completed, 1=interrupted, 2=cancelled, 3=ended
public typealias ResultCallbackFn = @convention(c) (Int32, UnsafeMutableRawPointer?) -> Void

/// Holds the full BlinkIDUXView object graph: SDK, analyzer, model, and hosting controller.
final class BlinkIDUXSession {
    let sdk: BlinkIDSdk
    let eventStream: BlinkIDEventStream
    let analyzer: BlinkIDAnalyzer
    let model: BlinkIDUXModel
    let hostingController: UIHostingController<BlinkIDUXView>
    private var resultTask: Task<Void, Never>?

    @MainActor
    init(sdk: BlinkIDSdk,
         eventStream: BlinkIDEventStream,
         analyzer: BlinkIDAnalyzer,
         model: BlinkIDUXModel) {
        self.sdk = sdk
        self.eventStream = eventStream
        self.analyzer = analyzer
        self.model = model

        let view = BlinkIDUXView(viewModel: model)
        self.hostingController = UIHostingController(rootView: view)
    }

    /// Starts a background task that awaits the analyzer result and fires the callback.
    ///
    /// Must be called after the session is retained and tracked in `liveBlinkIDUXHandles`.
    /// The callback is gated on `liveBlinkIDUXHandles.contains(handle)` — checked
    /// synchronously on @MainActor right before invocation. Since Free removes the
    /// handle from the set on the same main thread, the two operations are serialized
    /// and the callback cannot fire after Free completes.
    @MainActor
    func startResultMonitor(handle: UnsafeMutableRawPointer,
                            resultCallback: ResultCallbackFn?,
                            userData: UnsafeMutableRawPointer?) {
        let analyzerRef = analyzer
        let cb = resultCallback
        let ud = userData
        let sessionHandle = handle
        self.resultTask = Task { @MainActor in
            let result = await analyzerRef.result()
            guard !Task.isCancelled else { return }
            // Gate on handle still being tracked. Both this check and
            // Free's liveBlinkIDUXHandles.remove run on main thread, so they
            // are serialized — no race.
            guard liveBlinkIDUXHandles.contains(sessionHandle) else { return }
            let code: Int32
            switch result {
            case .completed: code = 0
            case .interrupted: code = 1
            case .cancelled: code = 2
            case .ended: code = 3
            @unknown default: code = -1
            }
            cb?(code, ud)
        }
    }

    func cancelResultMonitor() {
        resultTask?.cancel()
        resultTask = nil
    }
}

// MARK: - BlinkIDUXView Handle tracking

private var liveBlinkIDUXHandles = Set<UnsafeMutableRawPointer>()

private func validateBlinkIDUXHandle(_ handle: UnsafeMutableRawPointer?) -> BlinkIDUXSession? {
    guard let handle = handle, liveBlinkIDUXHandles.contains(handle) else {
        return nil
    }
    return Unmanaged<BlinkIDUXSession>.fromOpaque(handle).takeUnretainedValue()
}

// MARK: - BlinkIDUXView Exported C functions

/// Creates a BlinkIDUXView scanning session asynchronously.
///
/// Because BlinkIDAnalyzer.init is async throws, this function returns void
/// and calls onReady(handle, userData) on success or onError(msgPtr, msgLen, userData) on failure.
///
/// `onReady` is required — if null, the function is a no-op (no session is created and
/// the handle would leak since the caller has no way to free it).
///
/// Parameters:
/// - licenseKeyPtr/Len: UTF-8 encoded BlinkID license key
/// - showIntroductionAlert, showHelpButton, allowHapticFeedback: ScanningUXSettings (0=false, nonzero=true)
/// - preferFrontCamera: Camera position (0=back, nonzero=front)
/// - onReady: Required. Called with (handle, userData) on successful session creation
/// - onError: Called with (UTF-8 message pointer, length, userData) on failure. May be null.
/// - onResult: Called with (resultCode, userData) when scanning completes. May be null.
/// - userData: Opaque context pointer passed through to all callbacks
@_cdecl("SBW_BlinkIDUX_BlinkIDUXView_Create")
public func SBW_BlinkIDUX_BlinkIDUXView_Create(
    _ licenseKeyPtr: UnsafePointer<UInt8>?,
    _ licenseKeyLen: Int,
    _ showIntroductionAlert: Int32,
    _ showHelpButton: Int32,
    _ allowHapticFeedback: Int32,
    _ preferFrontCamera: Int32,
    _ onReady: ReadyCallbackFn?,
    _ onError: ErrorCallbackFn?,
    _ onResult: ResultCallbackFn?,
    _ userData: UnsafeMutableRawPointer?
) {
    // onReady is required — without it the caller can never receive the handle,
    // so the session would leak. Bail out immediately.
    guard let onReady = onReady else { return }

    // Copy the license key immediately (pointer may be transient)
    let licenseKey: String
    if let ptr = licenseKeyPtr, licenseKeyLen > 0 {
        licenseKey = String(
            bytes: UnsafeBufferPointer(start: ptr, count: licenseKeyLen),
            encoding: .utf8
        ) ?? ""
    } else {
        licenseKey = ""
    }

    let uxSettings = ScanningUXSettings(
        showIntroductionAlert: showIntroductionAlert != 0,
        showHelpButton: showHelpButton != 0,
        preferredCameraPosition: preferFrontCamera != 0 ? .front : .back,
        allowHapticFeedback: allowHapticFeedback != 0
    )

    Task { @MainActor in
        do {
            let sdkSettings = BlinkIDSdkSettings(licenseKey: licenseKey)
            let sdk = try await BlinkIDSdk.createBlinkIDSdk(withSettings: sdkSettings)
            let eventStream = BlinkIDEventStream()
            let analyzer = try await BlinkIDAnalyzer(
                sdk: sdk,
                eventStream: eventStream
            )
            let model = BlinkIDUXModel(
                analyzer: analyzer,
                uxSettings: uxSettings,
                sessionNumber: analyzer.sessionNumber
            )

            let session = BlinkIDUXSession(
                sdk: sdk,
                eventStream: eventStream,
                analyzer: analyzer,
                model: model
            )
            let handle = Unmanaged.passRetained(session).toOpaque()
            liveBlinkIDUXHandles.insert(handle)
            session.startResultMonitor(
                handle: handle,
                resultCallback: onResult,
                userData: userData
            )

            onReady(handle, userData)
        } catch {
            if let onError = onError {
                let msg = "\(error)"
                let utf8 = Array(msg.utf8)
                utf8.withUnsafeBufferPointer { buf in
                    guard let base = buf.baseAddress else { return }
                    // Call synchronously — pointer is only valid within this scope.
                    onError(base, buf.count, userData)
                }
            }
        }
    }
}

@_cdecl("SBW_BlinkIDUX_BlinkIDUXView_GetViewController")
public func SBW_BlinkIDUX_BlinkIDUXView_GetViewController(
    _ handle: UnsafeMutableRawPointer?
) -> UnsafeMutableRawPointer? {
    return onMainThread {
        guard let session = validateBlinkIDUXHandle(handle) else { return nil }
        return Unmanaged.passUnretained(session.hostingController).toOpaque()
    }
}

@_cdecl("SBW_BlinkIDUX_BlinkIDUXView_Free")
public func SBW_BlinkIDUX_BlinkIDUXView_Free(
    _ handle: UnsafeMutableRawPointer?
) {
    onMainThread {
        guard let handle = handle, liveBlinkIDUXHandles.remove(handle) != nil else { return }
        let session = Unmanaged<BlinkIDUXSession>.fromOpaque(handle).takeUnretainedValue()
        session.cancelResultMonitor()
        Unmanaged<BlinkIDUXSession>.fromOpaque(handle).release()
    }
}

// MARK: - BlinkIDUXView Test-only helpers

/// Cancels the analyzer, triggering the result callback with code 2 (cancelled).
@_cdecl("SBW_TEST_BlinkIDUX_BlinkIDUXView_Cancel")
public func SBW_TEST_BlinkIDUX_BlinkIDUXView_Cancel(
    _ handle: UnsafeMutableRawPointer?
) {
    onMainThread {
        guard let session = validateBlinkIDUXHandle(handle) else { return }
        Task {
            await session.analyzer.cancel()
        }
    }
}

/// Returns the number of live BlinkIDUXView session handles.
/// Test-only: used to verify no handles are leaked after null-callback or error-path tests.
@_cdecl("SBW_TEST_BlinkIDUX_BlinkIDUXView_LiveHandleCount")
public func SBW_TEST_BlinkIDUX_BlinkIDUXView_LiveHandleCount() -> Int {
    return onMainThread {
        liveBlinkIDUXHandles.count
    }
}
