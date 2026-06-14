// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Async;

/// <summary>
/// End-to-end coverage for async cancellation propagation across the C#↔Swift boundary
/// (Finding 39). The generated C# token callback always completes the awaiter with an
/// <see cref="OperationCanceledException"/> the instant the token fires, so the *only*
/// observable symptom of a lost cancel is Swift-side: the launched Swift <c>Task</c> runs
/// every slice to the end instead of bailing. <see cref="CancellationRaceProbe"/> tallies
/// exactly that — a "completed without observing cancel" tick — so these tests can assert
/// the durable invariant that a cancel is never lost.
///
/// The deterministic test pins the normal mid-flight path; the stress test drives the
/// register/assign/cancel/unregister registry under genuine concurrency, racing the cancel
/// against the synchronous launch to probe the pre-registration window (WINDOW A) and the
/// register→assign window (WINDOW B). The nanosecond pre-registration window cannot be hit
/// deterministically from managed code, so WINDOW A's generated fix is pinned deterministically
/// by the emitter unit tests (CancellationTokenEmitterTests); here it is exercised as a
/// no-loss / no-crash invariant under load.
/// </summary>
public class CancellationRaceTests : TestBase
{
    public CancellationRaceTests(TestResults results) : base(results) { }

    // 5ms per slice in the fixture; 80 slices ≈ 400ms of cancellable work — long enough that a
    // cancel fired at/just-after launch always lands while the Swift task is still running, so a
    // "completed without cancel" tick can only mean a genuinely lost cancel (not a benign race
    // where the work simply finished first).
    private const int WorkSlices = 80;

    public async Task TestPostLaunchCancel_SwiftTaskObservesCancellation()
    {
        // The async wrapper runs synchronously through the P/Invoke launch before returning its
        // Task, so by the time RaceableWorkAsync returns the Swift Task is registered and assigned.
        // Cancelling shortly after is a clean mid-flight cancel: the C# awaiter observes OCE AND
        // the cancel must propagate into the Swift Task so it bails rather than completing.
        CancellationRaceProbe.Reset();
        using var probe = new CancellationRaceProbe();
        using var cts = new CancellationTokenSource();

        var work = probe.RaceableWorkAsync((nint)WorkSlices, cts.Token);

        // Let the Swift task get into its sleep loop, then cancel mid-flight.
        await Task.Delay(20);
        cts.Cancel();

        try
        {
            await WithTimeout(work, DefaultAsyncTimeout);
            AssertTrue(false, "post-launch cancel — expected OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // Expected: the C# awaiter always observes cancellation when the token fires.
        }

        // The C# OCE above is guaranteed regardless of Swift; wait for every started Swift body to
        // resolve and assert it actually observed the cancel rather than running to completion.
        var (started, resolved) = await WaitForAllStartedSwiftTasksToResolve(maxMs: 3000);
        AssertEqual(1, started, "exactly one Swift work body should have started");
        AssertEqual(1, resolved, "the started Swift task must resolve");
        AssertEqual(0, CancellationRaceProbe.GetCompletedWithoutCancelCount(),
            "mid-flight cancel must not let the Swift task complete without observing cancellation");
        AssertEqual(1, CancellationRaceProbe.GetObservedCancelCount(),
            "the Swift task must record exactly one observed cancellation");
    }

    [Slow]
    public async Task TestConcurrentCancelRace_NeverLosesACancel()
    {
        // Drive many calls whose tokens are cancelled from a separate thread concurrently with the
        // synchronous launch, so the cancel can land in any window: before the pre-cancel check
        // (early FromCanceled, no Swift task launches), in the pre-registration window (WINDOW A),
        // in the register→assign window (WINDOW B), or after assignment (normal). Whichever window
        // it lands in, with the fix in place no cancel is lost: every Swift task that actually
        // launched must observe its cancel, so the completed-without-cancel tally stays at zero.
        const int iterations = 48;

        CancellationRaceProbe.Reset();
        using var probe = new CancellationRaceProbe();

        var sources = new List<CancellationTokenSource>(iterations);
        var tasks = new List<Task<int>>(iterations);

        for (int i = 0; i < iterations; i++)
        {
            var cts = new CancellationTokenSource();
            sources.Add(cts);
            // Fire the cancel from a pool thread to race the synchronous registration on this thread.
            _ = Task.Run(() => cts.Cancel());
            tasks.Add(probe.RaceableWorkAsync((nint)WorkSlices, cts.Token));
        }

        // Settle the C# awaiters. Each completes (OCE) the instant its token fires — well before the
        // Swift task finishes bailing — so this only drains the managed side.
        int observedOce = 0;
        int completedNormally = 0;
        foreach (var t in tasks)
        {
            try
            {
                await WithTimeout(t, DefaultAsyncTimeout);
                completedNormally++;
            }
            catch (OperationCanceledException)
            {
                observedOce++;
            }
        }

        // Now wait for every Swift body that started to resolve (bail or complete). A lost cancel
        // runs the full WorkSlices before it ticks completed-without-cancel and resolves, so gating
        // on started == resolved keeps the wait open until that slow tick lands — returning at the
        // first lull (while an uncancelled body is still mid-flight) would pass falsely.
        var (started, resolved) = await WaitForAllStartedSwiftTasksToResolve(maxMs: 5000);

        foreach (var cts in sources)
            cts.Dispose();

        TestLogger.Info(
            $"concurrent cancel race: {iterations} calls, oce={observedOce}, normalCompletions={completedNormally}, " +
            $"startedSwiftTasks={started}, resolvedSwiftTasks={resolved}, observedCancel={CancellationRaceProbe.GetObservedCancelCount()}, " +
            $"completedWithoutCancel={CancellationRaceProbe.GetCompletedWithoutCancelCount()}");

        // Meaningfulness gate: if every call were cancelled before its synchronous pre-cancel check,
        // no Swift task would launch and started == resolved == 0 would make every tally below pass
        // vacuously — a green run that exercised nothing. The async wrapper runs synchronously through
        // the P/Invoke launch before returning (see TestPostLaunchCancel's note), and the cancel here
        // is dispatched to a pool thread, so across 48 synchronous launches at least one always wins
        // its race and launches; a started == 0 run is a real failure to exercise the path, not benign.
        AssertTrue(started > 0,
            "concurrent cancel stress must launch at least one Swift task, else the race is never exercised");

        // Sanity: the wait actually drained every started Swift body, so the tally reads below are
        // not premature (no uncancelled body still mid-flight, about to tick completed-without-cancel).
        AssertEqual(resolved, started,
            "every started Swift body must resolve before the tallies are read");

        // The durable invariant: no launched Swift task ran to completion without observing its
        // cancel. (Calls cancelled before the pre-cancel check never launch a Swift task and so are
        // not counted here — that is a legitimate early cancel, not a lost one.)
        AssertEqual(0, CancellationRaceProbe.GetCompletedWithoutCancelCount(),
            "no concurrently-cancelled call may lose its cancel and run the Swift task to completion");

        // Every resolved Swift task resolved by observing cancellation (resolved == observed + completed,
        // and completed is asserted zero above) — i.e. the registry replayed/honored every cancel.
        AssertEqual(CancellationRaceProbe.GetObservedCancelCount(), resolved,
            "every launched Swift task must resolve by observing its cancellation");
    }

    /// <summary>
    /// Waits until the set of started Swift work bodies has settled (no new body has begun for
    /// 300ms) AND every started body has resolved, then returns the final (started, resolved)
    /// tallies. A lost cancel increments <c>started</c> at body entry but only increments
    /// <c>resolved</c> after running the full slice budget (~400ms), so gating on
    /// <c>resolved &gt;= started</c> holds the wait open until that slow uncancelled tick manifests;
    /// a fixed "wait for N resolved" floor would return at the first lull and pass falsely.
    /// </summary>
    /// <remarks>
    /// <c>resolved</c> is read <em>before</em> <c>started</c> each poll. Because every resolved body
    /// was started earlier, <c>started &gt;= resolved</c> holds at every instant; reading resolved
    /// first means the <c>resolved &gt;= started</c> check can only pass on a clean snapshot where no
    /// body is in-flight and none started or resolved between the two reads.
    /// </remarks>
    private static async Task<(int started, int resolved)> WaitForAllStartedSwiftTasksToResolve(int maxMs)
    {
        int lastStarted = -1;
        int stableStartedMs = 0;
        int elapsed = 0;
        while (elapsed < maxMs)
        {
            await Task.Delay(50);
            elapsed += 50;

            int resolved = CancellationRaceProbe.GetResolvedCount();
            int started = CancellationRaceProbe.GetStartedCount();

            if (started == lastStarted)
                stableStartedMs += 50;
            else
            {
                stableStartedMs = 0;
                lastStarted = started;
            }

            if (started > 0 && stableStartedMs >= 300 && resolved >= started)
                return (started, resolved);
        }
        return (CancellationRaceProbe.GetStartedCount(), CancellationRaceProbe.GetResolvedCount());
    }
}
