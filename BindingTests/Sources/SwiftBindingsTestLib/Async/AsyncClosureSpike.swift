// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async Closure Bridge Spike
//
// Hand-written ABI proof for bridging `@escaping () async throws -> Int32`
// closures from C# into Swift. The generator will generate this same
// shape from the emitter; this file de-risks the ABI first.
//
// All symbols here are local to `SwiftBindingsTestLib` (user-library module)
// and are prefixed `_acspike_` so they do not collide with the generated
// `SwiftBindings` wrapper module's `_SBW_*` emissions.

// Task registry helpers local to this spike. The generated
// `_SBWTaskEntry`/`_sbwRegisterTask`/`_sbwUnregisterTask` live in the
// `SwiftBindings` wrapper module as `private` members and are not
// callable from this module — so we declare our own.
private final class _ACSpike_TaskEntry {
    var task: Task<Void, Never>?
}
private var _acspikeActiveTasks: [Int64: _ACSpike_TaskEntry] = [:]
private let _acspikeTaskLock = NSLock()

private func _acspikeRegisterTask(_ taskId: Int64, _ entry: _ACSpike_TaskEntry) {
    _acspikeTaskLock.lock()
    _acspikeActiveTasks[taskId] = entry
    _acspikeTaskLock.unlock()
}

private func _acspikeUnregisterTask(_ taskId: Int64) {
    _acspikeTaskLock.lock()
    _acspikeActiveTasks.removeValue(forKey: taskId)
    _acspikeTaskLock.unlock()
}

/// Target "user" Swift function — what the generator would normally emit a
/// wrapper around. Deliberately trivial: just invoke the closure.
public func spikeCallAsyncOpTarget(_ op: @Sendable @escaping () async throws -> Int32) async throws -> Int32 {
    return try await op()
}

/// Error type surfaced when the C# user lambda throws.
public struct SpikeBridgeError: LocalizedError, CustomStringConvertible {
    public let description: String
    public var errorDescription: String? { description }
    public init(_ description: String) { self.description = description }
}

/// Continuation box retained across the C# `Start` call.
private final class _ACSpike_AsyncBox_Int32 {
    let cont: CheckedContinuation<Int32, Error>
    init(_ cont: CheckedContinuation<Int32, Error>) { self.cont = cont }
}

/// Sendable wrapper for the closure's (context, start-func) pair. The raw
/// pointer is unmanaged memory owned by C# for the lifetime of the call;
/// safe to ferry across concurrency domains. `UnsafeMutableRawPointer` is
/// non-Sendable in Swift 6.
private struct _ACSpike_ClosureHandoff: @unchecked Sendable {
    let contextPtr: UnsafeMutableRawPointer
    let startFunc: @convention(c) (UnsafeMutableRawPointer,
                                   UnsafeMutableRawPointer,
                                   UnsafeMutableRawPointer,
                                   UnsafeMutableRawPointer) -> Void
}

/// Success resume callback — invoked by C# `AsyncClosureHelper.RunAsync`
/// when the user lambda returns a value.
@_cdecl("_acspike_asyncBox_Int32_success")
internal func _acspike_asyncBox_Int32_success(
    _ boxPtr: UnsafeMutableRawPointer,
    _ resultPtr: UnsafeMutableRawPointer
) {
    let box = Unmanaged<_ACSpike_AsyncBox_Int32>.fromOpaque(boxPtr).takeRetainedValue()
    let value = resultPtr.load(as: Int32.self)
    box.cont.resume(returning: value)
}

/// Error resume callback — invoked by C# `AsyncClosureHelper.RunAsync`
/// when the user lambda throws.
@_cdecl("_acspike_asyncBox_Int32_error")
internal func _acspike_asyncBox_Int32_error(
    _ boxPtr: UnsafeMutableRawPointer,
    _ msgPtr: UnsafePointer<CChar>
) {
    let box = Unmanaged<_ACSpike_AsyncBox_Int32>.fromOpaque(boxPtr).takeRetainedValue()
    box.cont.resume(throwing: SpikeBridgeError(String(cString: msgPtr)))
}

/// Outer `@_cdecl` wrapper — matches the generator's async-throws harness
/// shape at WrapperEmitter.Async.cs:2086-2104 exactly. ABI:
///   - callback: `(Int32, Int64)` — (result, taskHandle).
///   - errorCallback: `(UnsafePointer<CChar>, Int32, Int64)` — (msg, isCancelled, taskHandle).
///   - `_sbwTask: Int64` — GCHandle-derived task handle.
///   - Then closure pair: `opContextPtr: UnsafeMutableRawPointer`,
///     `opStartFunc: @convention(c) (raw, raw, raw, raw) -> Void`.
@_cdecl("spike_callAsyncOp")
public func PInvoke_spike_callAsyncOp(
    _ callback: @convention(c) (Int32, Int64) -> Void,
    _ errorCallback: @convention(c) (UnsafePointer<CChar>, Int32, Int64) -> Void,
    _ _sbwTask: Int64,
    _ opContextPtr: UnsafeMutableRawPointer,
    _ opStartFunc: @convention(c) (UnsafeMutableRawPointer,
                                   UnsafeMutableRawPointer,
                                   UnsafeMutableRawPointer,
                                   UnsafeMutableRawPointer) -> Void
) {
    let _handoff = _ACSpike_ClosureHandoff(contextPtr: opContextPtr, startFunc: opStartFunc)
    let _entry = _ACSpike_TaskEntry()
    _acspikeRegisterTask(_sbwTask, _entry)
    _entry.task = Task {
        defer {
            _acspikeUnregisterTask(_sbwTask)
        }
        do {
            // Adapter closure: bridges a Swift `async throws -> Int32` call back
            // into the C# start thunk + continuation box machinery.
            let adapted: @Sendable () async throws -> Int32 = {
                return try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Int32, Error>) in
                    let box = _ACSpike_AsyncBox_Int32(cont)
                    let boxPtr = Unmanaged.passRetained(box).toOpaque()
                    // Resume callbacks passed to C# as opaque pointers to fit the
                    // generic start-func signature; Swift-side they are typed
                    // `@_cdecl` symbols declared above.
                    let successFP = unsafeBitCast(
                        _acspike_asyncBox_Int32_success as
                            @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer) -> Void,
                        to: UnsafeMutableRawPointer.self)
                    let errorFP = unsafeBitCast(
                        _acspike_asyncBox_Int32_error as
                            @convention(c) (UnsafeMutableRawPointer, UnsafePointer<CChar>) -> Void,
                        to: UnsafeMutableRawPointer.self)
                    _handoff.startFunc(_handoff.contextPtr, boxPtr, successFP, errorFP)
                }
            }

            let _result = try await spikeCallAsyncOpTarget(adapted)
            callback(_result, _sbwTask)
        } catch {
            let _isCancelled: Int32 = (error is CancellationError) ? 1 : 0
            let errorMessage = String(describing: error)
            errorMessage.withCString { errorCallback($0, _isCancelled, _sbwTask) }
        }
    }
}
