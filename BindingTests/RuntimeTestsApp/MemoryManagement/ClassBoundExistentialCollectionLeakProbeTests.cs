// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Probes ARC balance for Swift functions returning an OWNED collection of class-bound existentials
/// (<c>[any Marker]</c> / <c>[String: any Marker]</c>) — the return direction audit P1-07
/// (collection-element leak) and P1-08 (class-bound stride) actually fixed.
///
/// A class-bound existential element is a 16-byte <c>[classRef][witnessTable]</c> cell
/// (<see cref="Swift.Runtime.ClassExistentialContainer1"/>), so the wire carrier is a
/// <c>SwiftArray&lt;ClassExistentialContainer1&gt;</c> / <c>SwiftDictionary&lt;string,
/// ClassExistentialContainer1&gt;</c>. The generated owned-return projection materializes each cell as
/// <c>new MarkerProxy(e, ownsContainer: true)</c>: the SwiftArray subscript getter (<c>$sSayxSicig</c>)
/// <c>InitializeWithCopy</c>s the cell out of CoW storage at +1, and the SwiftDictionary value enumerator
/// moves it out at +1 (<c>MarshalMovedValueFromSlot</c>) — each lays an INDEPENDENT +1 on the class ref
/// that the <c>ownsContainer: true</c> proxy must adopt and release on Dispose.
///
/// This is the existential-carrier sibling of <see cref="WireCarrierLeakProbeTests"/>'s plain-class
/// <c>[TrackedRef]</c> / <c>[Int32: TrackedRef]</c> probes. <c>MarkerImpl</c> (a class-bound
/// <c>Marker</c> conformer) feeds the shared <see cref="LifetimeTracker"/> counters on alloc/deinit, so
/// the balance is asserted directly: after every materialized proxy AND the source carrier are disposed,
/// the live count must return to 0. A non-owning proxy orphans one element retain per materialization
/// (leak shows up as a non-zero live count); a double-adopt double-frees on Dispose (crash inside the
/// loop). The loop runs in a <c>[MethodImpl(NoInlining)]</c> helper so no stale stack slot keeps a proxy
/// or the carrier alive past its <c>Dispose</c>.
///
/// The carrier (the projection over the SwiftArray / SwiftDictionary) is disposed in a <c>finally</c>:
/// it holds its own +1 on the CoW storage backing every element, distinct from the per-element extraction
/// +1s. Disposing the element proxies alone leaves the last iteration's carrier rooted (a false residual).
/// </summary>
public class ClassBoundExistentialCollectionLeakProbeTests : TestBase
{
    public ClassBoundExistentialCollectionLeakProbeTests(TestResults results) : base(results) { }

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
    /// Owned <c>[any Marker]</c> return: the CoW storage holds every class-bound existential cell.
    /// Materializing each element constructs an <c>ownsContainer: true</c> proxy that adopts the
    /// subscript getter's <c>InitializeWithCopy</c> +1 on the class ref; disposing each proxy plus the
    /// carrier must drive the live count to 0. A non-owning element proxy would pin one conformer per
    /// materialization.
    /// </summary>
    public void TestClassBoundExistentialArrayReturnReleasesElements()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int elementsPerCall = 5;
        AllocAndDisposeExistentialArrays(50, elementsPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("[any Marker] return must adopt+release each element cell's +1 (and the carrier's CoW retain)");
        TestLogger.Info($"[any Marker]: 50 returns x {elementsPerCall} class-bound existential elements all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeExistentialArrays(int iterations, int elementsPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            var list = TestLibFunctions.MakeTrackedMarkerArray(elementsPerCall);
            try
            {
                // Indexing each element materializes a fresh ownsContainer:true proxy that adopts the
                // subscript getter's +1 on the class ref. Disposing it must release exactly that +1.
                foreach (var marker in list)
                    (marker as IDisposable)?.Dispose();
            }
            finally
            {
                // The projection owns the SwiftArray carrier's +1 on the CoW storage holding every
                // element cell; release it deterministically rather than leaving it to GC finalization
                // (otherwise the last iteration's carrier stays rooted and reads as a residual leak).
                (list as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// Owned <c>[String: any Marker]</c> return: the SwiftDictionary carrier holds every class-bound
    /// existential VALUE cell. Enumerating <c>.Values</c> moves each cell out at +1 into an
    /// <c>ownsContainer: true</c> proxy; disposing each proxy plus the carrier must drive the live count
    /// to 0. A non-owning value proxy would pin one conformer per value read.
    /// </summary>
    public void TestClassBoundExistentialMapReturnReleasesValues()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int entriesPerCall = 5;
        AllocAndDisposeExistentialMaps(50, entriesPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("[String: any Marker] return must adopt+release each value cell's +1 (and the carrier's CoW retain)");
        TestLogger.Info($"[String: any Marker]: 50 returns x {entriesPerCall} class-bound existential values all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeExistentialMaps(int iterations, int entriesPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            var dict = TestLibFunctions.MakeTrackedMarkerMap(entriesPerCall);
            try
            {
                foreach (var marker in dict.Values)
                    (marker as IDisposable)?.Dispose();
            }
            finally
            {
                (dict as IDisposable)?.Dispose();
            }
        }
    }
}
