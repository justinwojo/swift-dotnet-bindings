// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Swift Concurrency Interop Hook

fileprivate typealias EnqueueOriginal = @convention(thin) (UnownedJob) -> Void
fileprivate typealias EnqueueHook = @convention(thin) (UnownedJob, EnqueueOriginal) -> Void

/// A minimal executor that runs Swift async jobs on GCD.
/// Required for Swift async/await to work when called from .NET.
@available(macOS 10.15, iOS 13.0, tvOS 13.0, watchOS 6.0, *)
final class GCDExecutor: SerialExecutor {
    static let shared = GCDExecutor()
    private let queue = DispatchQueue(label: "swift-bindings-test.executor", qos: .userInitiated)

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

/// Initializes the Swift concurrency runtime for .NET interop.
/// Must be called before any async methods are invoked from C#.
@_cdecl("SwiftBindingsTestLib_InitializeConcurrency")
public func initializeConcurrency() {
    guard !_concurrencyInitialized else { return }
    _concurrencyInitialized = true

    guard let handle = dlopen(nil, 0),
          let hookPtr = dlsym(handle, "swift_task_enqueueGlobal_hook") else {
        return
    }

    let hook = hookPtr.assumingMemoryBound(to: EnqueueHook?.self)
    hook.pointee = { job, _ in
        GCDExecutor.shared.enqueue(job)
    }
}

// MARK: - AsyncWorker

/// Struct with various async methods for testing async emission.
public struct AsyncWorker {
    public let name: String

    public init(name: String) {
        self.name = name
    }

    /// Async void instance method.
    public func asyncVoidMethod() async {
        try? await Task.sleep(nanoseconds: 1_000_000)
    }

    /// Async instance method returning Int32.
    public func asyncReturnMethod() async -> Int32 {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return 42
    }

    /// Async instance method returning String.
    public func asyncStringMethod() async -> String {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return "Hello from \(name)"
    }

    /// Async static void method.
    public static func asyncStaticVoid() async {
        try? await Task.sleep(nanoseconds: 1_000_000)
    }

    /// Async static method returning Int32.
    public static func asyncStaticReturn() async -> Int32 {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return 99
    }

    /// Async method with parameters.
    public func asyncAdd(a: Int32, b: Int32) async -> Int32 {
        try? await Task.sleep(nanoseconds: 1_000_000)
        return a + b
    }
}

// MARK: - Mutating Async Receiver

/// Struct with a `mutating async` method. The wrapper emitter used to bind
/// `__self` as a `let` (immutable copy) inside async wrappers, which made
/// `mutating async` calls fail to compile (`cannot use mutating member on
/// immutable value`). Even after that compile error was masked, dereferencing
/// into a `let` would silently lose mutation across calls — fatal for
/// `AsyncIteratorProtocol.next()` and any other `mutating async` API.
public struct AsyncMutatingCounter {
    public var value: Int32

    public init(start: Int32 = 0) {
        self.value = start
    }

    /// Increments the counter and returns the new value. Mutation must write
    /// through to the original storage so that successive calls advance.
    public mutating func bumpAsync() async -> Int32 {
        try? await Task.sleep(nanoseconds: 1_000_000)
        value += 1
        return value
    }
}

// MARK: - Async Optional Return diagnostics
//
// Repro for an async-callback Optional<Int32> marshalling regression: the
// async-wrapper emit allocates a result buffer in Swift, writes the
// Optional via `initializeMemory`, and hands the pointer to a C# callback
// which reads it via `SwiftOptional<int>`. When `next()` returns `nil`,
// C# was reading `Some(0)` instead of `None` — pinning down whether that
// reproduces on a top-level async function isolates the bug from the
// iterator-method bridge introduced for AsyncSequence.

/// Async function that always returns `nil`. The C# binding must surface
/// this as an `int? = null`, NOT `0`. Used as a smoke test for the
/// async-return Optional<Int32> marshal path.
public func sbwAsyncReturnNoneInt() async -> Int32? {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return nil
}

/// Async function that always returns `Some(7)`. Pairs with
/// `sbwAsyncReturnNoneInt` to confirm the Some-side still works.
public func sbwAsyncReturnSomeSeven() async -> Int32? {
    try? await Task.sleep(nanoseconds: 1_000_000)
    return 7
}
