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

    /// <summary>
    /// NESTED owned <c>[[any Marker]]</c> return (audit L229): the existential leaf is buried under an
    /// intermediate <c>[any Marker]</c> layer, so the owned-return projection must recurse the
    /// <c>ownsContainer: true</c> adoption from the outer <c>ArrayProjection</c> through the inner
    /// <c>ArrayProjection</c> down to <c>ExistentialProjection</c>. Materializing the outer array yields
    /// owned inner-array carriers; materializing each inner array yields owned marker proxies. Disposing
    /// every marker proxy, every inner carrier, and the outer carrier must drive the live count to 0.
    /// Before the recursive fix the inner layer fell back to a NON-owning element conversion, orphaning
    /// one class-ref +1 per buried existential (leak shows up as a non-zero live count).
    /// </summary>
    public void TestNestedClassBoundExistentialArrayReturnReleasesElements()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int outerPerCall = 4;
        const int innerPerOuter = 3;
        AllocAndDisposeNestedExistentialArrays(40, outerPerCall, innerPerOuter);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("[[any Marker]] return must recurse owns:true adoption to the buried existential leaf (every inner-array element cell's +1 and every carrier's CoW retain)");
        TestLogger.Info($"[[any Marker]]: 40 returns x {outerPerCall} inner arrays x {innerPerOuter} buried class-bound existentials all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeNestedExistentialArrays(int iterations, int outerPerCall, int innerPerOuter)
    {
        for (int i = 0; i < iterations; i++)
        {
            var outerList = TestLibFunctions.MakeTrackedMarkerArrayOfArrays(outerPerCall, innerPerOuter);
            try
            {
                // Materializing the outer array yields an owned inner-array carrier per row; materializing
                // each inner array yields an owns:true proxy per buried existential. Both layers must be
                // disposed: the marker proxy (adopts the leaf class-ref +1) AND the inner carrier (owns the
                // inner CoW storage's +1), else a residual retain pins the conformer.
                foreach (var row in outerList)
                {
                    try
                    {
                        foreach (var marker in row)
                            (marker as IDisposable)?.Dispose();
                    }
                    finally
                    {
                        (row as IDisposable)?.Dispose();
                    }
                }
            }
            finally
            {
                (outerList as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// NESTED owned <c>[String: [any Marker]]</c> return (audit L229), Dictionary-of-arrays sibling: the
    /// existential leaf is buried under the dictionary's value <c>[any Marker]</c> layer, so the
    /// owned-return projection must recurse the <c>ownsContainer: true</c> adoption from
    /// <c>DictionaryProjection</c> through the value <c>ArrayProjection</c> down to
    /// <c>ExistentialProjection</c>. Enumerating <c>.Values</c> yields owned inner-array carriers; each
    /// inner array yields owned marker proxies. Disposing every proxy, every value carrier, and the
    /// dictionary carrier must drive the live count to 0.
    /// </summary>
    public void TestNestedClassBoundExistentialMapReturnReleasesValues()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int outerPerCall = 4;
        const int innerPerOuter = 3;
        AllocAndDisposeNestedExistentialMaps(40, outerPerCall, innerPerOuter);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("[String: [any Marker]] return must recurse owns:true adoption to the buried existential leaf (every value-array element cell's +1 and every carrier's CoW retain)");
        TestLogger.Info($"[String: [any Marker]]: 40 returns x {outerPerCall} value arrays x {innerPerOuter} buried class-bound existentials all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeNestedExistentialMaps(int iterations, int outerPerCall, int innerPerOuter)
    {
        for (int i = 0; i < iterations; i++)
        {
            var dict = TestLibFunctions.MakeTrackedMarkerMapOfArrays(outerPerCall, innerPerOuter);
            try
            {
                foreach (var row in dict.Values)
                {
                    try
                    {
                        foreach (var marker in row)
                            (marker as IDisposable)?.Dispose();
                    }
                    finally
                    {
                        (row as IDisposable)?.Dispose();
                    }
                }
            }
            finally
            {
                (dict as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// NESTED owned <c>[[String: any Marker]]</c> return (audit L229), Array-of-dictionaries sibling: the
    /// existential leaf is buried under an intermediate <c>[String: any Marker]</c> DICTIONARY layer, so
    /// the owned-return projection must recurse the <c>ownsContainer: true</c> adoption from the outer
    /// <c>ArrayProjection</c> through the element <c>DictionaryProjection</c> down to
    /// <c>ExistentialProjection</c>. The AoA/DoA probes above only ever nest an Array; this probe is the
    /// one that exercises a Dictionary as the buried inner container, so the admission gate's symmetric
    /// recursion (which now also admits <c>Array&lt;Dictionary&lt;K, any P&gt;&gt;</c>) is balanced for a
    /// dict-as-inner shape. Materializing the outer array yields owned inner-dictionary carriers;
    /// enumerating each dictionary's <c>.Values</c> yields owned marker proxies. Disposing every proxy,
    /// every inner-dictionary carrier, and the outer carrier must drive the live count to 0.
    /// </summary>
    public void TestNestedClassBoundExistentialArrayOfMapsReturnReleasesValues()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int outerPerCall = 4;
        const int innerPerOuter = 3;
        AllocAndDisposeNestedExistentialArrayOfMaps(40, outerPerCall, innerPerOuter);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("[[String: any Marker]] return must recurse owns:true adoption through the buried Dictionary layer to the existential leaf (every value cell's +1 and every carrier's CoW retain)");
        TestLogger.Info($"[[String: any Marker]]: 40 returns x {outerPerCall} inner dicts x {innerPerOuter} buried class-bound existentials all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeNestedExistentialArrayOfMaps(int iterations, int outerPerCall, int innerPerOuter)
    {
        for (int i = 0; i < iterations; i++)
        {
            var outerList = TestLibFunctions.MakeTrackedMarkerArrayOfMaps(outerPerCall, innerPerOuter);
            try
            {
                foreach (var row in outerList)
                {
                    try
                    {
                        foreach (var marker in row.Values)
                            (marker as IDisposable)?.Dispose();
                    }
                    finally
                    {
                        (row as IDisposable)?.Dispose();
                    }
                }
            }
            finally
            {
                (outerList as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// NESTED owned <c>[String: [String: any Marker]]</c> return (audit L229), Dictionary-of-dictionaries:
    /// the buried existential sits in an INVARIANT <c>IReadOnlyDictionary</c> value slot — the shape the
    /// AoA/DoA/AoM probes above MISS, because their outer container is a COVARIANT <c>IReadOnlyList</c> that
    /// silently absorbs a concrete inner <c>Dictionary</c>. Here the inner <c>DictionaryProjection</c> element
    /// conversion must surface its declared <c>IReadOnlyDictionary&lt;string, IMarker&gt;</c> interface or the
    /// generated selector is a CS0266 (the regression observed on ObjectMapper's <c>[String: [String: any P]]</c>
    /// returns). Materializing the outer dict yields owned inner-dictionary carriers; enumerating each inner
    /// dict's <c>.Values</c> yields owned marker proxies. Disposing every proxy, every inner-dictionary
    /// carrier, and the outer carrier must drive the live count to 0.
    /// </summary>
    public void TestNestedClassBoundExistentialMapOfMapsReturnReleasesValues()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int outerPerCall = 4;
        const int innerPerOuter = 3;
        AllocAndDisposeNestedExistentialMapOfMaps(40, outerPerCall, innerPerOuter);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("[String: [String: any Marker]] return must recurse owns:true adoption through the buried Dictionary layer to the existential leaf (every value cell's +1 and every carrier's CoW retain)");
        TestLogger.Info($"[String: [String: any Marker]]: 40 returns x {outerPerCall} inner dicts x {innerPerOuter} buried class-bound existentials all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeNestedExistentialMapOfMaps(int iterations, int outerPerCall, int innerPerOuter)
    {
        for (int i = 0; i < iterations; i++)
        {
            var outerDict = TestLibFunctions.MakeTrackedMarkerMapOfMaps(outerPerCall, innerPerOuter);
            try
            {
                foreach (var row in outerDict.Values)
                {
                    try
                    {
                        foreach (var marker in row.Values)
                            (marker as IDisposable)?.Dispose();
                    }
                    finally
                    {
                        (row as IDisposable)?.Dispose();
                    }
                }
            }
            finally
            {
                (outerDict as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// NESTED owned <c>[String: [[String: any Marker]]]</c> return (audit L229), three-level
    /// Dictionary→Array→Dictionary: the EXACT shape that regressed FirebaseFirestore/ObjectMapper's
    /// <c>[String: [[String: any P]]]</c> returns. The outer Dictionary VALUE slot is INVARIANT and its
    /// value is a COVARIANT <c>IReadOnlyList</c> whose elements are concrete inner dictionaries, so the
    /// array conversion's <c>IReadOnlyList&lt;Dictionary&lt;…&gt;&gt;</c> must be cast to its declared
    /// <c>IReadOnlyList&lt;IReadOnlyDictionary&lt;…&gt;&gt;</c> public type in the outer dictionary's
    /// AsProjected value selector or the generated selector is a CS0266. The MapOfMaps probe above nests a
    /// Dictionary DIRECTLY under the invariant value slot; this probe proves the cast also reaches through
    /// the intermediate Array layer. Materializing the outer dict yields owned inner-array carriers; each
    /// inner array yields owned inner-dictionary carriers; each inner dict's <c>.Values</c> yields owned
    /// marker proxies. Disposing every proxy, every inner-dictionary carrier, every inner-array carrier,
    /// and the outer carrier must drive the live count to 0.
    /// </summary>
    public void TestNestedClassBoundExistentialMapOfArrayOfMapsReturnReleasesValues()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int outerPerCall = 3;
        const int midPerOuter = 2;
        const int innerPerMid = 3;
        AllocAndDisposeNestedExistentialMapOfArrayOfMaps(40, outerPerCall, midPerOuter, innerPerMid);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("[String: [[String: any Marker]]] return must recurse owns:true adoption through the buried Array and Dictionary layers to the existential leaf (every value cell's +1 and every carrier's CoW retain)");
        TestLogger.Info($"[String: [[String: any Marker]]]: 40 returns x {outerPerCall} outer keys x {midPerOuter} inner arrays x {innerPerMid} buried class-bound existentials all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeNestedExistentialMapOfArrayOfMaps(int iterations, int outerPerCall, int midPerOuter, int innerPerMid)
    {
        for (int i = 0; i < iterations; i++)
        {
            var outerDict = TestLibFunctions.MakeTrackedMarkerMapOfArrayOfMaps(outerPerCall, midPerOuter, innerPerMid);
            try
            {
                foreach (var rows in outerDict.Values)
                {
                    try
                    {
                        foreach (var row in rows)
                        {
                            try
                            {
                                foreach (var marker in row.Values)
                                    (marker as IDisposable)?.Dispose();
                            }
                            finally
                            {
                                (row as IDisposable)?.Dispose();
                            }
                        }
                    }
                    finally
                    {
                        (rows as IDisposable)?.Dispose();
                    }
                }
            }
            finally
            {
                (outerDict as IDisposable)?.Dispose();
            }
        }
    }
}
