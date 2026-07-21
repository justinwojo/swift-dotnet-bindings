// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Foundation;
using Swift.Runtime;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Tracks Swift object lifecycle via the SwiftBindingsTestLib allocation counters.
/// </summary>
public static class LifetimeTracker
{
    // P/Invoke declarations for the Swift allocation tracking functions
    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_ResetAllocationCounters")]
    private static extern void ResetAllocationCounters();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_GetAllocationCount")]
    private static extern int GetAllocationCount();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_GetDeallocationCount")]
    private static extern int GetDeallocationCount();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_GetLiveObjectCount")]
    private static extern int GetLiveObjectCount();

    // Returns a strdup'd C string describing the registry-tracked objects still live (see
    // TrackedRef); the caller must free it with FreeTrackedString. Lets a leak probe that
    // never balances NAME the survivors — category, tag, allocation order — instead of
    // reporting a bare count.
    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_DumpLiveTrackedObjects")]
    private static extern IntPtr DumpLiveTrackedObjects();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_FreeString")]
    private static extern void FreeTrackedString(IntPtr ptr);

    // DIAGNOSTIC (not a correctness mechanism). When true, the quiescence drain also drains an
    // autorelease pool and briefly pumps the current thread's run loop each poll — to test the
    // hypothesis that a rare struct-with-ref survivor is held by a release scheduled on a run
    // loop / autorelease pool that GC.WaitForPendingFinalizers does not pump, rather than a real
    // lost-release leak. It is deliberately separate from the exact-count gate: the leak
    // assertion below is unchanged and still fails deterministically regardless of this flag, so
    // turning it off cannot hide a genuine leak. If a soak with this active goes fully green
    // where it otherwise flaked, that is evidence for the run-loop/pool hypothesis; the decision
    // to keep or remove it is deliberate, not silent.
    internal static bool RunLoopAutoreleaseDrainDiagnostic = true;
    private static bool _runLoopDiagLogged;

    // Quiescence-drain budgets. Swift ARC releases run asynchronously — a completion
    // callback on a foreign executor, a background finalizer thread — so the tracker's
    // dealloc counter can lag the C# call that dropped the last reference by a short,
    // nondeterministic interval (observed in the hundreds of ms). These let a genuinely
    // slow release land before an assert, and let a prior test's in-flight release settle
    // before the counters are zeroed, without ever loosening the exact-count gates: a real
    // leak never balances, so it still fails deterministically once the timeout elapses.
    private const int QuiescencePollMs = 25;
    private const int AssertQuiescenceTimeoutMs = 3000;
    private const int ResetQuiescenceTimeoutMs = 2000;

    /// <summary>
    /// Resets the allocation counters. Call before starting a lifetime test.
    /// </summary>
    public static void Reset()
    {
        try
        {
            // Drain any still-in-flight deallocation from the PRIOR test window BEFORE zeroing
            // the counters. A Swift release that lands just AFTER the reset would increment the
            // dealloc counter with no matching allocation in the new window and drive the live
            // count negative — a phantom "-1 leak" in the next probe (deallocations =
            // allocations + 1). A still-pending release keeps the object counted as live (its
            // alloc has landed, its dealloc has not: live = allocations - deallocations > 0), so
            // draining until live reaches 0 — not merely until the counters stop moving — is what
            // proves no release is still in flight across the reset boundary. "Counters stopped
            // moving" is too weak: they can sit stably at (N, N-1, 1) for a poll window while a
            // release is still scheduled, letting the stale dealloc straddle the reset (and, in
            // the next window, cancel a genuinely leaked allocation to a false live==0). Once
            // live hits 0 every prior-window alloc has a matching dealloc already counted, so
            // nothing can land in the gap before ResetAllocationCounters(). Best-effort: if a
            // prior-window leak keeps live above 0 past the timeout the reset proceeds anyway —
            // that leak is the prior test's AssertNoLeaks to report, not this reset's.
            DrainWhileAbove(0, ResetQuiescenceTimeoutMs);
            ResetAllocationCounters();
            // Zero the value-witness release-path counters in the same window as the allocation
            // counters, so a leak probe's failure readout covers only this test's release activity.
            ReleasePathDiagnostics.Reset();
            TestLogger.Memory("Allocation counters reset");
        }
        catch (Exception ex)
        {
            TestLogger.Warning($"Failed to reset allocation counters: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the current allocation statistics.
    /// </summary>
    public static (int allocations, int deallocations, int live) GetStats()
    {
        try
        {
            return (GetAllocationCount(), GetDeallocationCount(), GetLiveObjectCount());
        }
        catch (Exception ex)
        {
            TestLogger.Warning($"Failed to get allocation stats: {ex.Message}");
            return (-1, -1, -1);
        }
    }

    /// <summary>
    /// Logs the current allocation statistics.
    /// </summary>
    public static void LogStats(string context = "")
    {
        var (alloc, dealloc, live) = GetStats();
        var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
        TestLogger.Memory($"{prefix}Allocations={alloc}, Deallocations={dealloc}, Live={live}");
    }

    /// <summary>
    /// Asserts that all tracked objects have been deallocated.
    /// Forces GC first to ensure finalizers run.
    /// </summary>
    public static void AssertNoLeaks(string context = "")
    {
        // Drain finalizers, then re-check with conservative-root eviction and a bounded
        // quiescence wait if anything still looks live. Two orthogonal effects are absorbed
        // here, neither of which is a real leak:
        //   1. Async release latency. A Swift object dropped on the C# side is ARC-released
        //      by a completion callback / background executor a short, nondeterministic time
        //      later; the dealloc counter can lag the abandoning call. Re-draining across
        //      short sleeps (up to the timeout) lets that release land.
        //   2. Conservative-root phantoms. Under Mono's conservative stack scan a stale
        //      pointer-shaped value left in a spilled slot by a just-returned churn helper
        //      can pin one already-dead wrapper; overwriting that stack region with
        //      non-pointer fill and re-draining lets the next collect reclaim it.
        // Draining only ever releases objects (it never allocates), so the live count is
        // monotonically non-increasing here: a genuine leak — a still-reachable reference or
        // an orphaned native retain — never balances, stays non-zero for the whole timeout,
        // and the exact-zero gate below still fails deterministically with the same message.
        var (alloc, dealloc, live) = DrainWhileAbove(0, AssertQuiescenceTimeoutMs);

        if (live != 0)
        {
            var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
            throw new AssertionException(
                $"{prefix}Memory leak detected: {live} object(s) not deallocated " +
                $"(allocations={alloc}, deallocations={dealloc}). {DescribeLiveIdentities()} " +
                $"{ReleasePathDiagnostics.Snapshot()}");
        }

        TestLogger.Memory($"No leaks: {alloc} allocated, {dealloc} deallocated");
    }

    // Force finalizers and give slow async Swift releases time to land, re-checking with
    // conservative-root eviction, until the tracker reports at most <paramref name="target"/>
    // live objects or the bounded timeout elapses. Returns the final snapshot. Because
    // draining only releases (never allocates) the live count cannot rise here, so this can
    // only turn a transient over-count (a not-yet-landed release) into the expected count —
    // it can never mask a genuine leak, which keeps the count above target for the whole
    // timeout. The clean case (already at/below target after the first drain) returns
    // immediately with no sleeps.
    private static (int alloc, int dealloc, int live) DrainWhileAbove(int target, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var snap = DrainAndSnapshot();
        while (snap.live > target && Environment.TickCount64 < deadline)
        {
            // Clear a conservative-root phantom, then re-drain: cheap, and resolves at once
            // if a dead-but-pinned stack straggler was the only thing above target.
            EvictConservativeStackRoots();
            snap = DrainAndSnapshot();
            if (snap.live <= target)
                break;
            // DIAGNOSTIC (see RunLoopAutoreleaseDrainDiagnostic): give a run-loop /
            // autorelease-scheduled release a chance to run. Separate from the exact-count
            // gate — it can only release, never allocate, so it cannot mask a genuine leak.
            if (RunLoopAutoreleaseDrainDiagnostic)
            {
                PumpRunLoopAndDrainAutoreleasePool();
                snap = DrainAndSnapshot();
                if (snap.live <= target)
                    break;
            }
            // Still above target: yield briefly so an in-flight async release can land,
            // then re-drain and re-test against the deadline.
            Thread.Sleep(QuiescencePollMs);
            snap = DrainAndSnapshot();
        }
        return snap;
    }

    // DIAGNOSTIC helper. Drain an autorelease pool and briefly pump the current thread's run
    // loop, so a release scheduled on either — which GC.WaitForPendingFinalizers does not run —
    // gets a chance to fire. Pure observation aid for the struct-with-ref survivor hypothesis;
    // it never allocates a tracked object, so it cannot turn a real leak green. Non-inlined and
    // exception-swallowing so it can't perturb the drain frame or escape as a test failure.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PumpRunLoopAndDrainAutoreleasePool()
    {
        if (!_runLoopDiagLogged)
        {
            TestLogger.Memory("[DIAG] run-loop + autorelease-pool drain active in leak quiescence");
            _runLoopDiagLogged = true;
        }
        try
        {
            using (new NSAutoreleasePool())
            {
                // No input sources on this run loop returns immediately; the point is to let
                // any run-loop-scheduled release run and to drain the pool on dispose.
                NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.003));
            }
        }
        catch
        {
            // A diagnostic must never fail the probe it annotates.
        }
    }

    // Force finalizers to run and snapshot the tracker counters. Two collects bracket
    // the finalizer wait so any object resurrected-then-reabandoned during finalization
    // is also reclaimed before the snapshot is read.
    private static (int alloc, int dealloc, int live) DrainAndSnapshot()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GetStats();
    }

    // Overwrite the stack region a just-returned callee may have left holding a stale
    // pointer-shaped value, so Mono's conservative scan stops treating an already-dead
    // wrapper as rooted and the following collect can reclaim it. Non-inlined so the fill
    // lands below the assert/drain frames (over the popped churn-helper frame). A real leak
    // is a reachable reference, not a dead stack slot, so this cannot clear one.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EvictConservativeStackRoots()
    {
        Span<nint> scratch = stackalloc nint[1024];
        FillWithNonPointers(scratch);
        _evictSink = unchecked((int)SumBuffer(scratch));
    }

    // Write and read-back are split across non-inlinable boundaries and routed through
    // the volatile sink so the fill cannot be constant-folded away or dead-store
    // eliminated: the seed is a runtime value the JIT can't know at compile time, the
    // sum escapes to the sink, and neither loop is visible to the other for fusion — so
    // the stores must actually land in this frame's stack memory (the whole point of the
    // eviction). Values stay small so they can't be mistaken for heap references by the
    // next conservative scan.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FillWithNonPointers(Span<nint> buffer)
    {
        nint seed = (nint)((_evictSink & 0xFFFF) | 1);
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = seed + i;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint SumBuffer(Span<nint> buffer)
    {
        nint acc = 0;
        for (int i = 0; i < buffer.Length; i++)
            acc += buffer[i];
        return acc;
    }

    private static volatile int _evictSink;

    /// <summary>
    /// Asserts that the expected number of objects are currently live.
    /// </summary>
    public static void AssertLiveCount(int expected, string context = "")
    {
        // Raw snapshot first — no GC — so a test whose count is already exact behaves exactly
        // as before (this method used to be a pure snapshot). Only when MORE objects are live
        // than expected — an in-flight async release from earlier in the test that has not yet
        // landed — do we drain toward the target. Draining only releases (never allocates), so
        // it can lower a transient over-count to the expected value but can never lift a real
        // under-count (a genuine over-release fails immediately) or hide a genuine leak (which
        // stays above target past the timeout). Net: this can only convert a flaky over-count
        // into the correct pass, never turn a real failure green — objects the test still holds
        // a reference to survive every collect, so a positive expected count cannot be eroded.
        var (_, _, live) = GetStats();
        if (live > expected)
            (_, _, live) = DrainWhileAbove(expected, AssertQuiescenceTimeoutMs);

        if (live != expected)
        {
            var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
            throw new AssertionException(
                $"{prefix}Expected {expected} live object(s), got {live}. {DescribeLiveIdentities()} " +
                $"{ReleasePathDiagnostics.Snapshot()}");
        }
    }

    // Ask the Swift registry to name the tracked objects still live, so a leak that never
    // balances becomes root-cause evidence (which struct-with-ref family, which allocation
    // order/tag) instead of another tally mark. Best-effort: never throws — a diagnostic must
    // not mask the assertion it annotates.
    private static string DescribeLiveIdentities()
    {
        try
        {
            var ptr = DumpLiveTrackedObjects();
            if (ptr == IntPtr.Zero)
                return "Live identities: <unavailable>";
            try
            {
                return $"Live identities: {Marshal.PtrToStringAnsi(ptr)}";
            }
            finally
            {
                FreeTrackedString(ptr);
            }
        }
        catch (Exception ex)
        {
            return $"Live identities: <dump failed: {ex.Message}>";
        }
    }

    /// <summary>
    /// Tracks allocations during an action.
    /// </summary>
    public static (int allocsBefore, int allocsAfter, int newAllocs) TrackAllocations(Action action)
    {
        var before = GetAllocationCount();
        action();
        var after = GetAllocationCount();
        return (before, after, after - before);
    }

    /// <summary>
    /// Runs an action within a tracked scope that verifies cleanup.
    /// </summary>
    public static void RunWithLeakCheck(Action action, string context = "")
    {
        Reset();

        try
        {
            action();
        }
        finally
        {
            // AssertNoLeaks now drains finalizers and waits out any slow async Swift release
            // itself (bounded), so no fixed pre-delay is needed here — a fixed sleep is exactly
            // the timing-fragile pattern this replaced.
            AssertNoLeaks(context);
        }
    }
}
