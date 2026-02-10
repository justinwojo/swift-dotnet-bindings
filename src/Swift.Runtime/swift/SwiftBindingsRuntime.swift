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

// MARK: - SwiftString Wrapper Functions
//
// SwiftString.cs uses CallConvSwift P/Invokes for ToString() and Length,
// which trigger the Mono JIT assertion at jit-info.c:918. These @_cdecl
// wrappers perform string operations entirely on the Swift side, returning
// results via C-compatible types callable with CallingConvention.Cdecl.
//
// The buffer pointer points to the 16-byte raw representation of a
// Swift.String (2 words on arm64), which is the same layout as
// SwiftString.Buffer on the C# side.

/// Converts a Swift String buffer to UTF-8 bytes.
///
/// Reads a `String` from the raw 2-word buffer at `bufferPtr`, extracts its
/// UTF-8 representation, and returns an allocated byte buffer that the caller
/// must free with `SBW_SwiftString_FreeUtf8`.
///
/// - Parameters:
///   - bufferPtr: Pointer to the 16-byte Swift.String raw representation.
///   - outPtr: On return, pointer to the allocated UTF-8 byte buffer (nil if empty).
///   - outLen: On return, the number of UTF-8 bytes.
@_cdecl("SBW_SwiftString_ToUtf8")
public func sbw_swiftStringToUtf8(
    _ bufferPtr: UnsafeRawPointer,
    _ outPtr: UnsafeMutablePointer<UnsafeMutablePointer<UInt8>?>,
    _ outLen: UnsafeMutablePointer<Int>
) {
    // assumingMemoryBound + .pointee creates a retain-balanced copy:
    // increments refcount on read, decrements when `str` goes out of scope.
    // The original buffer at bufferPtr is unaffected.
    let str = bufferPtr.assumingMemoryBound(to: String.self).pointee

    let utf8Array = Array(str.utf8)
    if utf8Array.isEmpty {
        outPtr.pointee = nil
        outLen.pointee = 0
        return
    }

    let count = utf8Array.count
    let ptr = UnsafeMutablePointer<UInt8>.allocate(capacity: count)
    utf8Array.withUnsafeBufferPointer { buf in
        ptr.initialize(from: buf.baseAddress!, count: count)
    }
    outPtr.pointee = ptr
    outLen.pointee = count
}

/// Gets the character count of a Swift String from its raw buffer.
///
/// Returns `String.count` (Unicode scalar/grapheme cluster count), which
/// may differ from the UTF-8 byte count for multi-byte characters.
///
/// - Parameter bufferPtr: Pointer to the 16-byte Swift.String raw representation.
/// - Returns: The character count.
@_cdecl("SBW_SwiftString_GetCount")
public func sbw_swiftStringGetCount(_ bufferPtr: UnsafeRawPointer) -> Int {
    let str = bufferPtr.assumingMemoryBound(to: String.self).pointee
    return str.count
}

/// Creates a Swift String from UTF-8 bytes and writes its raw representation
/// to the provided output buffer.
///
/// The output buffer must be at least 16 bytes (2 words on arm64), matching
/// the size of `SwiftString.Buffer` on the C# side. The function initializes
/// the buffer with a new String value using proper ARC retain semantics.
///
/// - Parameters:
///   - utf8Ptr: Pointer to the UTF-8 encoded bytes.
///   - utf8Len: Number of UTF-8 bytes.
///   - outBufferPtr: Pointer to the 16-byte output buffer for the String representation.
@_cdecl("SBW_SwiftString_Create")
public func sbw_swiftStringCreate(
    _ utf8Ptr: UnsafePointer<UInt8>,
    _ utf8Len: Int,
    _ outBufferPtr: UnsafeMutableRawPointer
) {
    let data = UnsafeBufferPointer(start: utf8Ptr, count: utf8Len)
    let str = String(decoding: data, as: UTF8.self)
    // initialize(to:) properly retains the String value in the output buffer.
    outBufferPtr.assumingMemoryBound(to: String.self).initialize(to: str)
}

/// Destroys a Swift String stored in the provided buffer.
///
/// Properly deinitializes the String value at the buffer pointer, releasing
/// any ARC-managed storage. Call this instead of ValueWitnessTable->Destroy
/// to avoid the Mono JIT CallConvSwift assertion.
///
/// - Parameter bufferPtr: Pointer to the 16-byte Swift.String raw representation.
@_cdecl("SBW_SwiftString_Destroy")
public func sbw_swiftStringDestroy(_ bufferPtr: UnsafeMutableRawPointer) {
    bufferPtr.assumingMemoryBound(to: String.self).deinitialize(count: 1)
}

/// Frees a UTF-8 buffer previously allocated by `SBW_SwiftString_ToUtf8`.
///
/// - Parameter ptr: The pointer to free, or nil (no-op).
@_cdecl("SBW_SwiftString_FreeUtf8")
public func sbw_swiftStringFreeUtf8(_ ptr: UnsafeMutablePointer<UInt8>?) {
    ptr?.deallocate()
}
