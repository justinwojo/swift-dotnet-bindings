// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Audit P1-08, WRITE + PARAM half: class-bound (`: GestureHostBase`) `[any Marker]` arrays crossing
/// C# → Swift. The READ half (Swift → C#, <see cref="ClassBoundExistentialArrayTests"/>) was already
/// fixed by routing the SwiftArray element carrier through the 16-byte
/// <c>ClassExistentialContainer1</c>. The symmetric write/param directions still built
/// <c>SwiftArray&lt;ExistentialContainer1&gt;</c> (40-byte slots), so once Swift strode at 16 bytes it
/// read garbage classRef/witness for every element after [0] → SIGSEGV / over-release. Only the
/// return direction was tested, so this half shipped untested.
///
/// The two crossing points:
/// <list type="bullet">
/// <item><b>PARAM</b> — C# passes <c>IEnumerable&lt;IMarker&gt;</c> into a Swift <c>func</c>
///   (<c>ArrayProjection</c> FromEnumerable);</item>
/// <item><b>WRITE</b> — a C# class implements a protocol whose getter returns <c>[any Marker]</c>;
///   Swift calls the getter and reads the array back (<c>ProtocolProxyEmitter.Receivers</c>).</item>
/// </list>
///
/// Both narrow each element from the proxy/boxable-produced <c>ExistentialContainer1</c> to
/// <c>ClassExistentialContainer1</c>. There are two reachable EC1 layouts for a class-bound conformer,
/// and the narrowing must handle BOTH (else half of real call sites crash):
/// <list type="bullet">
/// <item><b>proxy layout</b> — a Swift-vended conformer wrapped in <c>{P}Proxy</c>
///   (<c>MarkerVendor.Make</c>): class instance in Payload0, witness table in Payload1;</item>
/// <item><b>boxable layout</b> — a concrete Swift class passed by value (<c>new MarkerImpl(...)</c> →
///   <c>IExistentialBoxable.BoxAsExistential1</c> → <c>Create</c>): class instance in Payload0,
///   Payload1 zero, witness table in the dedicated witness word.</item>
/// </list>
/// The boxable-element tests below would crash (null witness → dispatch on null) under a naive
/// "witness == Payload1" narrowing — they pin the layout-agnostic narrowing in place.
///
/// Each consumer sums <c>markerId()</c> across the whole array (indexing every element, not just [0])
/// so a wrong stride surfaces as a crash or wrong sum rather than a lucky element[0] hit.
/// </summary>
public class ClassBoundExistentialArrayWriteParamTests : TestBase
{
    public ClassBoundExistentialArrayWriteParamTests(TestResults results) : base(results) { }

    #region PARAM direction (C# IEnumerable<IMarker> → Swift func)

    /// <summary>
    /// PARAM, proxy-layout elements: Swift-vended conformers (<c>MarkerVendor.Make</c>) are proxies
    /// whose existential carries the witness in Payload1. Summing across all three proves the
    /// 16-byte stride — a 40-byte fill would have Swift read element[1]/[2] at the wrong offset.
    /// </summary>
    public void TestSumMarkerIdsParamProxyElements()
    {
        using var vendor = new MarkerVendor();
        var markers = new List<IMarker> { vendor.Make(1), vendor.Make(2), vendor.Make(3) };

        var sum = (int)TestLibFunctions.SumMarkerIds(markers);

        AssertEqual(6, sum, "SumMarkerIds over proxy-layout class-bound elements (1+2+3)");
        TestLogger.Info($"PARAM proxy elements: SumMarkerIds = {sum}");
    }

    /// <summary>
    /// PARAM, boxable-layout elements: <c>new MarkerImpl(...)</c> is a concrete Swift class passed by
    /// value, so <c>GetOrCreate</c> takes the <c>IExistentialBoxable</c> branch and the existential
    /// carries the witness in the dedicated witness word with Payload1 zero. The layout-agnostic
    /// narrowing must read the witness from that word; the naive "Payload1" narrowing would hand Swift
    /// a null witness and crash on the first dispatch.
    /// </summary>
    public void TestSumMarkerIdsParamBoxableElements()
    {
        var markers = new List<IMarker>
        {
            new MarkerImpl((nint)10),
            new MarkerImpl((nint)20),
            new MarkerImpl((nint)30),
        };

        var sum = (int)TestLibFunctions.SumMarkerIds(markers);

        AssertEqual(60, sum, "SumMarkerIds over boxable-layout class-bound elements (10+20+30)");
        TestLogger.Info($"PARAM boxable elements: SumMarkerIds = {sum}");
    }

    /// <summary>
    /// PARAM, mixed layouts in one array: a proxy element and boxable elements interleaved. Proves the
    /// narrowing keys off each element's own witness slot, not a per-array assumption.
    /// </summary>
    public void TestSumMarkerIdsParamMixedLayouts()
    {
        using var vendor = new MarkerVendor();
        var markers = new List<IMarker>
        {
            new MarkerImpl((nint)100),   // boxable layout
            vendor.Make(7),        // proxy layout
            new MarkerImpl((nint)200),   // boxable layout
        };

        var sum = (int)TestLibFunctions.SumMarkerIds(markers);

        AssertEqual(307, sum, "SumMarkerIds over mixed proxy/boxable elements (100+7+200)");
        TestLogger.Info($"PARAM mixed layouts: SumMarkerIds = {sum}");
    }

    /// <summary>
    /// PARAM, count-only: proves the array header marshals even when no element is indexed — the
    /// <c>Count</c>-succeeds / index-crashes asymmetry that masked the stride bug behind a passing
    /// header read.
    /// </summary>
    public void TestCountMarkersParam()
    {
        using var vendor = new MarkerVendor();
        var markers = new List<IMarker> { vendor.Make(1), new MarkerImpl((nint)2), vendor.Make(3), new MarkerImpl((nint)4) };

        var count = (int)TestLibFunctions.CountMarkers(markers);

        AssertEqual(4, count, "CountMarkers header read");
        TestLogger.Info($"PARAM count-only: CountMarkers = {count}");
    }

    /// <summary>
    /// PARAM ownership balance: passing a class-bound marker through a Swift array must NOT steal
    /// the source's +1. Swift's <c>Array.append</c> (<c>$sSa6appendyyxnF</c>) is <c>__owned</c> — it
    /// consumes the element at +1 — and the array's class-existential value-witness table releases
    /// word0 on destroy. The 16-byte <c>ClassExistentialContainer1</c> carrier merely <i>aliases</i>
    /// an existing class +1 (the proxy that <c>Make</c> vended, or the boxable wrapper), so the
    /// marshalling-into-array step must add its own +1 (<c>Arc.UnknownObjectRetain</c> on word0). Pre-
    /// fix it wrote the carrier at +0, so the array's destroy stole the source's +1 — deiniting a
    /// live object while C# still held the proxy/wrapper. That over-release surfaced only as a
    /// teardown SIGSEGV (the functional sum/count assertions all still passed). This test makes it
    /// deterministic via the shared lifetime counters: after the round-trip the sources MUST still be
    /// live; pre-fix the live count drops to 0 synchronously when <c>SumMarkerIds</c> disposes its
    /// internal array.
    /// </summary>
    public void TestSumMarkerIdsParamDoesNotStealElementOwnership()
    {
        // The allocation counter is process-global. Drain any pending finalizers left by earlier
        // tests BEFORE resetting it — otherwise their deferred Swift deinits land after Reset()
        // (during this test's GC.WaitForPendingFinalizers below) and drive the live count negative.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        LifetimeTracker.Reset();
        using var vendor = new MarkerVendor();

        IMarker proxyMarker = vendor.Make(1);             // Swift-vended proxy (proxy layout)
        IMarker boxableMarker = new MarkerImpl((nint)2);  // boxable conformer wrapper (boxable layout)
        LifetimeTracker.LogStats("after creating proxy + boxable marker");
        LifetimeTracker.AssertLiveCount(2, "after creating proxy + boxable marker");

        var sum = (int)TestLibFunctions.SumMarkerIds(new List<IMarker> { proxyMarker, boxableMarker });
        AssertEqual(3, sum, "SumMarkerIds(1+2)");

        // The internal SwiftArray is disposed synchronously inside SumMarkerIds. If the consuming
        // append under-retained, the array destroy already deinit'd both source objects here.
        GC.KeepAlive(proxyMarker);
        GC.KeepAlive(boxableMarker);
        LifetimeTracker.LogStats("after SumMarkerIds round-trip");
        LifetimeTracker.AssertLiveCount(2, "after SumMarkerIds round-trip — sources must survive");

        // Releasing the C# owners drives both Swift objects to deinit exactly once: no leak (live
        // reaches 0) and no double-free (no crash from releasing an already-freed object).
        (proxyMarker as IDisposable)?.Dispose();
        (boxableMarker as IDisposable)?.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        LifetimeTracker.LogStats("after disposing both markers");
        LifetimeTracker.AssertLiveCount(0, "after disposing both markers — no leak, no double-free");
        TestLogger.Info("PARAM ownership balance: source markers survived the array round-trip and freed exactly once");
    }

    #endregion

    #region WRITE direction (C#-implemented getter → Swift reads it back)

    /// <summary>
    /// A pure C# implementation of <see cref="IMarkerProvider"/> whose <c>Markers</c> getter returns a
    /// caller-supplied list. Swift reaches it through the generated proxy/receiver, so the receiver
    /// getter must build <c>SwiftArray&lt;ClassExistentialContainer1&gt;</c> with each element narrowed
    /// to the 16-byte carrier.
    /// </summary>
    private sealed class CSharpMarkerProvider : IMarkerProvider
    {
        private readonly IReadOnlyList<IMarker> _markers;
        public CSharpMarkerProvider(IReadOnlyList<IMarker> markers) => _markers = markers;
        public IReadOnlyList<IMarker> Markers => _markers;
    }

    /// <summary>
    /// WRITE, proxy-layout elements: the C# getter returns Swift-vended proxies; Swift's
    /// <c>consumeMarkerProvider</c> indexes the returned array and sums the ids.
    /// </summary>
    public void TestConsumeMarkerProviderWriteProxyElements()
    {
        using var vendor = new MarkerVendor();
        var provider = new CSharpMarkerProvider(new List<IMarker> { vendor.Make(4), vendor.Make(5), vendor.Make(6) });

        var sum = (int)TestLibFunctions.ConsumeMarkerProvider(provider);

        AssertEqual(15, sum, "ConsumeMarkerProvider over C#-returned proxy elements (4+5+6)");
        TestLogger.Info($"WRITE proxy elements: ConsumeMarkerProvider = {sum}");
    }

    /// <summary>
    /// WRITE, boxable-layout elements: the C# getter returns <c>new MarkerImpl(...)</c> by value, so
    /// the receiver getter narrows each from the boxable (witness-word) layout. The naive Payload1
    /// narrowing would crash here on the Swift side's first dispatch.
    /// </summary>
    public void TestConsumeMarkerProviderWriteBoxableElements()
    {
        var provider = new CSharpMarkerProvider(new List<IMarker> { new MarkerImpl((nint)11), new MarkerImpl((nint)12), new MarkerImpl((nint)13) });

        var sum = (int)TestLibFunctions.ConsumeMarkerProvider(provider);

        AssertEqual(36, sum, "ConsumeMarkerProvider over C#-returned boxable elements (11+12+13)");
        TestLogger.Info($"WRITE boxable elements: ConsumeMarkerProvider = {sum}");
    }

    #endregion

    #region Opaque (non-class-bound) control — must stay on the 40-byte carrier

    /// <summary>
    /// Control: <c>BugReproExistentialItem</c> has no class constraint, so its existential keeps the
    /// 40-byte <c>ExistentialContainer1</c> carrier in both directions. Passing it through a Swift
    /// param must still round-trip correctly — proving the write/param carrier change is surgical to
    /// class-bound elements and did not regress the opaque path.
    /// </summary>
    public void TestJoinItemDescriptionsOpaqueControl()
    {
        var items = new List<IBugReproExistentialItem>
        {
            new BugReproExistentialItemImpl("alpha"),
            new BugReproExistentialItemImpl("beta"),
            new BugReproExistentialItemImpl("gamma"),
        };

        var joined = TestLibFunctions.JoinItemDescriptions(items);

        AssertEqual("alpha,beta,gamma", joined, "JoinItemDescriptions over opaque (40-byte) existential array");
        TestLogger.Info($"OPAQUE control: JoinItemDescriptions = {joined}");
    }

    #endregion
}
