// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Proves the GC finalizer actually runs VWT Destroy for the struct-projected-as-class
/// categories, releasing the embedded reference field.
///
/// This closes a real blind spot: <see cref="LifetimeTracker"/> only tracked Swift
/// <c>class</c> instances (<c>TrackedObject</c>). The struct-with-ref categories —
/// ClassWithOpaquePayload (non-frozen struct -> SafeHandle) and ClassWithBufferStruct
/// (frozen struct with ref field -> Buffer) — were validated only by single-shot
/// <c>DeinitTracker.pointee</c> checks or "does not crash" stress loops. Nothing
/// asserted a balanced live-count over GC churn for the exact categories the ownership
/// docs promise "the GC finalizer runs VWT Destroy automatically" for.
///
/// The fixtures embed <c>TrackedRef</c>, which feeds the same alloc/dealloc counters
/// <see cref="LifetimeTracker"/> reads. So a leaked buffer (VWT Destroy never firing)
/// shows up as a non-zero live count, not just a crash.
/// </summary>
public class StructVwtDestroyLeakTests : TestBase
{
    public StructVwtDestroyLeakTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        // Loop: a round-trip leaves a two-level wrapper chain (make-wrapper +
        // passThrough-result) sharing the same embedded refs, so the last instance
        // can need more than one collect/finalize cycle to fully settle. Repeated
        // collects also clobber any callee-saved register still pinning the last
        // wrapper under Mono's conservative scan.
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    // Create-and-abandon in a non-inlined helper so the wrappers do not stay
    // GC-rooted on the calling test method's frame. NO Dispose — the whole point
    // is to drive cleanup through the GC finalizer path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAndAbandonNonFrozen(int n)
    {
        for (int i = 0; i < n; i++)
            _ = TestLibFunctions.MakeTrackedRefStruct(i);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAndAbandonFrozen(int n)
    {
        for (int i = 0; i < n; i++)
            _ = TestLibFunctions.MakeFrozenTrackedRefStruct(i);
    }

    // make() returns the struct by value (Direct strategy → Site 1 path), while
    // passThrough() returns it through an indirect-result heap buffer (IndirectResult
    // strategy → Site 2 path). Round-tripping abandons both wrappers so the GC drives
    // cleanup, and the run exercises BOTH return strategies for the 5-ref struct.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAndAbandonLargeFrozen(int n)
    {
        for (int i = 0; i < n; i++)
            _ = TestLibFunctions.MakeLargeFrozenTrackedRefStruct(i);
    }

    /// <summary>
    /// Non-frozen struct with a ref field (ClassWithOpaquePayload / SafeHandle):
    /// abandoning instances and forcing GC must finalize each SafeHandle, run VWT
    /// Destroy, and ARC-release the embedded TrackedRef — live count returns to 0.
    /// </summary>
    public void TestNonFrozenStructWithRefFinalizerReleasesEmbeddedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        CreateAndAbandonNonFrozen(500);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("non-frozen struct-with-ref finalizer must run VWT Destroy");
        TestLogger.Info("Non-frozen struct-with-ref: 500 abandoned instances all finalized + VWT-destroyed");
    }

    /// <summary>
    /// Frozen struct with a ref field (ClassWithBufferStruct): same guarantee
    /// through the Buffer projection — GC finalization runs VWT Destroy and
    /// ARC-releases the embedded TrackedRef.
    /// </summary>
    public void TestFrozenStructWithRefFinalizerReleasesEmbeddedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        CreateAndAbandonFrozen(500);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("frozen struct-with-ref finalizer must run VWT Destroy");
        TestLogger.Info("Frozen struct-with-ref: 500 abandoned instances all finalized + VWT-destroyed");
    }

    /// <summary>
    /// Large frozen struct with FIVE ref fields (5 × 8 = 40 bytes). Abandoning makes
    /// and forcing GC must finalize each wrapper's SafeHandle, run VWT Destroy, and
    /// ARC-release all five embedded TrackedRefs — live count returns to 0. Extends
    /// the single-ref finalizer guarantee to a multi-ref frozen buffer.
    /// </summary>
    public void TestLargeFrozenStructFinalizerReleasesEmbeddedRefs()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        CreateAndAbandonLargeFrozen(500);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("large frozen struct finalizer must VWT-destroy all 5 embedded refs");
        TestLogger.Info("Large frozen struct: 500 abandoned instances x 5 refs all finalized + VWT-destroyed");
    }

    /// <summary>
    /// The IndirectResult return path: <c>passThrough()</c> returns the 5-ref struct
    /// through a caller-allocated heap buffer (it exceeds the arm64 4-GPR direct-return
    /// budget when threaded through a struct-parameter thunk). The callee initializes
    /// the result INTO that temp buffer; <c>NewFromPayload</c> then COPIES out of it
    /// (InitializeWithCopy → +5 on the embedded refs). The success-path cleanup MUST
    /// VWT-destroy the temp buffer's retains before freeing it — without that, each
    /// call orphans 5 refs that no Dispose can ever reach.
    ///
    /// Asserted via explicit Dispose because the Site 2 destroy is a synchronous
    /// marshal-time cleanup (it runs inside the <c>passThrough</c> call, not at
    /// finalization). Disposing both the source and the round-tripped wrapper must
    /// drive the live count to 0; a leaked temp buffer would leave it pinned at +5
    /// per call regardless of Dispose.
    /// </summary>
    public void TestLargeFrozenStructIndirectResultDisposeReleasesEmbeddedRefs()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        for (int i = 0; i < 200; i++)
        {
            var made = TestLibFunctions.MakeLargeFrozenTrackedRefStruct(i);
            var roundTripped = TestLibFunctions.PassThroughLargeFrozenTrackedRefStruct(made);
            made.Dispose();
            roundTripped.Dispose();
        }

        LifetimeTracker.AssertNoLeaks("IndirectResult passThrough must VWT-destroy the temp buffer so Dispose releases all 5 embedded refs");
        TestLogger.Info("Large frozen struct IndirectResult Dispose: 200 make+passThrough x 5 refs released");
    }

    /// <summary>
    /// Explicit Dispose path for both categories, asserted via the deinit counter
    /// (existing DisposeTests only assert "does not crash"). After disposing every
    /// instance the embedded ref must be released — live count returns to 0.
    /// </summary>
    public void TestStructWithRefExplicitDisposeReleasesEmbeddedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        for (int i = 0; i < 200; i++)
        {
            var nf = TestLibFunctions.MakeTrackedRefStruct(i);
            var fr = TestLibFunctions.MakeFrozenTrackedRefStruct(i);
            nf.Dispose();
            fr.Dispose();
        }

        LifetimeTracker.AssertNoLeaks("explicit Dispose must release the embedded ref for both struct categories");
        TestLogger.Info("Struct-with-ref explicit Dispose: 200 of each category released their embedded ref");
    }

    /// <summary>
    /// Churn both categories under periodic GC pressure and assert the live count
    /// stays bounded throughout and returns to 0 at the end — the struct-category
    /// analogue of the class-instance Bundle 01 stress loop.
    /// </summary>
    [Slow]
    public void TestStructWithRefChurnNoLeak()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int iterations = 5_000;
        for (int i = 0; i < iterations; i++)
        {
            _ = TestLibFunctions.MakeTrackedRefStruct(i);
            _ = TestLibFunctions.MakeFrozenTrackedRefStruct(i);

            if (i % 500 == 0)
                GC.Collect();
        }

        LifetimeTracker.AssertNoLeaks($"{iterations} create-and-abandon iterations of each struct-with-ref category must leave no live refs");
        TestLogger.Info($"Struct-with-ref churn: {iterations} iterations x 2 categories left no leaked refs");
    }
}
