// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Async Properties
// Tests: Computed properties with async getters, including `get async throws`.
// Expected C#: C# properties cannot be async, so an async getter is projected as a
// Task-returning method (PropertyHandler routes it to EmitAsyncPropertyAsMethods).
//
// Async-ness of an accessor is INFERRED, not read: the ABI JSON carries no async flag on
// accessor nodes and an async accessor's mangled name has no `Ya` marker. Two oracles answer
// it and either one suffices — the TBD's sibling `{getter}Tu` / `{getter}TjTu` symbol, and the
// .swiftinterface's literal `get async` harvested as an interface fact. These fixtures cover
// the shapes where one oracle can go silent while the other holds: a nested type, an extension,
// and `get async throws` (whose mis-detection is the dangerous one — a sync projection puts a
// `ref SwiftError` CallConvSwift P/Invoke on an async entry point, which compiles and then
// mismatches the ABI on the first read).

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

// MARK: - Async throwing getters, nested types, and extension-declared accessors
// The three shapes the async-accessor oracles disagree about most easily.

/// Error raised by the `get async throws` fixtures below.
public enum AsyncPropertyError: Error {
    case unavailable
}

/// Top-level class carrying a `get async throws` accessor plus a nested type whose OWN property
/// is `get async throws` — the nested-type shape needs the full type-qualified key
/// ("AsyncImageAnalyzer.Region.pixels"), not just the property's simple name.
/// Deliberately NOT `final`: a non-final class accessor is dispatched through a thunk, so its TBD
/// async marker is the `TjTu` form rather than the bare `Tu` one, which is the variant the throwing
/// accessors below would otherwise leave uncovered.
public class AsyncImageAnalyzer {
    /// When true the throwing accessors raise instead of returning a value, so the same
    /// fixture drives both the success and the fault path of one `get async throws` accessor.
    public let shouldFail: Bool

    public init(shouldFail: Bool) {
        self.shouldFail = shouldFail
    }

    /// `get async throws` on a top-level class, dispatched through a thunk (see the class's own
    /// note): the TBD oracle has to find `TjTu` here, not the bare `Tu` the struct cases export.
    public var analyzedLabel: String {
        get async throws {
            try await Task.sleep(nanoseconds: 1_000_000)
            if shouldFail { throw AsyncPropertyError.unavailable }
            return "analyzed"
        }
    }

    /// Nested struct whose property is `get async throws` — the shape that mis-binds when the
    /// TBD oracle goes silent, because the throwing-getter wrapper gate then rejects it and the
    /// member falls through to a synchronous direct P/Invoke aimed at an async entry point.
    public struct Region {
        public let failing: Bool

        public init(failing: Bool) {
            self.failing = failing
        }

        public var pixels: Int32 {
            get async throws {
                try await Task.sleep(nanoseconds: 1_000_000)
                if failing { throw AsyncPropertyError.unavailable }
                return 42
            }
        }
    }

    /// Builds the nested Region so C# has a construction path that doesn't depend on nested-type
    /// initializer emission.
    public func makeRegion(failing: Bool) -> Region {
        return Region(failing: failing)
    }
}

/// `get async` declared in an EXTENSION rather than in the type body. The interface-fact key for
/// an extension member is built from the extended type's name, so this pins that the extension
/// scope renders the same key shape the type body does.
extension AsyncConfig {
    public var asyncExtensionLabel: String {
        get async {
            try? await Task.sleep(nanoseconds: delay)
            return "Extension: \(name)"
        }
    }
}

// MARK: - Synchronous throwing getter (NOT async)
// The `@_cdecl` property wrapper declines a throwing getter (it emits no try/catch), so this
// property is emitted through the ordinary direct CallConvSwift P/Invoke instead. That path DOES
// carry the error: swiftcc returns a thrown error in the dedicated error register, which the
// generated P/Invoke reads through its `ref SwiftError` out-parameter. This fixture is the
// positive control for that fall-through — both the returning and the throwing outcome.

/// Struct with a synchronous `get throws` property. Deliberately NOT async: it is the control
/// that separates "the wrapper declined" (fine — the direct path is ABI-correct) from
/// "we mistook an async getter for a sync one" (not fine — different entry point).
public struct ThrowingGetterBox {
    public let value: Int32
    public let shouldFail: Bool

    public init(value: Int32, shouldFail: Bool) {
        self.value = value
        self.shouldFail = shouldFail
    }

    public var checkedValue: Int32 {
        get throws {
            if shouldFail { throw AsyncPropertyError.unavailable }
            return value
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

// MARK: - AsyncThrowingStream support (Defect I redesign)
// AsyncThrowingStream<Element, Failure> now binds to IAsyncEnumerable<Element>. Beyond the element +
// completion callbacks every stream carries, the throwing variant adds a SEPARATE Swift producer-error
// callback: the generated wrapper iterates with `for try await` inside a do/catch, and on a
// `finish(throwing:)` termination its `catch` arm marshals the Swift error description across to the C#
// producer-error trampoline, which faults the channel so the consumer's `await foreach` RETHROWS at the
// boundary instead of silently truncating (the pre-redesign behaviour). A `CancellationError` is
// swallowed (consumer task-cancel is not a producer fault). This fixture pins the support against the
// REAL parser output (the ABI/swiftinterface must name the type `_Concurrency.AsyncThrowingStream`) and
// proves it is property-scoped: the sibling non-throwing `safeEvents` rides the same supported-stream
// emission path and must still round-trip.
public final class ThrowingStreamSource {
    public init() {}

    /// Throwing stream: yields 1, 2, 3 then faults via `finish(throwing:)`. A consumer's `await foreach`
    /// observes the three elements then RETHROWS the faulted error (description `boom`, marshalled by the
    /// producer-error callback into a SwiftRuntimeException) at the boundary.
    public var throwingEvents: AsyncThrowingStream<Int32, Error> {
        AsyncThrowingStream { continuation in
            continuation.yield(1)
            continuation.yield(2)
            continuation.yield(3)
            continuation.finish(throwing: StreamProducerError.boom)
        }
    }

    /// Sibling non-throwing stream — must still emit and round-trip, proving the throwing-stream support
    /// rides the same emission path and does not poison the plain AsyncStream sibling on the same type.
    public var safeEvents: AsyncStream<Int32> {
        AsyncStream { continuation in
            continuation.yield(7)
            continuation.yield(8)
            continuation.yield(9)
            continuation.finish()
        }
    }
}

/// Error a throwing AsyncStream raises via `finish(throwing:)` so the producer-threw fault path is
/// exercised end to end. `"\(error)"` renders the case name `boom`, which the Swift wrapper's `catch`
/// arm passes to the C# producer-error callback; the bridge surfaces it as the SwiftRuntimeException
/// message a consumer observes when `await foreach` rethrows.
public enum StreamProducerError: Error {
    case boom
}

// MARK: - Producer cancel (Defect I redesign)
// A consumer-initiated stop (Cancel/Dispose/enumerator disposal) routes through SwiftAsyncStream's
// producer-cancel registry hook, which task-cancels the SUSPENDED Swift producer Task (the wrapper's
// `for await`) rather than merely completing the channel. This closes the "stream dropped without
// completing or disposing leaks one handle+instance" residual. The fixture exercises it: `slowCounts`
// produces on a detached Task that sleeps between yields, so the wrapper's `for await` is genuinely
// suspended between elements — the exact shape producer-cancel exists to stop. `continuation.onTermination`
// forwards the wrapper Task's cancellation (delivered when SBW_CancelTask cancels it) to the producer
// Task. `producedCount` lets the consumer assert the producer's element count STOPS CLIMBING after a
// Cancel() — proving the suspended producer was actually stopped, not just that the channel closed.
public final class CancellableStreamSource {
    public init() {}

    private let _produced = ProducedCounter()

    /// Number of elements the Swift producer has yielded so far. After a consumer Cancel(), this must
    /// stop climbing (the producer Task was cancelled), not keep incrementing forever.
    public var producedCount: Int32 { _produced.value }

    /// Continuously-climbing AsyncStream: the producer Task yields an incrementing counter every 20ms,
    /// so `producedCount` rises without bound until the consumer stops it. A consumer break/Cancel must
    /// halt production — `producedCount` then freezes — rather than leaving the producer running forever.
    /// `continuation.onTermination` forwards the wrapper Task's cancellation to the producer Task.
    public var climbingCounts: AsyncStream<Int32> {
        let counter = _produced
        return AsyncStream { continuation in
            let task = Task {
                var i: Int32 = 0
                while !Task.isCancelled {
                    continuation.yield(i)
                    counter.value = i + 1
                    i += 1
                    try? await Task.sleep(nanoseconds: 20_000_000) // 20ms
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// Yields 3 elements then SUSPENDS INDEFINITELY — the "slow / never-yielding upstream" shape. The
    /// wrapper's `for await` parks with no next element boundary, so completing the channel cannot wake
    /// it; ONLY a registry task-cancel (the C# Cancel() → SBW_CancelTask → wrapper Task cancel →
    /// `onTermination`) drives the producer to `finish()` and the wrapper to its completion callback,
    /// which frees the rooting GCHandle. This isolates producer-cancel: pre-redesign such a parked stream
    /// leaked one context handle (and instance) forever.
    public var suspendingCounts: AsyncStream<Int32> {
        let counter = _produced
        return AsyncStream { continuation in
            let task = Task {
                var i: Int32 = 0
                while i < 3 {
                    continuation.yield(i)
                    counter.value = i + 1
                    i += 1
                }
                // Park until the wrapper Task is cancelled — no further elements ever arrive.
                while !Task.isCancelled {
                    try? await Task.sleep(nanoseconds: 50_000_000) // 50ms
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}

/// Counter shared between the Swift producer Task and the C# consumer's reads. `@unchecked Sendable`:
/// the single producer writes while running and the consumer reads after the producer has been cancelled
/// and settled, so the benign data race never observes a torn Int32.
final class ProducedCounter: @unchecked Sendable {
    var value: Int32 = 0
}
