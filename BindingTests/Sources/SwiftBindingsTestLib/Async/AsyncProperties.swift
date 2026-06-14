// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async Properties
// Tests: Computed properties with async getter
// Expected C#: Async property accessor or async method wrapper
// Limitation: Async properties are not yet supported by the generator

/// Struct with async computed properties.
public struct AsyncConfig {
    public let name: String
    public let delay: UInt64

    public init(name: String, delay: UInt64 = 1_000_000) {
        self.name = name
        self.delay = delay
    }

    /// Async computed property returning a String.
    public var asyncLabel: String {
        get async {
            try? await Task.sleep(nanoseconds: delay)
            return "Config: \(name)"
        }
    }

    /// Async computed property returning Int32.
    public var asyncNameLength: Int32 {
        get async {
            try? await Task.sleep(nanoseconds: delay)
            return Int32(name.count)
        }
    }
}

// MARK: - Class with Async Properties

/// Class with async computed properties.
public class AsyncDataSource {
    public let identifier: String

    public init(identifier: String) {
        self.identifier = identifier
    }

    /// Async computed property on a class.
    public var asyncItemCount: Int32 {
        get async {
            try? await Task.sleep(nanoseconds: 1_000_000)
            return Int32(identifier.count * 2)
        }
    }

    /// Async computed property returning String.
    public var asyncSummary: String {
        get async {
            try? await Task.sleep(nanoseconds: 1_000_000)
            return "DataSource[\(identifier)]"
        }
    }
}

// MARK: - Free Functions

/// Creates an AsyncConfig instance.
public func createAsyncConfig(name: String) -> AsyncConfig {
    return AsyncConfig(name: name)
}

/// Creates an AsyncDataSource instance.
public func createAsyncDataSource(identifier: String) -> AsyncDataSource {
    return AsyncDataSource(identifier: identifier)
}

// MARK: - X1: AsyncStream Property (AsyncStream with object and primitive element types)
// Generator has AsyncStreamEmitter.cs, runtime has SwiftAsyncStream.cs — both untested.

/// Class with AsyncStream computed properties.
/// Tests AsyncStream with both String (ISwiftObject) and Int32 (primitive) element types.
public class AsyncValueSource {
    public init() {}

    public var messages: AsyncStream<String> {
        AsyncStream { continuation in
            continuation.yield("first")
            continuation.yield("second")
            continuation.yield("third")
            continuation.finish()
        }
    }

    /// AsyncStream with primitive Int32 elements.
    /// Tests that SwiftAsyncStream<T> constraint was relaxed from ISwiftObject.
    public var counts: AsyncStream<Int32> {
        AsyncStream { continuation in
            continuation.yield(10)
            continuation.yield(20)
            continuation.yield(30)
            continuation.finish()
        }
    }

    /// AsyncStream whose element type is a Swift array. Regression coverage for
    /// the SwiftArray-at-API-boundary projection bug: pre-fix the property
    /// surfaced as `IAsyncEnumerable<Swift.SwiftArray<Int32>>`, leaking the runtime
    /// helper type at the public API boundary. Post-fix the property surfaces as
    /// `IAsyncEnumerable<IReadOnlyList<Int32>>` while the channel still stores
    /// `SwiftArray<Int32>` internally — covariance (`IAsyncEnumerable<out T>` plus
    /// `SwiftArray<T> : IReadOnlyList<T>`) closes the loop.
    public var batches: AsyncStream<[Int32]> {
        AsyncStream { continuation in
            continuation.yield([1, 2, 3])
            continuation.yield([4, 5])
            continuation.yield([6])
            continuation.finish()
        }
    }
}

// MARK: - Tracked-element AsyncStreams (ownership regression coverage)
// SwiftAsyncStream.OnElement receives a BORROWED `withUnsafePointer(to: element)` (valid only for the
// callback) while the Swift `for await` loop still owns its own +1 on `element` until the iteration
// ends. The element escapes via the C# channel, so OnElement must copy out an INDEPENDENT reference
// during the callback. These fixtures drive the three ownership shapes the borrowed-slot escape breaks:
//   - class element (TrackedRef): the payload word IS the object pointer → must deref + Arc.Retain;
//     a bare marshal stores the soon-dead slot address as the handle (wrong value + use-after-free).
//   - non-frozen struct (TrackedRefStruct, ADOPT/SafeHandle): the SafeHandle must wrap an independent
//     copy, not the borrowed slot the closure frees on return (use-after-free / double-free).
//   - large heap String (move-on-construction): a borrowed +0 must not be bitwise-moved as if it were
//     a transferred +1, or the shared storage is double-released. Small inline strings (≤15 UTF-8
//     bytes) have no heap storage / no ARC and hide the bug, so these strings force heap storage.
public final class TrackedRefStreamSource {
    public init() {}

    /// AsyncStream of class elements. Each TrackedRef is allocated as it is yielded; after a full
    /// drain plus disposal of the extracted C# wrappers the tracked live-count must return to zero.
    public var trackedRefs: AsyncStream<TrackedRef> {
        AsyncStream { continuation in
            continuation.yield(TrackedRef(tag: 1))
            continuation.yield(TrackedRef(tag: 2))
            continuation.yield(TrackedRef(tag: 3))
            continuation.finish()
        }
    }

    /// AsyncStream of non-frozen structs — the ADOPT/SafeHandle shape.
    public var trackedStructs: AsyncStream<TrackedRefStruct> {
        AsyncStream { continuation in
            continuation.yield(TrackedRefStruct(value: 1))
            continuation.yield(TrackedRefStruct(value: 2))
            continuation.yield(TrackedRefStruct(value: 3))
            continuation.finish()
        }
    }

    /// AsyncStream of large (heap-backed) Strings — the move-on-construction shape.
    public var longMessages: AsyncStream<String> {
        AsyncStream { continuation in
            continuation.yield(String(repeating: "alpha-", count: 8) + "tail0")
            continuation.yield(String(repeating: "bravo-", count: 8) + "tail1")
            continuation.yield(String(repeating: "charlie-", count: 8) + "tail2")
            continuation.finish()
        }
    }
}

// MARK: - AsyncThrowingStream rejection (Defect I)
// AsyncThrowingStream's terminal iteration error has no representation across the channel bridge
// (the bridge models a non-throwing AsyncStream as IAsyncEnumerable<T>), so the generator must
// reject it with SkipReason.UnsupportedThrowingAsyncStream rather than half-binding. Pre-fix the
// throwing variant matched IsAsyncStream and flowed into the supported-stream emission path, which
// emitted a Swift wrapper iterating it with a bare `for await` (no `try`) — a Swift compile error
// that the harness silently strips, leaving a C# property bound to a missing symbol. This fixture
// pins the rejection against the REAL parser output (the ABI/swiftinterface must name the type
// `_Concurrency.AsyncThrowingStream` for IsThrowingStream to fire) and proves the rejection is
// property-scoped: the sibling non-throwing `safeEvents` on the same type must still be emitted.
public final class ThrowingStreamSource {
    public init() {}

    /// MUST be skipped: AsyncThrowingStream is unsupported (UnsupportedThrowingAsyncStream).
    public var throwingEvents: AsyncThrowingStream<Int32, Error> {
        AsyncThrowingStream { continuation in
            continuation.yield(1)
            continuation.yield(2)
            continuation.finish()
        }
    }

    /// Sibling non-throwing stream — must still be emitted and round-trip at runtime, proving the
    /// throwing-stream rejection does not poison the whole type.
    public var safeEvents: AsyncStream<Int32> {
        AsyncStream { continuation in
            continuation.yield(7)
            continuation.yield(8)
            continuation.yield(9)
            continuation.finish()
        }
    }
}
