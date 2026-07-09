// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

    /// <summary>
    /// Resets the allocation counters. Call before starting a lifetime test.
    /// </summary>
    public static void Reset()
    {
        try
        {
            ResetAllocationCounters();
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
        // Drain finalizers, then re-check with conservative-root eviction if anything
        // still looks live. Under Mono's conservative stack scan a stale pointer-shaped
        // value left in a spilled stack slot by a just-returned churn helper can pin a
        // single already-dead wrapper, so a large create-and-abandon loop intermittently
        // reports one phantom straggler even after finalizers drain. Overwriting that
        // stack region with non-pointer fill and re-draining lets the next collect reclaim
        // a genuinely-dead straggler; a real leak is a still-reachable reference, so it
        // survives every eviction pass and still fails. Exact-zero stays the final gate.
        var (alloc, dealloc, live) = DrainAndSnapshot();
        for (int attempt = 0; live != 0 && attempt < 8; attempt++)
        {
            EvictConservativeStackRoots();
            (alloc, dealloc, live) = DrainAndSnapshot();
        }

        if (live != 0)
        {
            var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
            throw new AssertionException(
                $"{prefix}Memory leak detected: {live} object(s) not deallocated " +
                $"(allocations={alloc}, deallocations={dealloc})");
        }

        TestLogger.Memory($"No leaks: {alloc} allocated, {dealloc} deallocated");
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
    // wrapper as rooted and the following collect can reclaim it. Non-inlined so the
    // fill lands below AssertNoLeaks's frame (over the popped churn-helper frame). A
    // real leak is a reachable reference, not a dead stack slot, so this cannot clear
    // one.
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
        var (_, _, live) = GetStats();

        if (live != expected)
        {
            var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
            throw new AssertionException(
                $"{prefix}Expected {expected} live object(s), got {live}");
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
            // Allow finalizers to run
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Small delay to allow Swift cleanup
            Thread.Sleep(100);

            AssertNoLeaks(context);
        }
    }
}
