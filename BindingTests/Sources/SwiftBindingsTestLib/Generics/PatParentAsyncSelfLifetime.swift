// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Receiver-lifetime fixture for the parent-only ASYNC CSM path. `AsyncBag` (the
// shape fixture next door) has no stored properties, so nothing about its
// receiver storage is observable: whatever the wrapper reads out of `self_`, the
// awaited body produces the same answer. The parents here carry a reference
// field, which makes two separate questions answerable at runtime.
//
//   1. Does the Swift wrapper's `let __self = self_.pointee` really take a
//      by-value copy that RETAINS the reference field? If it does, the managed
//      receiver may be disposed the instant the synchronous entry returns and the
//      awaited body still reads live memory — so the managed side needs a lease
//      over the synchronous P/Invoke only, not across the whole awaited task.
//   2. Does the receiver's native storage survive the synchronous entry when the
//      consumer disposes it from another thread at the same moment?

/// Reference payload whose lifetime is visible through the shared allocation
/// counters `LifetimeTracker` reads. Immutable and final, so the value-type
/// parents below can stay `Sendable` across the async boundary.
public final class AsyncSelfRefPayload: Sendable {
    public let tag: Int32
    private let trackedSerial: Int64

    public init(tag: Int32) {
        self.tag = tag
        self.trackedSerial = recordTrackedAllocation(
            category: "AsyncSelfRefPayload", tag: tag)
    }

    deinit {
        recordTrackedDeallocation(serial: trackedSerial)
    }
}

/// Parent-only async CSM target whose receiver carries a class reference. Same
/// constraint shape as `AsyncBag` (PAT-constrained generic value parent, async
/// instance methods with no method-own generics), so the same specialization
/// hints drive it and the same emission arm renders it.
public struct AsyncRefBag<Item: AsyncBagItem>: Sendable where Item.Response: Sendable {
    private let payload: AsyncSelfRefPayload

    public init(tag: Int32) {
        self.payload = AsyncSelfRefPayload(tag: tag)
    }

    /// Non-async read-back of the reference field, so a test can establish the
    /// receiver is intact before and after the async call.
    public func payloadTag() -> Int32 { payload.tag }

    /// Suspends before touching the captured receiver copy. The suspension gives
    /// the managed caller a wide, reliable window in which to dispose its
    /// receiver wrapper while the awaited body has not yet read the reference
    /// field — the exact interval a task-long lease would be needed for and a
    /// synchronous-entry lease would not.
    public func respondAfterSuspension() async -> Item.Response {
        try? await Task.sleep(nanoseconds: 250_000_000)
        // Touching the captured copy's reference field is what makes an
        // unretained capture observable rather than merely theoretical.
        precondition(payload.tag >= 0)
        return Item.makeResponse()
    }

    /// No-suspension sibling: the body runs to completion on the first
    /// scheduling hop, which keeps the receiver-disposal race narrow enough to
    /// exercise the synchronous entry rather than the awaited tail.
    public func respondImmediately() async -> Item.Response {
        precondition(payload.tag >= 0)
        return Item.makeResponse()
    }
}

public func makeAsyncRefBagMockStringItem(tag: Int32) -> AsyncRefBag<MockStringItem> {
    return AsyncRefBag<MockStringItem>(tag: tag)
}

public func makeAsyncRefBagMockIntItem(tag: Int32) -> AsyncRefBag<MockIntItem> {
    return AsyncRefBag<MockIntItem>(tag: tag)
}
