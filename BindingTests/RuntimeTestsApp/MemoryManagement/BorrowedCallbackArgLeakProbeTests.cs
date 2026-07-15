// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Finding 11 — the borrow leak. A Copy-semantics runtime wrapper (<c>SwiftResult</c>/<c>SwiftArray</c>)
/// passed BY VALUE into a C# callback is read through the borrowed callback-arg marshal
/// (<c>MarshalCallbackArg&lt;…&gt;</c>). The wrapper's from-handle ctor runs <c>NativeMemory.Alloc</c> +
/// <c>InitializeWithCopy</c>, owning the native buffer plus a +1 on the embedded payload. The old
/// borrowed path blanket-suppressed the wrapper's SafeHandle finalizer, foreclosing the VWT Destroy and
/// leaking that copy per invocation; the declared <c>PayloadConstructionSemantics.Copy</c> contract now
/// keeps the finalizer so the buffer + embedded ref are released.
///
/// This is the opposite direction from <see cref="WireCarrierLeakProbeTests"/> (which probes the
/// Copy-wrapper *return* path). Here Swift invokes the callback <c>count</c> times, each time marshalling
/// a fresh borrowed wrapper into C#; the lambda reads-and-discards it WITHOUT disposing, so cleanup is
/// the finalizer's job alone. Each payload embeds a <see cref="LifetimeTracker"/>-counted
/// <c>TrackedRef</c>, so a suppressed Destroy is a non-zero live count after a GC drain — a deterministic
/// leak (the pinned ref is never released regardless of GC timing), not a flaky "does not crash".
///
/// The single Swift call runs in a <c>[MethodImpl(NoInlining)]</c> helper so no stale stack slot keeps
/// the last borrowed wrapper rooted past the drain.
/// </summary>
public class BorrowedCallbackArgLeakProbeTests : TestBase
{
    public BorrowedCallbackArgLeakProbeTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    /// <summary>
    /// <c>Result&lt;TrackedRef, TrackedRefError&gt;</c> (the SwiftResult Copy wrapper) passed by value into
    /// the callback: the borrowed wrapper's finalizer must run the VWT Destroy and release the embedded
    /// ref. The old suppress-on-borrow path pinned one ref per invocation.
    /// </summary>
    public void TestBorrowedResultCallbackArgReleasesEmbeddedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        InvokeBorrowedResults(1000);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("borrowed SwiftResult Copy-wrapper callback arg must not leak the InitializeWithCopy buffer + embedded ref");
        TestLogger.Info("borrowed SwiftResult callback arg: 1000 invocations released their embedded ref");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InvokeBorrowedResults(int count)
    {
        // Read-and-discard the borrowed Copy-wrapper without disposing — cleanup is the wrapper
        // finalizer's responsibility (the declared Copy semantics keep it; the old borrowed path
        // suppressed it and leaked the copy).
        TestLibFunctions.InvokeWithBorrowedTrackedResult(count, r => { _ = r; });
    }

    /// <summary>
    /// <c>[TrackedRef]</c> (the SwiftArray Copy wrapper) passed by value into the callback: the borrowed
    /// wrapper's finalizer must release its +1 on the CoW storage that backs the element. The old
    /// suppress-on-borrow path pinned the element per invocation.
    /// </summary>
    public void TestBorrowedArrayCallbackArgReleasesElement()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        InvokeBorrowedArrays(1000);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("borrowed SwiftArray Copy-wrapper callback arg must not leak the InitializeWithCopy CoW-storage retain");
        TestLogger.Info("borrowed SwiftArray callback arg: 1000 invocations released their element ref");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InvokeBorrowedArrays(int count)
    {
        TestLibFunctions.InvokeWithBorrowedTrackedArray(count, arr => { _ = arr; });
    }

    /// <summary>
    /// The MOVE-wrapper direction of the same borrow: a heap-form Swift <c>String</c> passed BY
    /// VALUE into the callback. The SwiftString from-handle ctor bitwise-copies the borrowed
    /// two-word value into a 16-byte container the wrapper allocates itself — so per invocation
    /// the marshal seam must (a) never value-witness-destroy the borrowed String that Swift's
    /// loop still owns, and (b) still free the wrapper's OWN container (the old blanket
    /// finalizer suppression leaked it per call). (a) is observed as content fidelity across
    /// many invocations WITH finalizer drains interleaved — an over-release corrupts or crashes
    /// once the drains run the payload cleanup while Swift keeps reusing the same String — and
    /// (b) as a native-footprint bound: the measured batch after a same-sized warmup must not
    /// grow the process footprint by anything near the container-leak magnitude.
    /// </summary>
    [Slow]
    public void TestBorrowedStringCallbackArgFreesContainerWithoutDestroyingString()
    {
        const int WarmupCount = 200_000;
        const int MeasuredCount = 400_000;
        // Red-world leak: ≥16 bytes of wrapper container per invocation → ≥6.4 MB over the
        // measured batch. Green-world steady-state growth after the warmup is ~0.
        const long GrowthBoundBytes = 4 * 1024 * 1024;

        string expected = string.Concat(Enumerable.Repeat("borrowed-string-move-arm/", 4));

        // Warmup: reach malloc/GC steady state (heap segments sized, arenas populated).
        long warmupMismatches = InvokeBorrowedStrings(WarmupCount, expected);
        DrainFinalizers();
        AssertEqual(0L, warmupMismatches, "warmup: borrowed String content must round-trip on every invocation");

        long baseline = NativeFootprint.TryGetPhysFootprintBytes();
        long measuredMismatches = InvokeBorrowedStrings(MeasuredCount, expected);
        DrainFinalizers();
        long after = NativeFootprint.TryGetPhysFootprintBytes();

        // Content fidelity across drains = the borrowed String was never over-released.
        AssertEqual(0L, measuredMismatches, "borrowed String content must survive payload-finalizer drains (no over-release of Swift-owned storage)");

        if (baseline > 0 && after > 0)
        {
            long growth = after - baseline;
            TestLogger.Info($"borrowed String callback arg: footprint growth {growth / 1024} KiB over {MeasuredCount} invocations");
            AssertTrue(growth < GrowthBoundBytes,
                $"per-invocation container must be freed: footprint grew {growth / 1024} KiB over {MeasuredCount} borrowed-String callbacks (bound {GrowthBoundBytes / 1024} KiB)");
        }
        else
        {
            TestLogger.Info("borrowed String callback arg: phys_footprint unavailable; leak bound skipped (content/no-crash assertions still ran)");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long InvokeBorrowedStrings(int count, string expected)
    {
        long mismatches = 0;
        // Read-and-discard without disposing — the wrapper's payload cleanup (finalizer) must
        // free its own 16-byte container while never destroying the borrowed String.
        TestLibFunctions.InvokeWithBorrowedString(count, s =>
        {
            if (s != expected)
                mismatches++;
        });
        return mismatches;
    }

    /// <summary>
    /// Reads the process physical footprint via <c>proc_pid_rusage</c> (RUSAGE_INFO_V0) — the
    /// same accounting Xcode's memory gauge uses; counts dirty/native pages, which is where the
    /// leaked NativeMemory containers land. Returns a non-positive value when unavailable.
    /// </summary>
    private static class NativeFootprint
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RUsageInfoV0
        {
            public ulong Uuid0;
            public ulong Uuid1;
            public ulong UserTime;
            public ulong SystemTime;
            public ulong PkgIdleWkups;
            public ulong InterruptWkups;
            public ulong Pageins;
            public ulong WiredSize;
            public ulong ResidentSize;
            public ulong PhysFootprint;
            public ulong ProcStartAbstime;
            public ulong ProcExitAbstime;
        }

        [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "proc_pid_rusage")]
        private static unsafe extern int ProcPidRusage(int pid, int flavor, RUsageInfoV0* buffer);

        [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "getpid")]
        private static extern int GetPid();

        internal static unsafe long TryGetPhysFootprintBytes()
        {
            try
            {
                var info = default(RUsageInfoV0);
                const int RusageInfoV0 = 0;
                if (ProcPidRusage(GetPid(), RusageInfoV0, &info) != 0)
                    return -1;
                return (long)info.PhysFootprint;
            }
            catch (Exception)
            {
                // DllImport resolution failure — fall back to the content/no-crash assertions.
                return -1;
            }
        }
    }
}
