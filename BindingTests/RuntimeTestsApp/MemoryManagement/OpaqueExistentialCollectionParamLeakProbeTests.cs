// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Probes ARC balance for passing Swift-vended (borrowed) OPAQUE (non-class-bound) existential
/// conformers BACK to Swift as a collection element — <c>[any BugReproExistentialItem]</c> /
/// <c>[String: any BugReproExistentialItem]</c>. This is the opaque sibling of the class-bound
/// PARAM/WRITE carrier audit (P1-08): a non-class-bound <c>any P</c> strides over the full 40-byte
/// <see cref="Swift.Runtime.ExistentialContainer1"/>, not the compact 16-byte
/// <see cref="Swift.Runtime.ClassExistentialContainer1"/>.
///
/// The C#→Swift collection-element conversion runs <c>GetOrCreate(...).GetExistentialContainer()</c>,
/// which for a Swift-vended proxy returns the proxy's stored container by raw value — ALIASING the
/// proxy's only +1. <c>SwiftArray&lt;ExistentialContainer1&gt;.FromEnumerable</c> raw-copies that
/// container into the array (<c>SwiftMarshal.MarshalToSwift</c> → <c>IExistentialContainer.CopyTo</c>,
/// a +0 memcpy) and the <c>__owned</c> <c>Array.append</c> (<c>$sSa6appendyyxnF</c>) consumes it. The
/// generated marshalling disposes the temporary array synchronously (<c>using var xsSwift</c>), so the
/// array's existential value-witness destroy OVER-RELEASES the proxy's payload before the call even
/// returns. The fix mints/donates the carrier's own +1 (the opaque analogue of
/// <c>ExistentialContainerFactory.CreateOwnedClassCarrier</c>, via the existential value-witness
/// <c>InitializeWithCopy</c>).
///
/// <c>TrackedOpaqueItem</c> feeds the same shared <see cref="LifetimeTracker"/> counters as the
/// class-bound <c>MarkerImpl</c>, so the balance is asserted directly and deterministically (no GC
/// dependence): N Swift-backed proxies are vended (live == N), passed through the param, and the live
/// count is checked WHILE the proxies are still rooted. An aliasing carrier shows up as the temp
/// array's <c>__owned</c> teardown prematurely deinit'ing each source proxy (live drops below N right
/// after the call) and/or a double-free crash when the proxy's own <c>Dispose</c> releases the
/// already-freed payload. A correct carrier leaves the source proxies at +1 (live stays N), and
/// disposing them drives the count back to 0.
///
/// This is the param/write counterpart to <see cref="ClassBoundExistentialCollectionLeakProbeTests"/>
/// (which covers the owned-RETURN direction) and the ownership probe the opaque collection-element
/// carrier should have shipped with.
/// </summary>
public class OpaqueExistentialCollectionParamLeakProbeTests : TestBase
{
    public OpaqueExistentialCollectionParamLeakProbeTests(TestResults results) : base(results) { }

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
    /// Passing Swift-vended opaque proxies as <c>[any BugReproExistentialItem]</c> must add the array's
    /// own +1 per element rather than aliasing each source proxy's only +1. With the proxies still
    /// rooted, the live count must remain N across the call (no premature deinit from the temp array's
    /// <c>__owned</c> teardown); disposing the proxies must then drive the count to 0.
    /// </summary>
    public void TestOpaqueExistentialArrayParamDoesNotOverReleaseElements()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int count = 5;
        ProbeOpaqueArrayParam(count);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("[any BugReproExistentialItem] param must release exactly the +1s it minted (source proxies disposed → 0)");
        TestLogger.Info($"[any BugReproExistentialItem] param: {count} Swift-vended proxies survived the round-trip and released cleanly");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbeOpaqueArrayParam(int count)
    {
        var proxies = new List<IBugReproExistentialItem>(count);
        for (int i = 0; i < count; i++)
            proxies.Add(TestLibFunctions.MakeTrackedOpaqueItem(i));

        try
        {
            // Pass the Swift-vended proxies as [any P]. The marshal builds a SwiftArray<EC1>,
            // __owned-appends each element, and disposes the temp array synchronously inside the call.
            string joined = TestLibFunctions.JoinItemDescriptions(proxies);

            // Touch every element's projection (describe() ran on each opaque existential): proves the
            // carrier marshalled correctly, not just element[0].
            string expected = string.Join(",", Enumerable.Range(0, count).Select(i => $"item-{i}"));
            if (joined != expected)
                throw new AssertionException(
                    $"[any BugReproExistentialItem] param marshalled wrong: expected '{expected}', got '{joined}'");

            // The proxies are still rooted on the stack here, so a live-count drop below `count` can
            // ONLY mean the temp array's __owned teardown over-released each source proxy's only +1
            // (premature deinit). A correct carrier minted its own +1, so the source proxies are intact.
            LifetimeTracker.AssertLiveCount(count,
                "[any BugReproExistentialItem] param must not steal the source proxy's +1 (temp-array __owned teardown over-release)");
        }
        finally
        {
            foreach (var p in proxies)
                (p as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Dictionary VALUE sibling: passing Swift-vended opaque proxies as
    /// <c>[String: any BugReproExistentialItem]</c> must add the dictionary's own +1 per value rather
    /// than aliasing each source proxy's only +1 (the value carrier routes through the same
    /// <c>GetArrayElementCarrierConversion</c> as the array element).
    /// </summary>
    public void TestOpaqueExistentialMapParamDoesNotOverReleaseValues()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        const int count = 5;
        ProbeOpaqueMapParam(count);
        DrainFinalizers();

        LifetimeTracker.AssertNoLeaks("[String: any BugReproExistentialItem] param must release exactly the +1s it minted (source proxies disposed → 0)");
        TestLogger.Info($"[String: any BugReproExistentialItem] param: {count} Swift-vended proxies survived the round-trip and released cleanly");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ProbeOpaqueMapParam(int count)
    {
        var proxies = new Dictionary<string, IBugReproExistentialItem>(count);
        for (int i = 0; i < count; i++)
            proxies[$"k{i:D2}"] = TestLibFunctions.MakeTrackedOpaqueItem(i);

        try
        {
            string joined = TestLibFunctions.JoinItemDescriptionsByKey(proxies);

            string expected = string.Join(",", Enumerable.Range(0, count).Select(i => $"item-{i}"));
            if (joined != expected)
                throw new AssertionException(
                    $"[String: any BugReproExistentialItem] param marshalled wrong: expected '{expected}', got '{joined}'");

            LifetimeTracker.AssertLiveCount(count,
                "[String: any BugReproExistentialItem] param must not steal the source proxy's +1 (temp-dict __owned teardown over-release)");
        }
        finally
        {
            foreach (var p in proxies.Values)
                (p as IDisposable)?.Dispose();
        }
    }
}
