// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Async sibling of <see cref="WireCarrierLeakProbeTests"/>: probes whether an async function
/// returning a frozen stdlib container (<c>[Class]</c>, <c>[ResilientStruct]</c>,
/// <c>[K: Class]</c>, <c>Set&lt;Class&gt;</c>) leaks the result carrier's value-witness +1.
///
/// The async start-thunk writes the result into the carrier via
/// <c>initializeMemory(as: &lt;Container&gt;.self, repeating: result, count: 1)</c>, which runs the
/// container's copy witness — a +1 on the copy-on-write storage that backs every element. The C#
/// completion callback then revives the container with <c>MarshalFromSwift&lt;SwiftArray/…&gt;</c>,
/// which takes its OWN independent +1 (NewFromPayload → InitializeWithCopy into a managed buffer).
/// Unless the callback value-witness-Destroys the carrier before <c>SBW_Free</c> reclaims the raw
/// allocation, that carrier +1 is orphaned and the container's backing storage leaks every call.
///
/// Each element embeds a <see cref="LifetimeTracker"/>-counted TrackedRef, so an orphaned carrier
/// retain shows up as a non-zero live count after the wrappers are disposed and the GC has drained —
/// not merely as "does not crash". These probes are the async-harness analogue of the SYNC
/// collection probes in <see cref="WireCarrierLeakProbeTests"/>.
///
/// The dispose loops run in <c>[MethodImpl(NoInlining)]</c> async helpers so the completed state
/// machine (which holds the awaited carrier local) is collectible before the leak assertion. Each
/// call is bounded by <c>WithTimeout(DefaultAsyncTimeout)</c> so a regressed completion callback
/// fails the probe bounded instead of hanging the run. As in the sync probes, disposing the
/// extracted element wrappers alone is not enough — the projection/SwiftSet carrier holds its own
/// +1 on the CoW storage, released deterministically in a <c>finally</c> rather than left to GC.
/// </summary>
public class AsyncCollectionCarrierLeakProbeTests : TestBase
{
    public AsyncCollectionCarrierLeakProbeTests(TestResults results) : base(results) { }

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
    /// <c>async -&gt; [TrackedRef]</c>: the SwiftArray carrier holds a +1 on the CoW storage backing
    /// every element. The async completion callback must value-witness-Destroy that carrier; a leak
    /// pins all <c>count</c> elements per awaited call.
    /// </summary>
    public async Task TestAsyncArrayOfClassReturnReleasesElements()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int elementsPerCall = 5;
        await AllocAndDisposeArraysAsync(50, elementsPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("async [TrackedRef] return must not orphan the SwiftArray carrier's retain on the CoW storage");
        TestLogger.Info($"async [TrackedRef]: 50 awaited returns x {elementsPerCall} elements all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AllocAndDisposeArraysAsync(int iterations, int elementsPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            var list = await WithTimeout(TestLibFunctions.FetchTrackedRefArrayAsync(elementsPerCall), DefaultAsyncTimeout);
            try
            {
                foreach (var element in list)
                    element.Dispose();
            }
            finally
            {
                // The projection owns the SwiftArray carrier (a +1 on the CoW storage backing every
                // element). Disposing the extracted element wrappers only releases the extraction
                // copies; the carrier retain is released deterministically here.
                (list as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// <c>async -&gt; [TrackedRefStruct]</c>: an array of a NON-frozen (resilient) struct embedding a
    /// TrackedRef. The carrier's +1 pins the CoW storage holding every struct buffer; the async
    /// callback must value-witness-Destroy it. Disposing each struct wrapper releases the embedded
    /// ref, and disposing the projection releases the carrier.
    /// </summary>
    public async Task TestAsyncArrayOfResilientStructReturnReleasesElements()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int elementsPerCall = 5;
        await AllocAndDisposeStructArraysAsync(50, elementsPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("async [TrackedRefStruct] return must not orphan the SwiftArray carrier's retain on the CoW storage");
        TestLogger.Info($"async [TrackedRefStruct]: 50 awaited returns x {elementsPerCall} elements all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AllocAndDisposeStructArraysAsync(int iterations, int elementsPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            var list = await WithTimeout(TestLibFunctions.FetchTrackedRefStructArrayAsync(elementsPerCall), DefaultAsyncTimeout);
            try
            {
                foreach (var element in list)
                    element.Dispose();
            }
            finally
            {
                (list as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>
    /// <c>async -&gt; [Int32: TrackedRef]</c>: the SwiftDictionary carrier copies the CoW storage (+1).
    /// The async callback must value-witness-Destroy it; a leak pins every value per awaited call.
    /// </summary>
    public async Task TestAsyncDictionaryOfClassReturnReleasesValues()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int entriesPerCall = 5;
        await AllocAndDisposeDictsAsync(50, entriesPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("async [Int32: TrackedRef] return must not orphan the SwiftDictionary carrier's retain");
        TestLogger.Info($"async [Int32: TrackedRef]: 50 awaited returns x {entriesPerCall} values all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AllocAndDisposeDictsAsync(int iterations, int entriesPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            var dict = await WithTimeout(TestLibFunctions.FetchTrackedRefDictAsync(entriesPerCall), DefaultAsyncTimeout);
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
    /// <c>async -&gt; Set&lt;TrackedRef&gt;</c>: the SwiftSet carrier copies the CoW storage (+1). The async
    /// callback must value-witness-Destroy it; a leak pins every member per awaited call.
    /// </summary>
    public async Task TestAsyncSetOfClassReturnReleasesMembers()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int membersPerCall = 5;
        await AllocAndDisposeSetsAsync(50, membersPerCall);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("async Set<TrackedRef> return must not orphan the SwiftSet carrier's retain");
        TestLogger.Info($"async Set<TrackedRef>: 50 awaited returns x {membersPerCall} members all released");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AllocAndDisposeSetsAsync(int iterations, int membersPerCall)
    {
        for (int i = 0; i < iterations; i++)
        {
            var set = await WithTimeout(TestLibFunctions.FetchTrackedRefSetAsync(membersPerCall), DefaultAsyncTimeout);
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
}
