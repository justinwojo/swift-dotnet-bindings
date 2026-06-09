// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Dictionary sibling: class-bound (`: GestureHostBase`) `[String: any Marker]`
/// dictionaries crossing C# ↔ Swift. The array fix routed the SwiftArray element carrier through the
/// 16-byte <c>ClassExistentialContainer1</c>; the equivalent dictionary VALUE paths still built
/// <c>SwiftDictionary&lt;_, ExistentialContainer1&gt;</c> (40-byte value slots). Because the existential
/// layout is a property of the TYPE (<c>MemoryLayout&lt;any Marker&gt;.stride == 16</c>) and not the
/// container, a `[String: any Marker]` stores 16-byte values just like `[any Marker]` stores 16-byte
/// elements — so the 40-byte carrier read garbage classRef/witness for every value → SIGSEGV /
/// over-release on dispatch.
///
/// Dictionary KEYS are never class-bound existentials — <c>any P</c> is not <c>Hashable</c>, so
/// <c>[any P: V]</c> is ill-formed — so only the value crosses through the existential carrier; the
/// key stays on its normal carrier. The two crossing points mirror the array test:
/// <list type="bullet">
/// <item><b>PARAM</b> — C# passes <c>IDictionary&lt;string, IMarker&gt;</c> into a Swift <c>func</c>
///   (<c>DictionaryProjection</c> FromDictionary);</item>
/// <item><b>WRITE</b> — a C# class implements a protocol whose getter returns
///   <c>[String: any Marker]</c>; Swift calls the getter and reads the values back
///   (<c>ProtocolProxyEmitter.Receivers</c> dict-value getter carrier, and transitively the setter's
///   <c>MarshalFromSwift&lt;SwiftDictionary&lt;_, carrier&gt;&gt;</c>).</item>
/// </list>
///
/// As in the array case there are two reachable EC1 layouts for a class-bound conformer and the
/// narrowing must handle BOTH (proxy: witness in Payload1; boxable: witness in the dedicated witness
/// word, Payload1 zero). Each consumer sums <c>markerId()</c> across ALL values so a wrong stride
/// surfaces as a crash or wrong sum rather than a lucky single-value hit.
/// </summary>
public class ClassBoundExistentialDictValueTests : TestBase
{
    public ClassBoundExistentialDictValueTests(TestResults results) : base(results) { }

    #region PARAM direction (C# IDictionary<string, IMarker> → Swift func)

    /// <summary>
    /// PARAM, proxy-layout values: Swift-vended conformers (<c>MarkerVendor.Make</c>) are proxies whose
    /// existential carries the witness in Payload1. Summing across all three proves the 16-byte value
    /// stride — a 40-byte fill would have Swift read the 2nd/3rd value at the wrong offset.
    /// </summary>
    public void TestSumMarkerIdsByKeyParamProxyValues()
    {
        using var vendor = new MarkerVendor();
        var markers = new Dictionary<string, IMarker>
        {
            ["a"] = vendor.Make(1),
            ["b"] = vendor.Make(2),
            ["c"] = vendor.Make(3),
        };

        var sum = (int)TestLibFunctions.SumMarkerIdsByKey(markers);

        AssertEqual(6, sum, "SumMarkerIdsByKey over proxy-layout class-bound values (1+2+3)");
        TestLogger.Info($"PARAM proxy values: SumMarkerIdsByKey = {sum}");
    }

    /// <summary>
    /// PARAM, boxable-layout values: <c>new MarkerImpl(...)</c> is a concrete Swift class passed by
    /// value, so the existential carries the witness in the dedicated witness word with Payload1 zero.
    /// The layout-agnostic narrowing must read the witness from that word; the naive "Payload1"
    /// narrowing would hand Swift a null witness and crash on the first dispatch.
    /// </summary>
    public void TestSumMarkerIdsByKeyParamBoxableValues()
    {
        var markers = new Dictionary<string, IMarker>
        {
            ["a"] = new MarkerImpl((nint)10),
            ["b"] = new MarkerImpl((nint)20),
            ["c"] = new MarkerImpl((nint)30),
        };

        var sum = (int)TestLibFunctions.SumMarkerIdsByKey(markers);

        AssertEqual(60, sum, "SumMarkerIdsByKey over boxable-layout class-bound values (10+20+30)");
        TestLogger.Info($"PARAM boxable values: SumMarkerIdsByKey = {sum}");
    }

    /// <summary>
    /// PARAM, mixed layouts in one dictionary: proxy and boxable values interleaved. Proves the
    /// narrowing keys off each value's own witness slot, not a per-dictionary assumption.
    /// </summary>
    public void TestSumMarkerIdsByKeyParamMixedLayouts()
    {
        using var vendor = new MarkerVendor();
        var markers = new Dictionary<string, IMarker>
        {
            ["a"] = new MarkerImpl((nint)100),   // boxable layout
            ["b"] = vendor.Make(7),              // proxy layout
            ["c"] = new MarkerImpl((nint)200),   // boxable layout
        };

        var sum = (int)TestLibFunctions.SumMarkerIdsByKey(markers);

        AssertEqual(307, sum, "SumMarkerIdsByKey over mixed proxy/boxable values (100+7+200)");
        TestLogger.Info($"PARAM mixed layouts: SumMarkerIdsByKey = {sum}");
    }

    /// <summary>
    /// PARAM, count-only: proves the dictionary header marshals even when no value is read — the
    /// <c>count</c>-succeeds / value-read-crashes asymmetry that masked the stride bug behind a passing
    /// header read.
    /// </summary>
    public void TestCountMarkerMapParam()
    {
        using var vendor = new MarkerVendor();
        var markers = new Dictionary<string, IMarker>
        {
            ["a"] = vendor.Make(1),
            ["b"] = new MarkerImpl((nint)2),
            ["c"] = vendor.Make(3),
            ["d"] = new MarkerImpl((nint)4),
        };

        var count = (int)TestLibFunctions.CountMarkerMap(markers);

        AssertEqual(4, count, "CountMarkerMap header read");
        TestLogger.Info($"PARAM count-only: CountMarkerMap = {count}");
    }

    /// <summary>
    /// PARAM ownership balance: passing a class-bound marker through a Swift dictionary VALUE must NOT
    /// steal the source's +1. <c>FromDictionary</c> fills each value slot through the owned
    /// <c>CreateOwnedClassCarrier</c> (a fresh +1 on word0), and the dictionary's class-existential
    /// value-witness table releases word0 on destroy, so the round-trip balances. Pre-fix the 40-byte
    /// carrier wrote the value at the wrong stride/ownership and the destroy stole the source's +1 —
    /// deiniting a live object while C# still held the proxy/wrapper. Made deterministic via the shared
    /// lifetime counters: after the round-trip the sources MUST still be live; releasing the C# owners
    /// then drives each Swift object to deinit exactly once (no leak, no double-free).
    /// </summary>
    public void TestSumMarkerIdsByKeyParamDoesNotStealValueOwnership()
    {
        // The allocation counter is process-global. Drain pending finalizers from earlier tests BEFORE
        // resetting it, else their deferred Swift deinits land after Reset() and skew the live count.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        LifetimeTracker.Reset();
        using var vendor = new MarkerVendor();

        IMarker proxyMarker = vendor.Make(1);             // Swift-vended proxy (proxy layout)
        IMarker boxableMarker = new MarkerImpl((nint)2);  // boxable conformer wrapper (boxable layout)
        LifetimeTracker.LogStats("after creating proxy + boxable marker");
        LifetimeTracker.AssertLiveCount(2, "after creating proxy + boxable marker");

        var sum = (int)TestLibFunctions.SumMarkerIdsByKey(new Dictionary<string, IMarker>
        {
            ["p"] = proxyMarker,
            ["b"] = boxableMarker,
        });
        AssertEqual(3, sum, "SumMarkerIdsByKey(1+2)");

        // The internal SwiftDictionary is disposed synchronously inside SumMarkerIdsByKey. If the fill
        // under-retained, the dictionary destroy already deinit'd both source objects here.
        GC.KeepAlive(proxyMarker);
        GC.KeepAlive(boxableMarker);
        LifetimeTracker.LogStats("after SumMarkerIdsByKey round-trip");
        LifetimeTracker.AssertLiveCount(2, "after SumMarkerIdsByKey round-trip — sources must survive");

        (proxyMarker as IDisposable)?.Dispose();
        (boxableMarker as IDisposable)?.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        LifetimeTracker.LogStats("after disposing both markers");
        LifetimeTracker.AssertLiveCount(0, "after disposing both markers — no leak, no double-free");
        TestLogger.Info("PARAM ownership balance: source markers survived the dict round-trip and freed exactly once");
    }

    #endregion

    #region WRITE direction (C#-implemented getter → Swift reads it back)

    /// <summary>
    /// A pure C# implementation of <see cref="IMarkerMapProvider"/> whose <c>MarkerMap</c> getter
    /// returns a caller-supplied dictionary. Swift reaches it through the generated proxy/receiver, so
    /// the receiver getter must build <c>SwiftDictionary&lt;_, ClassExistentialContainer1&gt;</c> with
    /// each value narrowed to the 16-byte carrier.
    /// </summary>
    private sealed class CSharpMarkerMapProvider : IMarkerMapProvider
    {
        private readonly IReadOnlyDictionary<string, IMarker> _map;
        public CSharpMarkerMapProvider(IReadOnlyDictionary<string, IMarker> map) => _map = map;
        public IReadOnlyDictionary<string, IMarker> MarkerMap => _map;
    }

    /// <summary>
    /// WRITE, proxy-layout values: the C# getter returns Swift-vended proxies; Swift's
    /// <c>consumeMarkerMapProvider</c> reads the returned dictionary's values and sums the ids.
    /// </summary>
    public void TestConsumeMarkerMapProviderWriteProxyValues()
    {
        using var vendor = new MarkerVendor();
        var provider = new CSharpMarkerMapProvider(new Dictionary<string, IMarker>
        {
            ["a"] = vendor.Make(4),
            ["b"] = vendor.Make(5),
            ["c"] = vendor.Make(6),
        });

        var sum = (int)TestLibFunctions.ConsumeMarkerMapProvider(provider);

        AssertEqual(15, sum, "ConsumeMarkerMapProvider over C#-returned proxy values (4+5+6)");
        TestLogger.Info($"WRITE proxy values: ConsumeMarkerMapProvider = {sum}");
    }

    /// <summary>
    /// WRITE, boxable-layout values: the C# getter returns <c>new MarkerImpl(...)</c> by value, so the
    /// receiver getter narrows each from the boxable (witness-word) layout. The naive Payload1 narrowing
    /// would crash here on the Swift side's first dispatch.
    /// </summary>
    public void TestConsumeMarkerMapProviderWriteBoxableValues()
    {
        var provider = new CSharpMarkerMapProvider(new Dictionary<string, IMarker>
        {
            ["a"] = new MarkerImpl((nint)11),
            ["b"] = new MarkerImpl((nint)12),
            ["c"] = new MarkerImpl((nint)13),
        });

        var sum = (int)TestLibFunctions.ConsumeMarkerMapProvider(provider);

        AssertEqual(36, sum, "ConsumeMarkerMapProvider over C#-returned boxable values (11+12+13)");
        TestLogger.Info($"WRITE boxable values: ConsumeMarkerMapProvider = {sum}");
    }

    #endregion
}
