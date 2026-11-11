// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// DIAGNOSTIC. Finds pointer-shaped words on the CURRENT thread's live stack that fall inside a
/// given managed object's memory, which is exactly what a conservative stack scan treats as a
/// root. Used on a leak probe's failure path to say whether a survivor is pinned by this thread.
/// Only meaningful on Mono's conservative collector; a precise, moving GC (CoreCLR, NativeAOT)
/// has no conservative roots, and an object's address can change during the scan.
/// </summary>
internal static unsafe class ConservativeRootProbe
{
    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "pthread_self")]
    private static extern IntPtr PthreadSelf();

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "pthread_get_stackaddr_np")]
    private static extern IntPtr PthreadGetStackAddr(IntPtr thread);

    /// <summary>The managed object's address. The caller must not hold the object across a GC.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static nint AddressOf(object obj) => *(nint*)Unsafe.AsPointer(ref obj);

    /// <summary>
    /// Counts words in [scanFrom, stack top) of the current thread that point into
    /// [lo, lo + span). Returns a short description with the hit offsets below the stack top.
    /// </summary>
    internal static string FindHits(nint scanFrom, nint lo, int span)
    {
        try
        {
            nint top = PthreadGetStackAddr(PthreadSelf());
            nint start = scanFrom & ~(nint)7;
            if (start <= 0 || start >= top || top - start > 64 * 1024 * 1024)
                return $"scan unavailable (start=0x{start:X}, top=0x{top:X})";
            var hits = new List<string>();
            for (nint p = start; p < top; p += sizeof(nint))
            {
                nint v = *(nint*)p;
                if (v >= lo && v < lo + span)
                    hits.Add($"top-0x{(top - p):X}(+{v - lo})");
            }
            return hits.Count == 0
                ? $"no hits in {top - start} bytes"
                : $"{hits.Count} hit(s) in {top - start} bytes: {string.Join(",", hits.Take(8))}";
        }
        catch (Exception ex)
        {
            return $"scan failed: {ex.Message}";
        }
    }
}
