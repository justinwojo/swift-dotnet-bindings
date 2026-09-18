// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Reads the live malloc block count across every malloc zone. A retain left on a Swift value or a
/// temporary buffer that is never freed shows up here as one block per leaked call, which neither
/// the lifetime tracker (it counts wrapper objects) nor a Swift deinit counter can see.
/// </summary>
internal static unsafe class MallocProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MallocStatistics
    {
        public uint BlocksInUse;
        public nuint SizeInUse;
        public nuint MaxSizeInUse;
        public nuint SizeAllocated;
    }

    // Aggregated over every malloc zone when the zone argument is NULL.
    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "malloc_zone_statistics")]
    private static extern void MallocZoneStatistics(IntPtr zone, MallocStatistics* stats);

    /// <summary>
    /// Collects garbage and drains finalizers so released wrappers have freed their native state,
    /// then returns the number of malloc blocks in use.
    /// </summary>
    public static long LiveBlocksAfterCollection()
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
        MallocStatistics stats;
        MallocZoneStatistics(IntPtr.Zero, &stats);
        return stats.BlocksInUse;
    }
}
