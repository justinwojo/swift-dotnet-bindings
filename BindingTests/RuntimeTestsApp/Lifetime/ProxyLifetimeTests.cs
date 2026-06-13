// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Runtime tests for the impl-anchored auto-wrap proxy lifetime model.
///
/// <para>
/// These tests verify that auto-wrapped protocol proxies are unregistered
/// from <see cref="SwiftObjectRegistry"/> once the proxy is collected and its
/// construction +1 (R0) is released, driving Swift's last reference to zero.
/// Under Design B2 the proxy is registered WEAKLY, so once the consumer drops
/// it the chain is: proxy collected → finalizer → <c>ProxyLifetimeTracker</c>
/// <c>.ReleaseHandle</c> → <c>SwiftReleaseTrampoline.Release</c> → Swift
/// <c>EveryProtocol.deinit</c> → <c>OnEveryProtocolDeinit</c> → weak-registry
/// <c>Unregister</c> + impl-root <c>GCHandle</c> free. (When Swift strongly
/// retains the existential instead, that retain — not the proxy — keeps the
/// chain alive; see <c>TestStrongSwiftRetainSurvivesImplGc</c>.)
/// </para>
///
/// <para>
/// <b>Why these tests use a "bulk and tolerate noise" assertion style:</b>
/// Mono's iOS-simulator GC uses conservative stack scanning, which
/// reliably keeps 1-2 most-recently-used objects alive across forced GC
/// cycles even when the user believes they should be collectible (the JIT
/// hasn't yet reused the stack slot that held the pointer). The original
/// auto-wrap leak was <b>unbounded</b> — every assignment leaked one proxy
/// forever. The new design bounds leaks to live impls. These tests prove
/// boundedness: after N assignments, the weak <see cref="SwiftObjectRegistry.Count"/>
/// returns to within a small constant of baseline — NOT the exact baseline.
/// With the original (broken) code, N iterations leak N proxies; with the
/// fix, N iterations leak at most ~2 (the conservative-scan noise floor).
/// The NativeAOT device runs use a precise GC and should hit the exact
/// baseline; see <c>TestCrossThreadFinalReleaseFromSwift</c> for that path.
/// </para>
///
/// <para>
/// Failure modes these tests catch:
/// </para>
/// <list type="bullet">
/// <item>Tracker never tracks (proxies leak until process exit — the original bug).</item>
/// <item>Tracker races with Swift deinit (double release or missed release).</item>
/// <item>Weak cache holds a strong ref (proxy can never be collected).</item>
/// <item>Cross-thread reverse P/Invoke from Swift deinit crashes on NativeAOT.</item>
/// </list>
/// <para>
/// Deliberately does NOT modify the existing
/// <c>AutoWrappedDelegateTests</c> — those cover round-trip semantics and
/// are the regression net for the auto-wrap fix itself. This file is the
/// regression net for the lifetime fix layered on top.
/// </para>
/// </summary>
public class ProxyLifetimeTests : TestBase
{
    public ProxyLifetimeTests(TestResults results) : base(results) { }

    // ---- 0.10.0 Layer C lifetime harness (populated by Bundles 1 and 3) ------
    //
    // Long-running / GC-pressure assertions for this class are gated by
    // `TestRunFlags.Lifetime` — set via `nuke binding-tests --lifetime`. Off by
    // default for inner-loop simulator runs; enabled unconditionally on the
    // integration serial gate. The 0.10.0 SafeHandle-refcount and
    // closure-lifetime bundles will populate methods here that loop a repro
    // pattern ~10k times with `GC.Collect()` between runs and assert
    // deterministic Swift alloc/dealloc counters return to baseline,
    // `CFGetRetainCount` returns to baseline for bridged ObjC objects, RSS
    // stays under a budget, and no finalizer-thread exceptions are logged.
    // Layer C — lifetime harness: exercises proxy cleanup through GC finalization.

    /// <summary>
    /// Number of GC cycles to run before asserting. The cleanup spans more than
    /// one pass: a pass collects the weakly-registered proxy and queues its
    /// finalizer; the finalizer releases R0 → Swift <c>EveryProtocol.deinit</c> →
    /// <c>OnEveryProtocolDeinit</c> frees the impl-root <c>GCHandle</c> and
    /// unregisters the entry; a later pass then collects the now-unrooted impl.
    /// Several cycles give that chain room to drain on the conservative Mono GC.
    /// </summary>
    private const int GcCycles = 6;

    /// <summary>
    /// Bulk iteration count. Pre-fix, N iterations would leak N proxies
    /// (unbounded). Post-fix, the tracker/deinit chain cleans them up.
    /// We assert a permissive upper bound so conservative-stack-scan noise
    /// (which typically pins 1-2 most-recently-returned locals) doesn't
    /// cause false negatives.
    /// </summary>
    private const int BulkIterations = 50;

    /// <summary>
    /// Upper bound on "leaked" proxies per bulk iteration count. Accounts
    /// for Mono iOS sim's conservative stack scanning pinning a small
    /// constant number of recently-used objects. This tolerance must stay
    /// MUCH smaller than <see cref="BulkIterations"/> to catch the original
    /// leak (which was N, not a small constant).
    /// </summary>
    private const int MaxResidualLeak = 5;

    /// <summary>
    /// Force a GC cycle. Mono's iOS simulator GC uses conservative stack
    /// scanning — it can pin recently-used objects through stale stack slots
    /// that the JIT hasn't rewritten yet. We mitigate by running GC on a
    /// worker thread (the main thread is blocked on Join with a minimal
    /// live-local footprint) and by scrubbing the worker's own stack with
    /// a throwaway allocation loop before collecting.
    /// </summary>
    private static void ForceGc()
    {
        var worker = new System.Threading.Thread(ForceGcWorker) { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ForceGcWorker()
    {
        // Scrub the worker thread's own stack with a throwaway allocation loop.
        var scratch = new object[256];
        for (int i = 0; i < scratch.Length; i++)
            scratch[i] = new object();
        GC.KeepAlive(scratch);

        for (int i = 0; i < GcCycles; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>
    /// Asserts the per-iteration leak is within the conservative-scan noise floor,
    /// measured against the WEAK <see cref="SwiftObjectRegistry.Count"/>.
    ///
    /// <para>
    /// Design B2 registers every auto-wrapped C#-impl proxy <b>weakly</b>
    /// (<c>SwiftObjectRegistry.Register</c>, never <c>RegisterStrong</c>), so
    /// <see cref="SwiftObjectRegistry.StrongCount"/> is structurally 0 for these
    /// objects and is blind to their lifecycle — it would stay at baseline even
    /// under a catastrophic R0 leak. The weak <c>Count</c> is the faithful signal:
    /// the registry entry is added in the proxy ctor (alongside the tracker's
    /// impl-root <c>GCHandle</c>) and removed in the one
    /// <c>OnEveryProtocolDeinitCore</c> callback (which also frees that root), so
    /// <c>Count</c> moves in lockstep with the tracker root and returns to baseline
    /// iff every R0→deinit→Unregister chain completed. A leaked iteration leaves a
    /// lingering entry, so <c>Count</c> grows by the leak count.
    /// </para>
    /// </summary>
    private void AssertBoundedLeak(int baseline, string scenarioDescription)
    {
        var current = SwiftObjectRegistry.Count;
        var leaked = current - baseline;
        TestLogger.Info($"[ProxyLifetime] {scenarioDescription}: baseline={baseline}, current={current}, leaked={leaked}");
        AssertTrue(
            leaked <= MaxResidualLeak,
            $"{scenarioDescription}: weak-registry Count leaked {leaked} > tolerance {MaxResidualLeak} (baseline={baseline}, current={current}). " +
            "The impl-anchored tracker / Swift deinit chain is not releasing proxies.");
    }

    /// <summary>
    /// Demonstrates that the leak signal used by <see cref="AssertBoundedLeak"/>
    /// actually has teeth — guarding against a repeat of the superseded
    /// <c>StrongCount</c> signal, which was structurally 0 for these weakly
    /// registered proxies and so could never observe a leak. The weak
    /// <see cref="SwiftObjectRegistry.Count"/> must rise by exactly one per LIVE
    /// auto-wrapped proxy (proving a real per-iteration leak WOULD surface as a
    /// growing Count), then return to baseline once every proxy is cleaned up.
    /// Without this, a <c>leaked=0</c> reading in the bulk scenarios is ambiguous:
    /// it cannot distinguish "cleanup works" from "the signal is dead".
    /// </summary>
    public void TestWeakRegistryCountTracksLiveProxies()
    {
        ForceGc();
        var baseline = SwiftObjectRegistry.Count;

        const int liveCount = 8;
        var harnesses = new ProxyLifetimeHarness?[liveCount];
        for (int i = 0; i < liveCount; i++)
        {
            var harness = new ProxyLifetimeHarness();
            // The strong Swift `receiver` slot retains the auto-wrapped proxy's
            // EveryProtocol, so its weak-registry entry persists for as long as
            // `harness` is reachable — the same shape a leaked iteration takes.
            harness.Receiver = new PingReceiverImpl();
            harnesses[i] = harness;
        }

        // Teeth: each live proxy is exactly one weak-registry entry. If this
        // stayed at baseline, the signal would be blind to leaks (the old bug).
        AssertEqual(baseline + liveCount, SwiftObjectRegistry.Count,
            "Weak Count must rise by one per live auto-wrapped proxy — else the leak signal is hollow");

        // Drop every strong reference; the R0 -> deinit -> Unregister chain must run.
        for (int i = 0; i < liveCount; i++)
            harnesses[i] = null;
        ForceGc();

        var after = SwiftObjectRegistry.Count;
        AssertTrue(after - baseline <= MaxResidualLeak,
            $"Weak Count must return to baseline after the proxies are cleaned up (baseline={baseline}, after={after})");
    }

    /// <summary>
    /// Scenario 1: pass a plain C# impl to a one-shot method parameter that
    /// Swift does not store. After N calls, all N proxies must be released
    /// (within the conservative-scan noise floor).
    ///
    /// <para>
    /// Pre-fix behaviour: each call leaked one proxy forever, so after N
    /// iterations the weak-registry <c>Count</c> would be <c>baseline + N</c>.
    /// Post-fix: <c>Count</c> should be within <see cref="MaxResidualLeak"/>
    /// of baseline.
    /// </para>
    /// </summary>
    public void TestOneShotMethodParameterReleasesAfterImplGc()
    {
        var baseline = SwiftObjectRegistry.Count;

        for (int i = 0; i < BulkIterations; i++)
            FireOneShot();

        ForceGc();

        AssertBoundedLeak(baseline, $"{BulkIterations}x one-shot method parameter");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void FireOneShot()
    {
        var harness = new ProxyLifetimeHarness();
        var impl = new PingReceiverImpl();
        harness.PingOnce(impl, value: 1);
        GC.KeepAlive(harness);
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Scenario 2: assign a plain C# impl to Swift's strong <c>receiver</c>
    /// slot, then explicitly clear the slot and drop every C# reference.
    /// Repeated N times; the tracker + deinit chain must release almost all
    /// of them.
    /// </summary>
    public void TestDelegateSetThenClearReleasesProxy()
    {
        var baseline = SwiftObjectRegistry.Count;

        for (int i = 0; i < BulkIterations; i++)
            AssignAndClear();

        ForceGc();

        AssertBoundedLeak(baseline, $"{BulkIterations}x receiver set+clear");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssignAndClear()
    {
        var harness = new ProxyLifetimeHarness();
        var impl = new PingReceiverImpl();
        harness.Receiver = impl;
        harness.Receiver = null;
        GC.KeepAlive(harness);
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Scenario 3: overwrite <c>receiver</c> with a second impl, then clear.
    /// Both proxies must be unregistered after GC. Repeated N times.
    /// </summary>
    public void TestOverwriteThenClearReleasesBothProxies()
    {
        var baseline = SwiftObjectRegistry.Count;

        for (int i = 0; i < BulkIterations; i++)
            AssignOverwriteClear();

        ForceGc();

        AssertBoundedLeak(baseline, $"{BulkIterations}x receiver A→B→nil");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssignOverwriteClear()
    {
        var harness = new ProxyLifetimeHarness();
        var a = new PingReceiverImpl();
        var b = new PingReceiverImpl();
        harness.Receiver = a;
        harness.Receiver = b;
        harness.Receiver = null;
        GC.KeepAlive(harness);
        GC.KeepAlive(a);
        GC.KeepAlive(b);
    }

    /// <summary>
    /// Scenario 4: exercise the weak-cache rebuild path. Assign impl_i to a
    /// fresh harness; drop both; GC; repeat. Each iteration re-enters the
    /// <c>s_autoWrapCache</c> with a fresh impl instance, which exercises the
    /// stale-entry rebuild path. If the cache held proxies strongly (the
    /// pre-fix state), every iteration would leak a proxy.
    /// </summary>
    public void TestCacheReuseAfterDeinitRebuilds()
    {
        var baseline = SwiftObjectRegistry.Count;

        for (int i = 0; i < BulkIterations; i++)
            CacheRebuildRound();

        ForceGc();

        AssertBoundedLeak(baseline, $"{BulkIterations}x cache miss rebuild");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void CacheRebuildRound()
    {
        var harness = new ProxyLifetimeHarness();
        var impl = new PingReceiverImpl();
        harness.Receiver = impl;
        harness.Receiver = null;
        GC.KeepAlive(harness);
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Scenario 5: trigger the final Swift release of the receiver from a
    /// background dispatch queue. The EveryProtocol <c>deinit</c> then fires
    /// on that queue's worker thread and reverse-P/Invokes into C# on a
    /// non-main thread. Mono and NativeAOT have historically had different
    /// tolerances for this path — the test must pass on both.
    ///
    /// <para>
    /// This is the one test that <b>must</b> run on <c>nuke binding-tests --device</c>
    /// to exercise the NativeAOT reverse-P/Invoke path. The simulator run
    /// validates it doesn't crash on Mono.
    /// </para>
    /// </summary>
    public void TestCrossThreadFinalReleaseFromSwift()
    {
        var baseline = SwiftObjectRegistry.Count;

        // Bulk-iterate so conservative-scan noise doesn't mask a
        // per-iteration leak.
        for (int i = 0; i < BulkIterations; i++)
            CrossThreadRound();

        ForceGc();

        AssertBoundedLeak(baseline, $"{BulkIterations}x cross-thread Swift deinit");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void CrossThreadRound()
    {
        var harness = new ProxyLifetimeHarness();
        var impl = new PingReceiverImpl();
        harness.Receiver = impl;
        // Clear on a background queue so the final Swift release — and
        // thus the EveryProtocol.deinit callback chain — runs off the
        // main thread.
        harness.ClearReceiverOnBackgroundQueue();
        GC.KeepAlive(harness);
        GC.KeepAlive(impl);
    }

    /// <summary>
    /// Defect G / Design B2 invariant: the C# impl is rooted by <b>Swift
    /// liveness</b>, so it survives GC for exactly as long as Swift holds the
    /// EveryProtocol — and reverse dispatch therefore resolves the <i>live</i>
    /// impl rather than fabricating a value.
    ///
    /// <para>
    /// The test drops every managed reference to the impl while Swift still
    /// strongly retains the proxy (the <c>strongDelegate</c> slot), then forces
    /// GC. Under B2 the proxy is registered only <i>weakly</i> in
    /// <see cref="SwiftObjectRegistry"/> (so it can be collected), but
    /// <see cref="ProxyLifetimeTracker"/> holds a <b>strong GCHandle</b> on the
    /// impl keyed by the EveryProtocol handle, freed only when Swift's deinit
    /// fires. Because Swift's <c>strongDelegate</c> keeps the EveryProtocol
    /// alive, that root is still allocated after GC, so the impl is NOT
    /// collected. <see cref="AutoWrappedMonitor.FireStrong"/> then dispatches
    /// through the witness table, the receiver resolves the live impl via
    /// <see cref="ProxyLifetimeTracker.ResolveImpl{T}"/>, and the C# method
    /// actually runs.
    /// </para>
    ///
    /// <para>
    /// Pre-B2 (the inverted-lifetime defect) the impl was held only weakly, so
    /// this GC collected it and the receiver either crashed across the
    /// <c>[UnmanagedCallersOnly]</c> boundary (NullReferenceException) or
    /// silently fabricated a default — neither of which runs the real callback.
    /// The assertion that the impl's <c>MonitorDidUpdate</c> actually executed
    /// (via the static observed-call counter) is what distinguishes B2's
    /// "rooted + resolved" from the old "collected + fabricated".
    /// </para>
    ///
    /// <para>
    /// Uses <see cref="AutoWrappedMonitor.AutoWrappedMonitor(Swift.Runtime.IAutoWrappedMonitorDelegate)"/>
    /// (the <c>init(initialDelegate:)</c> constructor), which stores the
    /// delegate in both the <c>weak delegate</c> AND the
    /// <c>strong strongDelegate</c> property — so Swift's strong retain anchors
    /// the EveryProtocol (and thus the impl GCHandle root) past the GC cycle.
    /// </para>
    /// </summary>
    public void TestStrongSwiftRetainSurvivesImplGc()
    {
        // Reset the cross-frame observation counter: the impl is intentionally
        // unreachable from this frame (so it CAN be collected if B2 is broken),
        // so we observe its callback through a static rather than a live ref.
        AutoWrappedDelegateImplForLifetime.ResetObservation();

        // Create the monitor outside the helper so it stays rooted past the
        // GC cycle — only the impl is dropped.
        AutoWrappedMonitor monitor = null!;
        try
        {
            monitor = CreateMonitorWithDroppedImpl();

            ForceGc();

            // Swift still strongly retains the proxy (strongDelegate slot), so
            // the impl GCHandle root is still allocated and the impl survived GC.
            // Pre-B2 this line crashed (collected impl → NRE across the
            // [UnmanagedCallersOnly] boundary) or no-opped a fabricated default.
            monitor.FireStrong();

            // Swift sets lastNotifiedSlot = 2 immediately before dispatching the
            // strong slot — necessary but NOT sufficient (it was 2 pre-B2 too).
            AssertEqual(2, monitor.LastNotifiedSlot, "FireStrong dispatched the strong slot");

            // The decisive B2 assertion: the LIVE impl actually serviced the
            // reverse call. ResolveImpl returned the Swift-rooted impl rather
            // than the loud backstop firing or a value being fabricated.
            AssertTrue(
                AutoWrappedDelegateImplForLifetime.ObservedCallCount >= 1,
                "B2: impl rooted by Swift liveness survived GC and actually serviced the reverse call " +
                $"(observed {AutoWrappedDelegateImplForLifetime.ObservedCallCount} calls)");
            AssertEqual(
                monitor.LastFiredValue, AutoWrappedDelegateImplForLifetime.LastObservedValue,
                "the live impl received the same counter value Swift dispatched");

            TestLogger.Info(
                $"[ProxyLifetime] Strong Swift retain kept impl alive for dispatch: " +
                $"counter={monitor.LastFiredValue}, lastNotifiedSlot={monitor.LastNotifiedSlot}, " +
                $"observedCalls={AutoWrappedDelegateImplForLifetime.ObservedCallCount}");
        }
        finally
        {
            GC.KeepAlive(monitor);
        }
    }

    // Helper in its own frame so the `impl` local is guaranteed unreachable
    // after return (outside any JIT stack slot the main test frame might pin).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static AutoWrappedMonitor CreateMonitorWithDroppedImpl()
    {
        var impl = new AutoWrappedDelegateImplForLifetime();
        // init(initialDelegate:) stores BOTH weak and strong — so the proxy is
        // anchored by Swift's `strongDelegate` property even after the tracker
        // finalizer releases our +1.
        var monitor = new AutoWrappedMonitor(initialDelegate: impl);
        GC.KeepAlive(impl);
        // impl is not returned and not re-anchored. Once this frame returns,
        // the only reference Swift/tracker/proxy hold to impl is via
        // WeakReference — next GC makes impl eligible for collection.
        return monitor;
    }

    /// <summary>
    /// Scenario 6 (smoke): exercise the process-exit guard synchronously so
    /// a crash in the finalizer path would surface here. A real shutdown
    /// test is impossible from a running process — manual inspection of
    /// SwiftExitGuard + ProxyCleanup's finalizer is the actual verification.
    /// </summary>
    public void TestProcessExitGuardShortCircuits()
    {
        var harness = new ProxyLifetimeHarness();
        var impl = new PingReceiverImpl();
        harness.Receiver = impl;

        try
        {
            SwiftExitGuardHelper.MarkExiting();
            // Drop everything. If the finalizer path crashes during exit we
            // would fail here (the test runner would abort). The guard
            // short-circuits Arc.Release, so the pointer leaks harmlessly.
            harness.Receiver = null;
            impl = null;
            ForceGc();
            TestLogger.Info("Process-exit guard short-circuited without crashing");
        }
        finally
        {
            SwiftExitGuardHelper.ClearExiting();
            GC.KeepAlive(harness);
        }
    }
}

/// <summary>
/// Plain C# implementation of <see cref="IProxyLifetimeReceiver"/>. Deliberately
/// does NOT implement <c>ISwiftExistentialConvertible</c> or
/// <c>IExistentialBoxable</c> — the point is to route through the auto-wrap
/// path (<c>ExistentialContainerFactory.GetOrCreate</c> → generated proxy
/// fallback) so the impl-anchored tracker path is exercised.
/// </summary>
internal class PingReceiverImpl : IProxyLifetimeReceiver
{
    public int LastValue { get; private set; }
    public int PingCount { get; private set; }

    public void Ping(int value)
    {
        PingCount++;
        LastValue = value;
    }
}

/// <summary>
/// Plain C# implementation used by
/// <see cref="ProxyLifetimeTests.TestStrongSwiftRetainSurvivesImplGc"/>.
/// This scenario needs an impl that the test can deliberately let become
/// unreachable while Swift still strongly retains the wrapping proxy.
/// Lives in this file (not AutoWrappedDelegateTests.cs) to keep the
/// dead-impl regression coverage next to the other lifetime tests.
/// </summary>
internal class AutoWrappedDelegateImplForLifetime : IAutoWrappedMonitorDelegate
{
    // Cross-frame observation: the lifetime test deliberately holds NO managed
    // reference to the impl, so it observes whether the reverse call actually ran
    // through these statics rather than through a live instance reference.
    internal static int ObservedCallCount;
    internal static int LastObservedValue;

    internal static void ResetObservation()
    {
        ObservedCallCount = 0;
        LastObservedValue = 0;
    }

    public void MonitorDidUpdate(int value)
    {
        // Under B2 this MUST run after GC: the impl is rooted by Swift liveness,
        // so reverse dispatch resolves the live impl. Pre-B2 the impl was
        // collected and this never ran (the receiver crashed or fabricated a
        // default) — the static counter is how the test tells the two apart.
        ObservedCallCount++;
        LastObservedValue = value;
    }
}

/// <summary>
/// Thin wrapper around the internal <c>SwiftExitGuard</c> so the runtime test
/// project can toggle the process-exit flag for the shutdown smoke test.
/// <c>SwiftExitGuard</c> is internal to <c>Swift.Runtime</c> and
/// <c>SetProcessExitingForTest</c> is only visible via
/// <c>InternalsVisibleTo("Swift.Runtime.Tests")</c>; we replicate the same
/// pattern through reflection so the runtime tests project does not need an
/// additional InternalsVisibleTo entry.
/// </summary>
internal static class SwiftExitGuardHelper
{
    private static readonly System.Reflection.MethodInfo? s_setter =
        typeof(Swift.Runtime.SwiftFrameworkResolver).Assembly
            .GetType("Swift.Runtime.SwiftExitGuard")
            ?.GetMethod(
                "SetProcessExitingForTest",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

    public static void MarkExiting() => s_setter?.Invoke(null, new object[] { true });
    public static void ClearExiting() => s_setter?.Invoke(null, new object[] { false });
}
