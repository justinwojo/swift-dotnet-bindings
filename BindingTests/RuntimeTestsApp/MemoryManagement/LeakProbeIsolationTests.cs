// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Positive controls for the leak-probe harness itself. Before a churn,
/// <see cref="LifetimeTracker.RunIsolated(Action)"/> scrubs stacks and fills the free nursery with
/// decoys, and the drain in <see cref="LifetimeTracker.AssertNoLeaks(string)"/> scrubs stacks and
/// wakes parked pool workers. All of that only removes roots nothing stands behind. These tests
/// prove that a churned object which IS still referenced stays counted through every one of those
/// steps, so the harness cannot turn a genuine leak into a pass. They also check that the failure
/// readout names the survivor's owning handle from the shared handle ledger, without the probe
/// recording any observation of its own.
/// </summary>
public class LeakProbeIsolationTests : TestBase
{
    public LeakProbeIsolationTests(TestResults results) : base(results) { }

    private static TrackedObject? s_held;

    /// <summary>
    /// A static field holds one tracked object created inside an isolated churn that also abandons
    /// many others. The abandoned ones must all be reclaimed, and the held one must stay live
    /// until the field lets it go.
    /// </summary>
    public void TestIsolatedChurnKeepsAStaticallyHeldObjectCounted()
    {
        LifetimeTracker.Reset();
        LifetimeTracker.RunIsolated(() =>
        {
            AbandonTrackedObjects(200);
            s_held = TestLibFunctions.CreateTrackedObject(4242);
        });

        try
        {
            LifetimeTracker.AssertLiveCount(1, "statically held object survives the isolated churn");
            string readout = ExpectLeakFailure("statically held object");
            AssertTrue(readout.Contains("1 object(s) not deallocated"),
                $"the failure counts exactly the held object: {readout}");
            AssertTrue(readout.Contains("rootedHandles=1 "),
                $"the shared handle ledger names the held object's handle as rooted: {readout}");
        }
        finally
        {
            s_held?.Dispose();
            s_held = null;
        }
        LifetimeTracker.AssertNoLeaks("statically held object released");
    }

    /// <summary>
    /// The same control with the only reference in a strong GC handle allocated on the churn
    /// thread, which exits before the drain: a root no thread's stack or registers carry.
    /// </summary>
    public void TestIsolatedChurnKeepsAHandleHeldObjectCounted()
    {
        LifetimeTracker.Reset();
        GCHandle held = LifetimeTracker.RunIsolated(() =>
        {
            AbandonTrackedObjects(200);
            return GCHandle.Alloc(TestLibFunctions.CreateTrackedObject(4343), GCHandleType.Normal);
        });

        try
        {
            LifetimeTracker.AssertLiveCount(1, "handle-held object survives the isolated churn");
            string readout = ExpectLeakFailure("handle-held object");
            AssertTrue(readout.Contains("rootedHandles=1 "),
                $"the shared handle ledger names the held object's handle as rooted: {readout}");
        }
        finally
        {
            try
            {
                (held.Target as TrackedObject)?.Dispose();
            }
            finally
            {
                held.Free();
            }
        }
        LifetimeTracker.AssertNoLeaks("handle-held object released");
    }

    private const int StaleWordCount = 2048;
    private const int ChurnCount = 1000;

    /// <summary>
    /// The hazard the isolation exists for, staged on purpose. A live caller frame holds stale
    /// words naming nursery addresses whose objects have already been collected, exactly the
    /// state a test runner's frames can carry into a probe, and the churn then runs below that
    /// frame. On Mono a churn object allocated at one of those addresses is pinned by the word,
    /// so the probe would report a leak that no reference stands behind.
    /// <see cref="LifetimeTracker.RunIsolated(Action)"/> must leave every churned object
    /// collectable. The control repeats the staging with a plain joined thread and requires that
    /// it does pin churn objects there, so the main check cannot pass only because the staging
    /// stopped reproducing the hazard.
    /// </summary>
    public void TestIsolatedChurnIsNotPinnedByStaleWordsInALiveCallerFrame()
    {
        // The assertion runs inside the staging frame: stale words in a test runner's frames
        // outlive the probe's own assertion, and a pin only lasts while its word does.
        LifetimeTracker.Reset();
        WithStaleWordsInALiveFrame(() =>
        {
            LifetimeTracker.RunIsolated(() => AbandonTrackedObjects(ChurnCount));
            LifetimeTracker.AssertNoLeaks("isolated churn under stale words in a live caller frame");
        });

        // Conservative stack scanning, and so the hazard, is Mono's alone.
        if (!SwiftRuntimeInfo.IsMonoRuntime)
            return;

        LifetimeTracker.Reset();
        int pinned = 0;
        WithStaleWordsInALiveFrame(() =>
        {
            var plain = new Thread(() => AbandonTrackedObjects(ChurnCount));
            plain.Start();
            plain.Join();
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            GC.Collect();
            pinned = LifetimeTracker.GetStats().live;
        });
        ScrubBelowCaller();
        // Once the frame holding the stale words is gone, so are the pins.
        LifetimeTracker.AssertNoLeaks("control churn after its stale frame returned");
        TestLogger.Info($"control: a plain joined thread left {pinned} of {ChurnCount} churned objects pinned");
        AssertTrue(pinned > 0,
            "control: the staged stale words pin churn objects allocated by a plain joined thread");
    }

    // Runs the body below a live frame whose stack holds stale words naming freed nursery
    // addresses. A fresh thread allocates the objects, as the churn thread will, so they take
    // the nursery addresses a churn thread's first allocations take after a collection. Only
    // interior addresses leave that thread, so the collection that follows frees every object.
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void WithStaleWordsInALiveFrame(Action body)
    {
        nint* words = stackalloc nint[StaleWordCount];
        var addresses = new nint[StaleWordCount];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var seeder = new Thread(() => RecordAddressesOfDeadObjects(addresses));
        seeder.Start();
        seeder.Join();
        GC.Collect();
        for (int i = 0; i < StaleWordCount; i++)
            words[i] = addresses[i];
        body();
        // Read the words after the body, so the frame and its stack stay live until it returns.
        nint sum = 0;
        for (int i = 0; i < StaleWordCount; i++)
            sum += words[i];
        s_sink += (int)sum;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RecordAddressesOfDeadObjects(nint[] addresses)
    {
        for (int i = 0; i < addresses.Length; i++)
            addresses[i] = ConservativeRootProbe.AddressOf(new object[2]) + 16;
    }

    // Overwrites the stack the staging frame used, so the drain's frames, pushed over it next,
    // cannot pick up one of its stale words.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ScrubBelowCaller()
    {
        Span<nint> scratch = stackalloc nint[StaleWordCount * 2];
        for (int i = 0; i < scratch.Length; i++)
            scratch[i] = i + 1;
        nint sum = 0;
        for (int i = 0; i < scratch.Length; i++)
            sum += scratch[i];
        s_sink += (int)sum;
    }

    private static volatile int s_sink;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AbandonTrackedObjects(int count)
    {
        for (int i = 0; i < count; i++)
            _ = TestLibFunctions.CreateTrackedObject(i);
    }

    // Runs the leak assertion that must fail and returns its message. The assertion only reads
    // the counters, so the caller's later assertion sees the same window.
    private string ExpectLeakFailure(string what)
    {
        try
        {
            LifetimeTracker.AssertNoLeaks(what);
        }
        catch (AssertionException ex)
        {
            return ex.Message;
        }
        AssertTrue(false, $"AssertNoLeaks passed although the {what} is still referenced");
        return "";
    }
}
