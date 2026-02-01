// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

import Foundation

// MARK: - Swift Concurrency Interop Hook

fileprivate typealias EnqueueOriginal = @convention(thin) (UnownedJob) -> Void
fileprivate typealias EnqueueHook = @convention(thin) (UnownedJob, EnqueueOriginal) -> Void

/// A minimal executor that runs jobs on GCD
@available(macOS 10.15, iOS 13.0, tvOS 13.0, watchOS 6.0, *)
final class GCDExecutor: SerialExecutor {
    static let shared = GCDExecutor()
    private let queue = DispatchQueue(label: "swift-bindings.executor", qos: .userInitiated)

    @available(macOS 14.0, iOS 17.0, tvOS 17.0, watchOS 10.0, *)
    func enqueue(_ job: consuming ExecutorJob) {
        let unownedJob = UnownedJob(job)
        let executor = asUnownedSerialExecutor()
        queue.async {
            unownedJob.runSynchronously(on: executor)
        }
    }

    // Legacy API for older OS versions
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

private var _concurrencyInitialized = false

@_cdecl("AsyncTests_InitializeConcurrency")
public func initializeConcurrency() {
    guard !_concurrencyInitialized else { return }
    _concurrencyInitialized = true

    // Use dlsym to get the hook variable pointer (like swift-concurrency-extras does)
    guard let handle = dlopen(nil, 0),
          let hookPtr = dlsym(handle, "swift_task_enqueueGlobal_hook") else {
        return
    }

    let hook = hookPtr.assumingMemoryBound(to: EnqueueHook?.self)
    hook.pointee = { job, _ in
        GCDExecutor.shared.enqueue(job)
    }
}

// MARK: - AsyncStruct

public struct AsyncStruct {
    public let storedValue: Int32

    public init(_ storedValue: Int32) {
        self.storedValue = storedValue
    }

    public func AsyncVoid() async {
        try? await Task.sleep(nanoseconds: 1_000_000_000)
    }

    public func AsyncNonVoid(seconds: UInt64) async -> UInt64 {
        try? await Task.sleep(nanoseconds: seconds * 1_000_000_000)
        return seconds
    }

    public static func AsyncVoidStatic() async {
        try? await Task.sleep(nanoseconds: 1_000_000_000)
    }

    public static func AsyncNonVoidStatic(seconds: UInt64) async -> UInt64 {
        try? await Task.sleep(nanoseconds: seconds * 1_000_000_000)
        return seconds
    }

    public func GenericUnconstrained<T>(input: T) async {
        try? await Task.sleep(nanoseconds: 1_000_000_000)
    }

    public static func GenericUnconstrainedStatic<T>(input: T) async {
        try? await Task.sleep(nanoseconds: 1_000_000_000)
    }

    public func GenericCollectionConstraint<C>(input: C) async -> Int
        where C: Collection, C.Element == String
    {
        for identifier in input {
            if identifier == "error" {
                return -1
            }
        }
        try? await Task.sleep(nanoseconds: 1_000_000_000)
        return input.count
    }

    public static func GenericCollectionConstraintStatic<C>(input: C) async -> Int
        where C: Collection, C.Element == String
    {
        for identifier in input {
            if identifier == "error" {
                return -1
            }
        }
        try? await Task.sleep(nanoseconds: 1_000_000_000)
        return input.count
    }

    public func ArrayPassThrough(input: [String]) async -> [String] {
        try? await Task.sleep(nanoseconds: 1_000_000_000)
        return input
    }

    public func StringPassThrough(input: String) async -> String {
        try? await Task.sleep(nanoseconds: 1_000_000_000)
        return input
    }
}

