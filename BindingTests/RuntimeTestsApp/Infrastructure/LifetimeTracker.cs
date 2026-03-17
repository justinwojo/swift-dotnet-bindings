// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
        // Force GC to run finalizers
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var (alloc, dealloc, live) = GetStats();

        if (live != 0)
        {
            var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
            throw new AssertionException(
                $"{prefix}Memory leak detected: {live} object(s) not deallocated " +
                $"(allocations={alloc}, deallocations={dealloc})");
        }

        TestLogger.Memory($"No leaks: {alloc} allocated, {dealloc} deallocated");
    }

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
