// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Probes whether the indirect-result / by-value return cleanup leaks the retains
/// copied out of the wire buffer for non-struct copy-out carriers, specifically:
///   - <c>Optional&lt;FrozenTrackedRefStruct&gt;</c> — wire carrier is SwiftOptional&lt;…&gt;,
///     whose non-POD NewFromPayload runs InitializeWithCopy (a +1 on the embedded ref).
///   - <c>[TrackedRef]</c> — wire carrier is SwiftArray&lt;…&gt;, whose NewFromPayload runs
///     InitializeWithCopy (a +1 on the CoW storage holding every element).
///
/// The frozen-struct categories (StructVwtDestroyLeakTests) proved the value-witness
/// Destroy fires for the FrozenWithMemoryProjection shape. These two carriers copy out
/// the same way but are NOT FrozenWithMemoryProjection at the top level, so they exercise
/// whether the cleanup gate covers the whole copy-out category or only frozen structs.
///
/// Each embedded ref is a <see cref="LifetimeTracker"/>-counted TrackedRef, so an orphaned
/// retain shows up as a non-zero live count after the wrappers are disposed and the GC has
/// drained — not merely as "does not crash".
///
/// The collection probes dispose the returned carrier (the projection over the SwiftArray /
/// SwiftDictionary, or the SwiftSet itself) in a <c>finally</c>, releasing its retain on the
/// Swift copy-on-write storage deterministically. Disposing the extracted element wrappers alone
/// is not enough — the carrier holds its own +1 on the CoW storage that backs every element, and
/// relying on GC finalization for that retain leaves the last iteration's carrier rooted (a false
/// "N leaked" residual). The loop still runs in a <c>[MethodImpl(NoInlining)]</c> helper so no
/// stale stack slot keeps a carrier alive past its <c>Dispose</c>.
/// </summary>
public class WireCarrierLeakProbeTests : TestBase
{
    public WireCarrierLeakProbeTests(TestResults results) : base(results) { }

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
    /// Optional&lt;frozen-struct-with-ref&gt; return: disposing the wrapper must release the
    /// embedded TrackedRef. A leaked wire-buffer retain would pin it at +1 per call. The small
    /// (one-ref) struct returns Optional via the indirect-result <c>_cdeclBuf</c> arm.
    /// </summary>
    public void TestOptionalFrozenStructReturnReleasesEmbeddedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        for (int i = 0; i < 200; i++)
        {
            var opt = TestLibFunctions.MakeOptionalFrozenTrackedRefStruct(true, i);
            opt?.Dispose();
        }
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("Optional<frozen-with-ref> return must not orphan the wire-buffer copy's retain");
        TestLogger.Info("Optional<frozen-with-ref>: 200 present returns released their embedded ref");
    }

    /// <summary>
    /// Optional&lt;large-frozen-struct-with-5-refs&gt; return: the 40-byte payload is too large
    /// for a register return, so it goes through the indirect-result (<c>_cdeclBuf</c>) arm —
    /// exercising the builder-side <c>DestroyWireBufferRetains</c> cleanup directly. Disposing
    /// the wrapper must release all five embedded TrackedRefs; a leaked carrier retain pins
    /// five per call.
    /// </summary>
    public void TestOptionalLargeFrozenStructReturnReleasesEmbeddedRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        for (int i = 0; i < 200; i++)
        {
            var opt = TestLibFunctions.MakeOptionalLargeFrozenTrackedRefStruct(true, i);
            opt?.Dispose();
        }
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("Optional<large-frozen-with-5-refs> indirect return must not orphan the wire-buffer copy's retains");
        TestLogger.Info("Optional<large-frozen-with-5-refs>: 200 present returns released all embedded refs");
    }

    /// <summary>
    /// Array-of-class return: the CoW storage holds every element's reference. Disposing
    /// the element wrappers must drive the live count to 0; a leaked array-carrier retain
    /// would pin all <c>count</c> elements per call.
    /// </summary>
    public void TestArrayOfClassReturnReleasesElements()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int elementsPerCall = 5;
        AllocAndDisposeArrays(50, elementsPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("[TrackedRef] return must not orphan the SwiftArray carrier's retain on the CoW storage");
        TestLogger.Info($"[TrackedRef]: 50 returns x {elementsPerCall} elements all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeArrays(int iterations, int elementsPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            var list = TestLibFunctions.MakeTrackedRefArray(elementsPerCall);
            try
            {
                foreach (var element in list)
                    element.Dispose();
            }
            finally
            {
                // The projection owns the SwiftArray carrier (a +1 on the CoW storage holding
                // every element). Disposing the extracted element wrappers only releases the
                // extraction copies; the carrier retain is released deterministically here
                // rather than left to GC finalization (otherwise the last iteration's carrier
                // stays rooted and reads as a residual leak).
                (list as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// Dictionary-of-class-value return: the SwiftDictionary carrier copies the CoW storage
    /// (+1). Disposing the value wrappers must drive the live count to 0; a leaked carrier
    /// retain pins every value per call.
    /// </summary>
    public void TestDictionaryOfClassReturnReleasesValues()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int entriesPerCall = 5;
        AllocAndDisposeDicts(50, entriesPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("[Int32: TrackedRef] return must not orphan the SwiftDictionary carrier's retain");
        TestLogger.Info($"[Int32: TrackedRef]: 50 returns x {entriesPerCall} values all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeDicts(int iterations, int entriesPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            var dict = TestLibFunctions.MakeTrackedRefDict(entriesPerCall);
            try
            {
                foreach (var value in dict.Values)
                    value.Dispose();
            }
            finally
            {
                (dict as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// Set-of-class return: the SwiftSet carrier copies the CoW storage (+1). Disposing the
    /// member wrappers must drive the live count to 0; a leaked carrier retain pins every
    /// member per call.
    /// </summary>
    [SkipOnDevice("Generator bug: class Set-elements need a Hashable witness table, but the generator emits no WitnessTableDispatcher.Register for class conformances, so the unconstrained HashableConformanceRegistry.GetHashableWitnessTable<T> path falls to reflection MakeGenericMethod (AOT-incompatible). Orthogonal to the wire-carrier leak fix this probe validates on Mono; needs generated witness-table pre-registration.")]
    public void TestSetOfClassReturnReleasesMembers()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int membersPerCall = 5;
        AllocAndDisposeSets(50, membersPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("Set<TrackedRef> return must not orphan the SwiftSet carrier's retain");
        TestLogger.Info($"Set<TrackedRef>: 50 returns x {membersPerCall} members all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocAndDisposeSets(int iterations, int membersPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            var set = TestLibFunctions.MakeTrackedRefSet(membersPerCall);
            try
            {
                foreach (var member in set)
                    member.Dispose();
            }
            finally
            {
                // Set is returned as the SwiftSet carrier itself (no projection), already
                // IDisposable; release its CoW-storage retain deterministically.
                (set as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// Result-with-class-success return: the SwiftResult carrier copies the success payload
    /// (+1 on the embedded ref). Disposing the success wrapper must drive the live count to 0.
    /// </summary>
    public void TestResultOfClassReturnReleasesSuccessRef()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        for (int i = 0; i < 200; i++)
        {
            // The SwiftResult carrier copies the success payload (+1 on the embedded ref) and
            // owns it independently of the extracted .Success wrapper — dispose BOTH: the carrier
            // (via using) to release its copied retain, and the extracted Success wrapper.
            using var result = TestLibFunctions.MakeTrackedRefResult(true, i);
            if (result.IsSuccess)
                result.Success?.Dispose();
        }
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("Result<TrackedRef,_> success return must not orphan the SwiftResult carrier's retain");
        TestLogger.Info("Result<TrackedRef,_>: 200 success returns released their embedded ref");
    }
}
