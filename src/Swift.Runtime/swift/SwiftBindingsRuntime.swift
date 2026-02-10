// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Swift Concurrency Interop Hook
//
// Swift's cooperative concurrency model uses a dedicated thread pool that .NET
// threads don't participate in. When C# calls a Swift async method via P/Invoke,
// the Swift task is enqueued on the cooperative pool but never executes because
// no .NET thread runs Swift's executor loop.
//
// This library hooks swift_task_enqueueGlobal_hook to redirect all globally-
// enqueued Swift tasks to GCD, where they will actually run.
//
// Known limitations:
//   - @MainActor tasks are NOT intercepted (swift_task_enqueueMainExecutor_hook
//     is buggy in Swift 5.5-6.0 and often not invoked by the runtime)
//   - Task cancellation does not propagate through GCD dispatch
//   - Custom actor executors are not intercepted — only plain Task {} and
//     Task.detached {} go through the global hook

fileprivate typealias EnqueueOriginal = @convention(thin) (UnownedJob) -> Void
fileprivate typealias EnqueueHook = @convention(thin) (UnownedJob, EnqueueOriginal) -> Void

/// A minimal executor that runs Swift jobs on GCD.
@available(macOS 10.15, iOS 13.0, tvOS 13.0, watchOS 6.0, *)
final class GCDExecutor: SerialExecutor {
    static let shared = GCDExecutor()
    private let queue = DispatchQueue(label: "swift-bindings.executor", qos: .userInitiated)

    func enqueue(_ job: UnownedJob) {
        let executor = asUnownedSerialExecutor()
        queue.async {
            job.runSynchronously(on: executor)
        }
    }

    func asUnownedSerialExecutor() -> UnownedSerialExecutor {
        UnownedSerialExecutor(ordinary: self)
    }
}

// MARK: - Initialization

private var _isInitialized = false

/// Initialize Swift concurrency for interop with C#/.NET.
///
/// Hooks `swift_task_enqueueGlobal_hook` to redirect Swift tasks to GCD
/// instead of Swift's cooperative thread pool. Call once before any async
/// Swift calls from C#.
@_cdecl("SwiftBindings_InitializeConcurrency")
public func initializeConcurrency() {
    guard !_isInitialized else { return }

    guard let handle = dlopen(nil, 0),
          let hookPtr = dlsym(handle, "swift_task_enqueueGlobal_hook") else {
        return
    }

    let hook = hookPtr.assumingMemoryBound(to: EnqueueHook?.self)
    hook.pointee = { job, _ in
        GCDExecutor.shared.enqueue(job)
    }

    _isInitialized = true
}

/// Check if concurrency has been initialized.
@_cdecl("SwiftBindings_IsConcurrencyInitialized")
public func isConcurrencyInitialized() -> Bool {
    return _isInitialized
}

// MARK: - Existential Type Metadata

/// Swift runtime's MetadataResponse: (metadata pointer, completion state).
/// We model this as a tuple so @_silgen_name captures both return registers
/// correctly on ARM64, avoiding UB from truncating a 2-word return.
private typealias MetadataResponse = (metadataPtr: UnsafeMutableRawPointer, state: Int)

/// Import swift_getExistentialTypeMetadata from the Swift runtime.
/// Calling this from Swift avoids the Mono JIT assertion that occurs
/// when C# calls it via CallConvSwift P/Invoke.
@_silgen_name("swift_getExistentialTypeMetadata")
private func _swift_getExistentialTypeMetadata(
    _ request: Int,
    _ superclass: UnsafeRawPointer?,
    _ numProtocols: Int,
    _ protocols: UnsafeRawPointer?
) -> MetadataResponse

/// Returns existential type metadata for a zero-protocol existential (Any).
///
/// Wraps `swift_getExistentialTypeMetadata` so the call happens entirely on
/// the Swift side, avoiding the Mono JIT `CallConvSwift` assertion crash.
///
/// - Parameter numProtocols: Number of protocol constraints. Only 0 is supported.
/// - Returns: Metadata pointer, or nil if numProtocols is unsupported.
@_cdecl("SwiftBindings_GetExistentialTypeMetadata")
public func getExistentialTypeMetadata(_ numProtocols: Int) -> UnsafeMutableRawPointer? {
    guard numProtocols == 0 else { return nil }
    // request=0 is MetadataRequest.Complete, superclass=nil, protocols=nil
    let response = _swift_getExistentialTypeMetadata(0, nil, 0, nil)
    return response.metadataPtr
}
