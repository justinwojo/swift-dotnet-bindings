// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Regression fixture for the parent-only async VOID CSM gap — the void-return
// sibling of PatParentAsyncMethods.swift (which covers value-RETURNING async on
// a generic struct parent). Before this work, the parent-only async CSM path
// hard-rejected void-returning async methods at the pairing validator
// (`IsEmittableParentOnlyAsyncPairing`: `if (returnSpec.IsEmptyTuple) return
// false;`), so each async void method fell through to the catch-all
// GenericTypeCallback skip and never emitted any wrapper.
//
// After the fix, async void methods (throwing and non-throwing, parameterless
// and Swift.String-parameterized) on a generic struct parent each emit a
// per-conformer NON-generic `Task` extension whose @_cdecl completion callback
// carries ONLY the GCHandle context — no result pointer is allocated, passed,
// or freed on either side.
//
// `DonationItem` mirrors `Cubby` (PatParentOnlyMethods.swift): a marker PAT
// whose associated type the methods never reference, registered in
// specialization-hints.json with StringDonationItem + IntDonationItem so the
// engine's parent-baseline resolver finds non-empty conformer sets and each
// closed instantiation gets its own extension class.
//
// The async void methods observe their effect through a shared `DonationSink`
// reference (a Swift class). The generic struct parent holds the sink by
// reference, so the value-type `let __self = self_.pointee` copy in the @_cdecl
// wrapper still shares the SAME sink instance the C# caller reads back — the
// runtime witness that the void method body actually ran across the async hop.

public protocol DonationItem {
    associatedtype Tag
}

public struct StringDonationItem: DonationItem {
    public typealias Tag = String
    public init() {}
}

public struct IntDonationItem: DonationItem {
    public typealias Tag = Int32
    public init() {}
}

public enum DonationError: Error {
    case rejected
}

/// Reference-typed effect sink. The async void methods record into it; the C#
/// test reads `count`/`last` after awaiting to witness the void body ran across
/// the async boundary (a void method on a value-type parent cannot mutate the
/// parent, so a shared reference is the only observable channel).
public final class DonationSink {
    private var _count: Int = 0
    private var _last: String = ""
    public init() {}
    public func record(_ s: String) {
        _count += 1
        _last = s
    }
    public var count: Int { _count }
    public var last: String { _last }
}

/// Parent-only async VOID CSM target. `Donator<Item: DonationItem>` declares
/// async void instance methods with no method-own generics — the void-return
/// analogue of `CubbyBag`'s sync parent-only methods. `Item` is never referenced
/// in a method body; parent-only specialization closes the receiver type alone.
public struct Donator<Item: DonationItem> {
    public let sink: DonationSink
    public init(_ sink: DonationSink) {
        self.sink = sink
    }

    /// async void, non-throwing, no parameters — the marquee shape (ActivityKit
    /// `Activity<T>.update`/`end`, TipKit `Tips.Event<T>.donate`). Forces the
    /// success-only void completion (`completion(context)`, no resultPtr).
    public func donate() async {
        sink.record("donate")
    }

    /// async void, non-throwing, one Swift.String parameter. Exercises the
    /// Utf8Slice param path together with the void completion: the (ptr, len)
    /// pair marshals across, the completion still carries only context.
    public func donateNamed(_ name: String) async {
        sink.record(name)
    }

    /// async void, throwing, one Swift.String parameter. `name == "fail"` drives
    /// the error-callback path (Task faults with a SwiftException); any other
    /// value drives the success path (sink records, Task completes). Confirms the
    /// void throwing wrapper installs BOTH callbacks (success + error) and routes
    /// to the correct one without ever allocating a result buffer.
    public func donateOrThrow(_ name: String) async throws {
        if name == "fail" {
            throw DonationError.rejected
        }
        sink.record(name)
    }

    /// async void, non-throwing, suspends on a cancellable sleep. Drives the void
    /// CANCELLATION path end-to-end: a pre-canceled C# token cancels the launched
    /// Task at birth via the producer-cancel registry (`SBW_CancelTask` lands
    /// before `_sbwAssignTask`), `Task.sleep` returns immediately under
    /// cooperative cancellation, and the `Task.isCancelled` guard suppresses the
    /// record. The non-throwing void wrapper still calls `completion(context)`
    /// afterwards, so the single success callback fires exactly once and frees the
    /// GCHandle — while the C# side, whose cancel registration already called
    /// `TrySetCanceled`, observes an `OperationCanceledException` (first-writer
    /// wins on the TaskCompletionSource). The sink must stay empty.
    public func donateAfterDelay() async {
        try? await Task.sleep(nanoseconds: 3_000_000_000)
        if Task.isCancelled { return }
        sink.record("delayed")
    }
}

// MARK: - Closed-conformer factories
//
// Mirror PatParentAsyncMethods.swift: the C# test path obtains closed instances
// through typed factories rather than a generic constructor surface. The void
// CSM extensions emit on `Donator<StringDonationItem>` and
// `Donator<IntDonationItem>`.

public func makeDonationSink() -> DonationSink {
    return DonationSink()
}

public func makeStringDonator(_ sink: DonationSink) -> Donator<StringDonationItem> {
    return Donator<StringDonationItem>(sink)
}

public func makeIntDonator(_ sink: DonationSink) -> Donator<IntDonationItem> {
    return Donator<IntDonationItem>(sink)
}
