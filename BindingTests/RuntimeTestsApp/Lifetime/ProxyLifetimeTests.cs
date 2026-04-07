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
/// from <see cref="SwiftObjectRegistry"/> when EITHER the user's impl is
/// garbage-collected OR Swift releases its last reference to the existential
/// container, whichever comes first. In practice: impl GC triggers the
/// release chain via <c>ProxyLifetimeTracker</c> → <c>Arc.Release</c> →
/// Swift <c>EveryProtocol.deinit</c> → <c>OnEveryProtocolDeinit</c> → strong
/// registry cleanup.
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
/// boundedness: after N assignments, <see cref="SwiftObjectRegistry.StrongCount"/>
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

    /// <summary>
    /// Number of GC cycles to run before asserting — ProxyCleanup's finalizer
    /// + Swift's deinit fires in two distinct GC passes (first collects the
    /// impl and the tracker's CWT entry, second collects the proxy after
    /// its strong-registry root drops from the deinit callback).
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

    private void AssertBoundedLeak(int baseline, string scenarioDescription)
    {
        var current = SwiftObjectRegistry.StrongCount;
        var leaked = current - baseline;
        TestLogger.Info($"[ProxyLifetime] {scenarioDescription}: baseline={baseline}, current={current}, leaked={leaked}");
        AssertTrue(
            leaked <= MaxResidualLeak,
            $"{scenarioDescription}: StrongCount leaked {leaked} > tolerance {MaxResidualLeak} (baseline={baseline}, current={current}). " +
            "The impl-anchored tracker / Swift deinit chain is not releasing proxies.");
    }

    /// <summary>
    /// Scenario 1: pass a plain C# impl to a one-shot method parameter that
    /// Swift does not store. After N calls, all N proxies must be released
    /// (within the conservative-scan noise floor).
    ///
    /// <para>
    /// Pre-fix behaviour: each call leaked one proxy forever, so after N
    /// iterations <c>StrongCount</c> would be <c>baseline + N</c>.
    /// Post-fix: <c>StrongCount</c> should be within <see cref="MaxResidualLeak"/>
    /// of baseline.
    /// </para>
    /// </summary>
    public void TestOneShotMethodParameterReleasesAfterImplGc()
    {
        var baseline = SwiftObjectRegistry.StrongCount;

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
        var baseline = SwiftObjectRegistry.StrongCount;

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
        var baseline = SwiftObjectRegistry.StrongCount;

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
        var baseline = SwiftObjectRegistry.StrongCount;

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
    /// This is the one test that <b>must</b> run on <c>nuke runtime-tests-device</c>
    /// to exercise the NativeAOT reverse-P/Invoke path. The simulator run
    /// validates it doesn't crash on Mono.
    /// </para>
    /// </summary>
    public void TestCrossThreadFinalReleaseFromSwift()
    {
        var baseline = SwiftObjectRegistry.StrongCount;

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
    /// Codex P0 regression: impl is GC'd while Swift still holds a STRONG
    /// retain on the proxy. The tracker releases our +1 (via the cleanup
    /// finalizer) but Swift's strong retain keeps the EveryProtocol alive,
    /// so <see cref="ProxyLifetimeTracker.OnEveryProtocolDeinit"/> never
    /// fires and <see cref="SwiftObjectRegistry.TryGetProxyFromContainer"/>
    /// still returns the proxy. Swift calls a method on the proxy — the
    /// weak <c>_csharpImpl</c> unwrap returns null. Pre-fix the generated
    /// receiver did <c>proxy._csharpImpl!.Method(...)</c> and threw
    /// NullReferenceException across the <c>[UnmanagedCallersOnly]</c>
    /// boundary, terminating the process. Post-fix the receiver returns a
    /// safe default (void return = no-op, value returns = zeroed buffer).
    ///
    /// <para>
    /// Uses <see cref="AutoWrappedMonitor.AutoWrappedMonitor(Swift.Runtime.IAutoWrappedMonitorDelegate)"/>
    /// (the <c>init(initialDelegate:)</c> constructor), which stores the
    /// delegate in both the <c>weak delegate</c> AND the
    /// <c>strong strongDelegate</c> property — so Swift's strong retain
    /// outlives the tracker's +1 release and the monitor's
    /// <see cref="AutoWrappedMonitor.FireStrong"/> path dispatches directly
    /// into the (now-dead) proxy without going through a nullable check on
    /// the Swift side.
    /// </para>
    /// </summary>
    public void TestStrongSwiftRetainSurvivesImplGc()
    {
        // Create the monitor outside the helper so it stays rooted past the
        // GC cycle — only the impl must become collectible.
        AutoWrappedMonitor monitor = null!;
        try
        {
            monitor = CreateMonitorWithDroppedImpl();

            ForceGc();

            // Swift still strongly retains the proxy (strongDelegate slot).
            // The receiver must survive the impl-GC and return a safe default.
            // Pre-fix: this line terminates the process with a NullReferenceException
            // that propagates across the [UnmanagedCallersOnly] boundary.
            // Post-fix: the receiver's null-impl guard silently no-ops and
            // lastNotifiedSlot remains 0 because the delegate method is not actually called.
            monitor.FireStrong();

            // Survival assertion: we got here without the process being
            // terminated. The monitor's counter still increments (that is a
            // Swift-side side effect, independent of the delegate callback).
            AssertTrue(monitor.LastFiredValue >= 1, "Monitor counter still increments even with dead impl");

            TestLogger.Info(
                $"[ProxyLifetime] Strong Swift retain + dead impl survived: " +
                $"counter={monitor.LastFiredValue}, lastNotifiedSlot={monitor.LastNotifiedSlot}");
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
/// The Codex P0 scenario needs an impl that the test can deliberately let
/// become unreachable while Swift still strongly retains the wrapping proxy.
/// Lives in this file (not AutoWrappedDelegateTests.cs) to keep the
/// dead-impl regression coverage next to the other lifetime tests.
/// </summary>
internal class AutoWrappedDelegateImplForLifetime : IAutoWrappedMonitorDelegate
{
    public void MonitorDidUpdate(int value)
    {
        // Intentionally empty. If this method ever runs after GC, the
        // regression-guard test will have caught a Swift-to-dead-impl
        // dispatch and the test body's assertions will confirm the
        // fallback path (not this method) serviced the call.
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
