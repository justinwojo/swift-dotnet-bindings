// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Reverse dispatch onto a collected consumer-owned implementation
//
// A C# implementation assigned into a NON-RETAINING Swift slot (`weak`/`unowned`) is carried
// by a conformer box the Swift side never retained, so the box follows the consumer's own
// implementation object. That makes one state reachable from ordinary application code that
// the strong-slot lane can never reach: Swift still holds the conformer box through some
// OTHER reference — a private array, a captured closure, an operation already in flight —
// after the consumer has dropped the implementation, and calls back into a carrier whose
// implementation is gone.
//
// These fixtures build exactly that state, deterministically:
//
//   F1 — the framework stashes a private strong reference to the delegate it was handed, the
//        consumer drops theirs, and Swift then calls every shape of requirement through the
//        private reference. Nothing about the timing is racy: the strong reference makes the
//        state permanent, so the callbacks land on a collected implementation every time.
//
//   F2 — a callback already running on a background queue observes the drop mid-flight. The
//        ordering is pinned by semaphores rather than by sleeping: Swift signals that the
//        callback has started, waits for the consumer to say it has dropped and collected,
//        and only then makes the call.
//
// Sentinels are chosen so a degraded result cannot be confused with "there was no delegate":
// every entry point reports `-1` when its private reference is empty, so `-1` in a test means
// the fixture itself is wrong, not that the binding degraded.

// MARK: F1 — every requirement shape on one collected carrier

/// Reverse-dispatch protocol covering the requirement shapes whose degraded behaviour differs:
/// a `Void` requirement (nothing to hand back), an Optional-returning requirement (`nil` is a
/// legal answer), a `Bool`-returning requirement (`false` is the identity), and — on the sibling
/// protocols below — the two throwing shapes, which differ from each other: an `async throws`
/// requirement reaches C# through a continuation that HAS an error channel, while a plain
/// synchronous `throws` requirement does not (see `ReverseCollectedSyncThrowingDelegate`).
public protocol ReverseCollectedDelegate: AnyObject {
    /// Void requirement. A live implementation records the call; a degraded one must simply
    /// not happen, with no trace on either side.
    func didUpdate(_ value: Int32)

    /// Optional-returning requirement. A live implementation returns `value + 3000`.
    func optionalValue(_ value: Int32) -> Int32?

    /// `Bool`-returning requirement. A live implementation returns `true` for a positive value.
    func shouldProceed(_ value: Int32) -> Bool
}

/// F1: the framework shape where a delegate is handed over through a `weak` slot and then
/// privately retained as well.
///
/// The `weak` slot is what the consumer assigns, so the carrier crossing the boundary is
/// consumer-owned. `retainDelegateInternally()` then takes the second, private strong
/// reference the consumer cannot see or reach — the same thing a framework does when it
/// appends a delegate to an observer list or captures it in a queued block. Once the consumer
/// drops their implementation, the conformer box stays alive on this private reference while
/// the implementation behind it is collected, and every call below lands on that carrier.
public class CollectedDelegateHost {
    /// Non-retaining storage — the assignment the consumer makes, and the one that puts the
    /// carrier on the consumer-owned lane.
    public weak var weakDelegate: (any ReverseCollectedDelegate)?

    /// The private strong reference. An array rather than a stored property so the "framework
    /// kept a copy somewhere you cannot see" story is literal.
    private var retained: [any ReverseCollectedDelegate] = []

    public init() {}

    /// Takes the private strong reference to whatever is currently in the `weak` slot. Called
    /// after the consumer assigns, exactly as a framework would on `didSet`.
    public func retainDelegateInternally() {
        if let delegate = weakDelegate {
            retained.append(delegate)
        }
    }

    /// True once the private strong reference exists — the precondition every invoke below
    /// depends on.
    public var retainsDelegateInternally: Bool {
        return !retained.isEmpty
    }

    /// True while the `weak` slot still reads non-nil. Under F1 this stays true even after the
    /// consumer drops their implementation, because the private strong reference keeps the
    /// conformer box (and therefore the weak slot) alive.
    public var hasWeakDelegate: Bool {
        return weakDelegate != nil
    }

    /// Void requirement, dispatched from the private strong reference. Returns `0` once the
    /// call has been made and `-1` when nothing is privately retained, so a test can prove the
    /// fixture really dispatched; whether the call REACHED managed code is observed on the
    /// managed side, since a degraded void call leaves no Swift-side trace by construction.
    public func invokeVoidFromRetained(_ value: Int32) -> Int32 {
        guard let delegate = retained.first else { return -1 }
        delegate.didUpdate(value)
        return 0
    }

    /// Optional-returning requirement, dispatched from the private strong reference. Returns
    /// the delegate's value, `-2` when the delegate answered `nil`, `-1` when nothing is
    /// privately retained.
    public func invokeOptionalFromRetained(_ value: Int32) -> Int32 {
        guard let delegate = retained.first else { return -1 }
        return delegate.optionalValue(value) ?? -2
    }

    /// `Bool`-returning requirement, dispatched from the private strong reference. Returns `1`
    /// for `true`, `0` for `false`, `-1` when nothing is privately retained — an `Int32` so
    /// "the delegate said false" is never confused with "there was no delegate".
    public func invokeBoolFromRetained(_ value: Int32) -> Int32 {
        guard let delegate = retained.first else { return -1 }
        return delegate.shouldProceed(value) ? 1 : 0
    }
}

// MARK: F1 — the throwing requirement

/// The throwing half of F1, on its own protocol so its reverse-dispatch witness is independent
/// of the three sync shapes above. A requirement that can throw is the one shape where the
/// boundary has somewhere to put the failure, so a collected implementation surfaces as an
/// ordinary Swift error rather than as a synthesized return value.
public protocol ReverseCollectedThrowingDelegate: AnyObject {
    /// A live implementation returns `value + 4000`.
    func compute(_ value: Int32) async throws -> Int32
}

/// F1 for the throwing requirement: same `weak` slot plus private strong reference, with the
/// call re-thrown so the consumer observes the error on their own call path.
public class CollectedThrowingDelegateHost {
    public weak var weakDelegate: (any ReverseCollectedThrowingDelegate)?

    private var retained: [any ReverseCollectedThrowingDelegate] = []

    public init() {}

    public func retainDelegateInternally() {
        if let delegate = weakDelegate {
            retained.append(delegate)
        }
    }

    public var retainsDelegateInternally: Bool {
        return !retained.isEmpty
    }

    /// Dispatches the throwing requirement from the private strong reference and lets the error
    /// propagate, so the consumer sees it as a failure of their own call. Returns `-1` when
    /// nothing is privately retained.
    public func invokeFromRetained(_ value: Int32) async throws -> Int32 {
        guard let delegate = retained.first else { return -1 }
        return try await delegate.compute(value)
    }
}

// MARK: F1 — the SYNCHRONOUS throwing requirement

/// The synchronous throwing shape, which is NOT the same story as the `async throws` one above.
///
/// An `async throws` requirement reverse-dispatches through a continuation that carries an error
/// function pointer, so a degraded call has somewhere to put a failure and Swift sees a genuine
/// thrown error. A plain `func f() throws -> T` has no such channel: its receiver thunk is a
/// cdecl function returning a value buffer, with no error out-slot and no Swift error register,
/// so the boundary can only hand back a value. A degraded call therefore returns the return
/// type's identity value exactly as a non-throwing requirement does — this fixture exists to
/// hold that behaviour still and make it observable, not to assert an error appears.
public protocol ReverseCollectedSyncThrowingDelegate: AnyObject {
    /// A live implementation returns `value + 6000`; it never throws in this fixture, so any
    /// error Swift observes came from the boundary rather than from the implementation.
    func computeNow(_ value: Int32) throws -> Int32
}

/// F1 for the synchronous throwing requirement: same `weak` slot plus private strong reference.
public class CollectedSyncThrowingDelegateHost {
    public weak var weakDelegate: (any ReverseCollectedSyncThrowingDelegate)?

    private var retained: [any ReverseCollectedSyncThrowingDelegate] = []

    public init() {}

    public func retainDelegateInternally() {
        if let delegate = weakDelegate {
            retained.append(delegate)
        }
    }

    public var retainsDelegateInternally: Bool {
        return !retained.isEmpty
    }

    /// Dispatches the synchronous throwing requirement from the private strong reference and
    /// reports what the boundary produced: the delegate's value, `-1` when nothing is privately
    /// retained, or `-3` when Swift observed a thrown error. The `-3` arm is what makes this test
    /// able to notice if the synchronous shape ever gains an error channel — today it stays
    /// unused and the degraded call comes back as the return type's identity value.
    public func invokeFromRetained(_ value: Int32) -> Int32 {
        guard let delegate = retained.first else { return -1 }
        do {
            return try delegate.computeNow(value)
        } catch {
            return -3
        }
    }
}

// MARK: F2 — a callback in flight while the consumer drops

/// Reverse-dispatch protocol for the in-flight race. A live implementation returns
/// `value + 5000`.
public protocol ReverseRaceDelegate: AnyObject {
    func step(_ value: Int32) -> Int32
}

/// F2: a callback already dispatched onto a background queue observes the consumer dropping
/// the implementation underneath it.
///
/// The hand-off is a pair of semaphores rather than a sleep, so the interleaving is exact:
/// the background block signals that it is inside the callback and blocks; the consumer drops
/// their implementation and collects; the consumer releases the block, which then makes the
/// call. Whatever the call does — dispatch normally because a collection did not happen, or
/// degrade because it did — it must not take the process down, and the result must come back.
public class RaceDelegateHost {
    /// Non-retaining storage, so the carrier is consumer-owned.
    public weak var weakDelegate: (any ReverseRaceDelegate)?

    private var retained: [any ReverseRaceDelegate] = []

    private let started = DispatchSemaphore(value: 0)
    private let proceed = DispatchSemaphore(value: 0)
    private let finished = DispatchSemaphore(value: 0)
    private var result: Int32 = -1

    public init() {}

    public func retainDelegateInternally() {
        if let delegate = weakDelegate {
            retained.append(delegate)
        }
    }

    public var retainsDelegateInternally: Bool {
        return !retained.isEmpty
    }

    /// Starts the callback on a background queue. The block captures the privately-retained
    /// delegate, announces that it is running, and waits to be released before calling.
    public func beginCallbackOnBackgroundQueue(_ value: Int32) {
        let delegate = retained.first
        DispatchQueue.global().async { [self] in
            started.signal()
            proceed.wait()
            result = delegate?.step(value) ?? -1
            finished.signal()
        }
    }

    /// Blocks until the background callback is running and parked at the hand-off.
    public func waitUntilCallbackStarted() {
        started.wait()
    }

    /// Releases the parked callback so it makes the call.
    public func allowCallbackToProceed() {
        proceed.signal()
    }

    /// Blocks until the callback has returned, then reports what it got.
    public func waitForCallbackResult() -> Int32 {
        finished.wait()
        return result
    }
}
