// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

import Foundation

// MARK: - Cancellation-race probe (Finding 39)
//
// Exercises the lost-cancel race in the generated Swift task-cancellation registry.
// The registry assigns the launched `Task` to its entry through a locked helper
// (`_sbwAssignTask`) and the cancel path records a `wasCancelled` replay flag under the
// same lock, so a cancel that arrives before the task is observable still cancels the work.
//
// The probe's async work is cancellation-aware: it sleeps in short slices, checking
// `Task.isCancelled` each slice. If the launched task was cancelled — whether the cancel
// landed mid-flight or was replayed from the register→assign window — the loop bails and
// throws `CancellationError`, which the generated error callback maps to an
// `OperationCanceledException` on the C# side. If the work instead runs every slice to the
// end WITHOUT ever observing cancellation, it records a "completed without cancel" tick.
//
// The durable invariant the runtime test asserts: a call whose token fires at/just-after
// launch must never record a completed-without-cancel tick — i.e. the cancel is never lost.

/// Thread-safe outcome tallies for the cancellation-race probe. The counters are process-wide
/// (the registry race is process-wide), guarded by an `NSLock` because the work bodies resolve
/// on Swift's cooperative executor across threads while the C# test reads the tallies.
public final class CancellationRaceProbe {
    private static let lock = NSLock()
    private static var _completedWithoutCancel: Int = 0
    private static var _observedCancel: Int = 0
    private static var _started: Int = 0

    public init() {}

    /// Long-running, cancellation-aware work. Sleeps in `slices` short steps, checking
    /// `Task.isCancelled` before each step and once more after the loop. Throws
    /// `CancellationError` the moment cancellation is observed (recording an observed-cancel
    /// tick); otherwise records a completed-without-cancel tick and returns the slice count.
    ///
    /// `Task.sleep` itself throws on cancellation; `try?` swallows that so the very next
    /// `Task.isCancelled` check performs the explicit, attributable bail.
    public func raceableWork(slices: Int) async throws -> Int32 {
        CancellationRaceProbe.recordStarted()
        for _ in 0..<slices {
            if Task.isCancelled {
                CancellationRaceProbe.recordObservedCancel()
                throw CancellationError()
            }
            try? await Task.sleep(nanoseconds: 5_000_000) // 5ms
        }
        if Task.isCancelled {
            CancellationRaceProbe.recordObservedCancel()
            throw CancellationError()
        }
        CancellationRaceProbe.recordCompletedWithoutCancel()
        return Int32(slices)
    }

    private static func recordStarted() {
        lock.lock(); _started += 1; lock.unlock()
    }

    private static func recordObservedCancel() {
        lock.lock(); _observedCancel += 1; lock.unlock()
    }

    private static func recordCompletedWithoutCancel() {
        lock.lock(); _completedWithoutCancel += 1; lock.unlock()
    }

    /// Number of work bodies that ran every slice to the end WITHOUT observing cancellation.
    /// A correctly-replayed cancel keeps this at zero for any call whose token fired at launch.
    public static func completedWithoutCancelCount() -> Int32 {
        lock.lock(); defer { lock.unlock() }; return Int32(_completedWithoutCancel)
    }

    /// Number of work bodies that observed cancellation and bailed early.
    public static func observedCancelCount() -> Int32 {
        lock.lock(); defer { lock.unlock() }; return Int32(_observedCancel)
    }

    /// Total work bodies that have resolved (bailed or completed) — used by the test to wait
    /// for in-flight Swift tasks to settle before reading the tallies.
    public static func resolvedCount() -> Int32 {
        lock.lock(); defer { lock.unlock() }; return Int32(_observedCancel + _completedWithoutCancel)
    }

    /// Number of work bodies that have begun executing (incremented at entry, before the first
    /// cancellation check). The test waits until every started body has resolved: a lost cancel
    /// starts immediately but only resolves after running the full slice budget, so gating on
    /// `started == resolved` keeps the settle wait open until that slow tick manifests — a
    /// fixed "wait for N resolved" floor would return before it and pass falsely.
    public static func startedCount() -> Int32 {
        lock.lock(); defer { lock.unlock() }; return Int32(_started)
    }

    public static func reset() {
        lock.lock(); _observedCancel = 0; _completedWithoutCancel = 0; _started = 0; lock.unlock()
    }
}
