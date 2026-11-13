// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Foundation;
using Swift.Runtime;

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Tracks Swift object lifecycle via the SwiftBindingsTestLib allocation counters.
/// </summary>
public static class LifetimeTracker
{
    // P/Invoke declarations for the Swift allocation tracking functions
    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_ResetAllocationCounters")]
    private static extern void ResetAllocationCounters();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_GetAllocationCount")]
    private static extern int GetAllocationCount();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_GetDeallocationCount")]
    private static extern int GetDeallocationCount();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_GetLiveObjectCount")]
    private static extern int GetLiveObjectCount();

    // Returns a strdup'd C string describing the registry-tracked objects still live (see
    // TrackedRef); the caller must free it with FreeTrackedString. Lets a leak probe that
    // never balances NAME the survivors — category, tag, allocation order — instead of
    // reporting a bare count.
    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_DumpLiveTrackedObjects")]
    private static extern IntPtr DumpLiveTrackedObjects();

    [DllImport("SwiftBindingsTestLib", EntryPoint = "SwiftBindingsTestLib_FreeString")]
    private static extern void FreeTrackedString(IntPtr ptr);

    // DIAGNOSTIC (not a correctness mechanism). When true, the quiescence drain also drains an
    // autorelease pool and briefly pumps the current thread's run loop each poll — to test the
    // hypothesis that a rare struct-with-ref survivor is held by a release scheduled on a run
    // loop / autorelease pool that GC.WaitForPendingFinalizers does not pump, rather than a real
    // lost-release leak. It is deliberately separate from the exact-count gate: the leak
    // assertion below is unchanged and still fails deterministically regardless of this flag, so
    // turning it off cannot hide a genuine leak. If a soak with this active goes fully green
    // where it otherwise flaked, that is evidence for the run-loop/pool hypothesis; the decision
    // to keep or remove it is deliberate, not silent.
    internal static bool RunLoopAutoreleaseDrainDiagnostic = true;
    private static bool _runLoopDiagLogged;

    // Quiescence-drain budgets. Swift ARC releases run asynchronously — a completion
    // callback on a foreign executor, a background finalizer thread — so the tracker's
    // dealloc counter can lag the C# call that dropped the last reference by a short,
    // nondeterministic interval (observed in the hundreds of ms). These let a genuinely
    // slow release land before an assert, and let a prior test's in-flight release settle
    // before the counters are zeroed, without ever loosening the exact-count gates: a real
    // leak never balances, so it still fails deterministically once the timeout elapses.
    private const int QuiescencePollMs = 25;
    private const int AssertQuiescenceTimeoutMs = 3000;
    private const int ResetQuiescenceTimeoutMs = 2000;

    /// <summary>
    /// Resets the allocation counters. Call before starting a lifetime test.
    /// </summary>
    public static void Reset()
    {
        try
        {
            // Drain any still-in-flight deallocation from the PRIOR test window BEFORE zeroing
            // the counters. A Swift release that lands just AFTER the reset would increment the
            // dealloc counter with no matching allocation in the new window and drive the live
            // count negative — a phantom "-1 leak" in the next probe (deallocations =
            // allocations + 1). A still-pending release keeps the object counted as live (its
            // alloc has landed, its dealloc has not: live = allocations - deallocations > 0), so
            // draining until live reaches 0 — not merely until the counters stop moving — is what
            // proves no release is still in flight across the reset boundary. "Counters stopped
            // moving" is too weak: they can sit stably at (N, N-1, 1) for a poll window while a
            // release is still scheduled, letting the stale dealloc straddle the reset (and, in
            // the next window, cancel a genuinely leaked allocation to a false live==0). Once
            // live hits 0 every prior-window alloc has a matching dealloc already counted, so
            // nothing can land in the gap before ResetAllocationCounters(). Best-effort: if a
            // prior-window leak keeps live above 0 past the timeout the reset proceeds anyway —
            // that leak is the prior test's AssertNoLeaks to report, not this reset's.
            DrainWhileAbove(0, ResetQuiescenceTimeoutMs);
            ResetAllocationCounters();
            // Zero the value-witness release-path counters in the same window as the allocation
            // counters, so a leak probe's failure readout covers only this test's release activity.
            ReleasePathDiagnostics.Reset();
            TestLogger.Memory("Allocation counters reset");
        }
        catch (Exception ex)
        {
            TestLogger.Warning($"Failed to reset allocation counters: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets the current allocation statistics.
    /// </summary>
    public static (int allocations, int deallocations, int live) GetStats()
    {
        try
        {
            return (GetAllocationCount(), GetDeallocationCount(), GetLiveObjectCount());
        }
        catch (Exception ex)
        {
            TestLogger.Warning($"Failed to get allocation stats: {ex.Message}");
            return (-1, -1, -1);
        }
    }

    /// <summary>
    /// Logs the current allocation statistics.
    /// </summary>
    public static void LogStats(string context = "")
    {
        var (alloc, dealloc, live) = GetStats();
        var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
        TestLogger.Memory($"{prefix}Allocations={alloc}, Deallocations={dealloc}, Live={live}");
    }

    /// <summary>
    /// Asserts that all tracked objects have been deallocated.
    /// Forces GC first to ensure finalizers run.
    /// </summary>
    public static void AssertNoLeaks(string context = "") => AssertNoLeaks(context, observations: null);

    /// <summary>
    /// <see cref="AssertNoLeaks(string)"/> with the churn's weak <paramref name="observations"/>:
    /// on failure the message also says which abandoned objects are still strongly reachable and
    /// which thread's stack or registers hold a word pointing into them.
    /// </summary>
    internal static unsafe void AssertNoLeaks(string context, LeakObservations? observations)
    {
        int stackAnchor = 0;
        nint scanFrom = (nint)(&stackAnchor);
        // Drain finalizers, then re-check with conservative-root eviction and a bounded
        // quiescence wait if anything still looks live. Two orthogonal effects are absorbed
        // here, neither of which is a real leak:
        //   1. Async release latency. A Swift object dropped on the C# side is ARC-released
        //      by a completion callback / background executor a short, nondeterministic time
        //      later; the dealloc counter can lag the abandoning call. Re-draining across
        //      short sleeps (up to the timeout) lets that release land.
        //   2. Conservative-root phantoms. Under Mono's conservative stack scan a stale
        //      pointer-shaped value left in a spilled slot by a just-returned churn helper
        //      can pin one already-dead wrapper; overwriting that stack region with
        //      non-pointer fill and re-draining lets the next collect reclaim it. A stale word
        //      in a parked thread-pool worker's saved state is cleared by waking the workers
        //      (see RefreshParkedPoolWorkers).
        // Draining only ever releases objects (it never allocates), so the live count is
        // monotonically non-increasing here: a genuine leak — a still-reachable reference or
        // an orphaned native retain — never balances, stays non-zero for the whole timeout,
        // and the exact-zero gate below still fails deterministically with the same message.
        var (alloc, dealloc, live) = DrainWhileAbove(0, AssertQuiescenceTimeoutMs);

        if (live != 0)
        {
            var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
            throw new AssertionException(
                $"{prefix}Memory leak detected: {live} object(s) not deallocated " +
                $"(allocations={alloc}, deallocations={dealloc}). {DescribeLiveIdentities()} " +
                $"{ReleasePathDiagnostics.Snapshot()}" +
                (observations is null ? "" : $" {observations.Summarize()} {observations.DescribeRoots(scanFrom)}"));
        }

        TestLogger.Memory($"No leaks: {alloc} allocated, {dealloc} deallocated");
    }

    // Force finalizers and give slow async Swift releases time to land, re-checking with
    // conservative-root eviction, until the tracker reports at most <paramref name="target"/>
    // live objects or the bounded timeout elapses. Returns the final snapshot. Because
    // draining only releases (never allocates) the live count cannot rise here, so this can
    // only turn a transient over-count (a not-yet-landed release) into the expected count —
    // it can never mask a genuine leak, which keeps the count above target for the whole
    // timeout. The clean case (already at/below target after the first drain) returns
    // immediately with no sleeps.
    private static (int alloc, int dealloc, int live) DrainWhileAbove(int target, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        var snap = DrainAndSnapshot();
        bool poolRefreshed = false;
        while (snap.live > target && Environment.TickCount64 < deadline)
        {
            // Clear a conservative-root phantom, then re-drain: cheap, and resolves at once
            // if a dead-but-pinned stack straggler was the only thing above target. The pool
            // refresh covers a stale word in a parked worker's saved state; once is enough,
            // since nothing the drain does gives a worker a pointer to a tracked object.
            EvictConservativeStackRoots();
            if (!poolRefreshed)
            {
                RefreshParkedPoolWorkers();
                poolRefreshed = true;
            }
            snap = DrainAndSnapshot();
            if (snap.live <= target)
                break;
            // DIAGNOSTIC (see RunLoopAutoreleaseDrainDiagnostic): give a run-loop /
            // autorelease-scheduled release a chance to run. Separate from the exact-count
            // gate — it can only release, never allocate, so it cannot mask a genuine leak.
            if (RunLoopAutoreleaseDrainDiagnostic)
            {
                PumpRunLoopAndDrainAutoreleasePool();
                snap = DrainAndSnapshot();
                if (snap.live <= target)
                    break;
            }
            // Still above target: yield briefly so an in-flight async release can land,
            // then re-drain and re-test against the deadline.
            Thread.Sleep(QuiescencePollMs);
            snap = DrainAndSnapshot();
        }
        return snap;
    }

    // DIAGNOSTIC helper. Drain an autorelease pool and briefly pump the current thread's run
    // loop, so a release scheduled on either — which GC.WaitForPendingFinalizers does not run —
    // gets a chance to fire. Pure observation aid for the struct-with-ref survivor hypothesis;
    // it never allocates a tracked object, so it cannot turn a real leak green. Non-inlined and
    // exception-swallowing so it can't perturb the drain frame or escape as a test failure.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PumpRunLoopAndDrainAutoreleasePool()
    {
        if (!_runLoopDiagLogged)
        {
            TestLogger.Memory("[DIAG] run-loop + autorelease-pool drain active in leak quiescence");
            _runLoopDiagLogged = true;
        }
        try
        {
            using (new NSAutoreleasePool())
            {
                // No input sources on this run loop returns immediately; the point is to let
                // any run-loop-scheduled release run and to drain the pool on dispose.
                NSRunLoop.Current.RunUntil(NSDate.FromTimeIntervalSinceNow(0.003));
            }
        }
        catch
        {
            // A diagnostic must never fail the probe it annotates.
        }
    }

    // Force finalizers to run and snapshot the tracker counters. Two collects bracket
    // the finalizer wait so any object resurrected-then-reabandoned during finalization
    // is also reclaimed before the snapshot is read.
    private static (int alloc, int dealloc, int live) DrainAndSnapshot()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GetStats();
    }

    // Overwrite the stack region a just-returned callee may have left holding a stale
    // pointer-shaped value, so Mono's conservative scan stops treating an already-dead
    // wrapper as rooted and the following collect can reclaim it. Non-inlined so the fill
    // lands below the assert/drain frames (over the popped churn-helper frame). A real leak
    // is a reachable reference, not a dead stack slot, so this cannot clear one.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EvictConservativeStackRoots()
    {
        Span<nint> scratch = stackalloc nint[1024];
        FillWithNonPointers(scratch);
        _evictSink = unchecked((int)SumBuffer(scratch));
    }

    // Write and read-back are split across non-inlinable boundaries and routed through
    // the volatile sink so the fill cannot be constant-folded away or dead-store
    // eliminated: the seed is a runtime value the JIT can't know at compile time, the
    // sum escapes to the sink, and neither loop is visible to the other for fusion — so
    // the stores must actually land in this frame's stack memory (the whole point of the
    // eviction). Values stay small so they can't be mistaken for heap references by the
    // next conservative scan.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FillWithNonPointers(Span<nint> buffer)
    {
        nint seed = (nint)((_evictSink & 0xFFFF) | 1);
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = seed + i;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static nint SumBuffer(Span<nint> buffer)
    {
        nint acc = 0;
        for (int i = 0; i < buffer.Length; i++)
            acc += buffer[i];
        return acc;
    }

    private static volatile int _evictSink;

    // How long the pool may go without another refresh item starting before every idle worker
    // is taken to have woken. Waking a parked worker takes microseconds, and each woken worker
    // wakes the next while items remain queued, whereas the pool injects a new thread only after
    // a starvation delay many times longer. A gap this long therefore means every idle worker is
    // already holding an item, and the wait ends without growing the pool.
    private const int PoolRefreshQuietMs = 25;
    private const int PoolRefreshTimeoutMs = 1000;

    /// <summary>
    /// Wakes every parked thread-pool worker once, has each overwrite a deep region of its own
    /// stack with non-pointer fill, and lets it park again. Mono only.
    /// <para>
    /// When a Mono thread blocks, as an idle pool worker does while it waits for work, the runtime
    /// saves its registers and a copy of the stack just below its stack pointer, and every
    /// collection scans that saved state conservatively for as long as the thread stays parked,
    /// which for an idle worker can be many seconds. A pointer-shaped word in it that names a
    /// since-freed nursery address pins whatever object the allocator later puts there. A leak
    /// probe's churn allocates hundreds of objects in a row, so one of them can land on such an
    /// address and survive every drain although nothing references it. Waking the worker
    /// discards the saved state, and the scrub makes sure the state saved when it parks again
    /// holds no stale word. Each item holds its worker until no further item has started for
    /// <see cref="PoolRefreshQuietMs"/>, so one worker cannot run every item while the rest stay
    /// parked.
    /// </para>
    /// <para>
    /// This only removes roots that no live reference stands behind. A genuine leak, a reachable
    /// reference or an orphaned native retain, does not depend on any thread's saved state and
    /// survives it.
    /// </para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void RefreshParkedPoolWorkers()
    {
        if (!SwiftRuntimeInfo.IsMonoRuntime)
            return;
        int existing = ThreadPool.ThreadCount;
        // A caller running on a pool thread is not parked, and cannot take an item.
        int workers = existing - (Thread.CurrentThread.IsThreadPoolThread ? 1 : 0);
        if (workers <= 0)
            return;
        // The pool lets only as many items run at once as its current thread goal, which can sit
        // below the number of threads that exist, and the surplus threads stay parked. Raising
        // the floor to the existing count for the refresh lets every one of them take an item.
        ThreadPool.GetMinThreads(out int minWorkers, out int minIo);
        bool raised = minWorkers < existing && ThreadPool.SetMinThreads(existing, minIo);
        try
        {
            var refresh = new PoolRefresh(workers);
            for (int i = 0; i < workers; i++)
                ThreadPool.UnsafeQueueUserWorkItem(refresh, preferLocal: false);
            long deadline = Environment.TickCount64 + PoolRefreshTimeoutMs;
            while (refresh.StartedCount < workers
                && Environment.TickCount64 < deadline
                && refresh.StartSignal.Wait(PoolRefreshQuietMs))
            {
            }
            refresh.Release.Set();
            // Items still queued run on the released workers and finish at once. Waiting for
            // them keeps refresh work from overlapping the caller's next step.
            refresh.Finished.Wait(PoolRefreshTimeoutMs);
        }
        finally
        {
            if (raised)
                ThreadPool.SetMinThreads(minWorkers, minIo);
        }
    }

    private sealed class PoolRefresh : IThreadPoolWorkItem
    {
        private int _started;
        internal readonly SemaphoreSlim StartSignal = new(0);
        internal readonly ManualResetEventSlim Release = new(false);
        internal readonly CountdownEvent Finished;

        internal PoolRefresh(int items) => Finished = new CountdownEvent(items);

        internal int StartedCount => Volatile.Read(ref _started);

        public void Execute()
        {
            ScrubStack();
            Interlocked.Increment(ref _started);
            StartSignal.Release();
            Release.Wait(PoolRefreshTimeoutMs);
            Finished.Signal();
        }
    }

    // 64 KB of non-pointer fill below the caller: deeper than any saved stack copy a parked
    // worker's next block can capture from the frames that ran before it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ScrubStack()
    {
        Span<nint> scratch = stackalloc nint[8192];
        FillWithNonPointers(scratch);
        _evictSink = unchecked((int)SumBuffer(scratch));
    }

    /// <summary>
    /// Asserts that the expected number of objects are currently live.
    /// </summary>
    public static void AssertLiveCount(int expected, string context = "")
    {
        // Raw snapshot first — no GC — so a test whose count is already exact behaves exactly
        // as before (this method used to be a pure snapshot). Only when MORE objects are live
        // than expected — an in-flight async release from earlier in the test that has not yet
        // landed — do we drain toward the target. Draining only releases (never allocates), so
        // it can lower a transient over-count to the expected value but can never lift a real
        // under-count (a genuine over-release fails immediately) or hide a genuine leak (which
        // stays above target past the timeout). Net: this can only convert a flaky over-count
        // into the correct pass, never turn a real failure green — objects the test still holds
        // a reference to survive every collect, so a positive expected count cannot be eroded.
        var (_, _, live) = GetStats();
        if (live > expected)
            (_, _, live) = DrainWhileAbove(expected, AssertQuiescenceTimeoutMs);

        if (live != expected)
        {
            var prefix = string.IsNullOrEmpty(context) ? "" : $"{context}: ";
            throw new AssertionException(
                $"{prefix}Expected {expected} live object(s), got {live}. {DescribeLiveIdentities()} " +
                $"{ReleasePathDiagnostics.Snapshot()}");
        }
    }

    /// <summary>
    /// Asks the Swift registry to name the tracked objects still live, so a leak that never
    /// balances becomes root-cause evidence (which struct-with-ref family, which allocation
    /// order/tag) instead of another tally mark. Exposed to the test assembly so a probe can also
    /// assert the identity wiring itself works — a survivor list that is empty because a fixture
    /// never registered identity looks exactly like a survivor list that is empty because nothing
    /// leaked. Best-effort: never throws — a diagnostic must not mask the assertion it annotates.
    /// </summary>
    internal static string DescribeLiveIdentities()
    {
        try
        {
            var ptr = DumpLiveTrackedObjects();
            if (ptr == IntPtr.Zero)
                return "Live identities: <unavailable>";
            try
            {
                return $"Live identities: {Marshal.PtrToStringAnsi(ptr)}";
            }
            finally
            {
                FreeTrackedString(ptr);
            }
        }
        catch (Exception ex)
        {
            return $"Live identities: <dump failed: {ex.Message}>";
        }
    }

    /// <summary>
    /// Runs a leak probe's allocation/churn phase on a dedicated thread and returns only after
    /// that thread has exited.
    /// <para>
    /// Mono scans every live thread's stack and saved registers conservatively. A churn loop run
    /// on the test thread leaves pointer-shaped stale values to the objects it just abandoned in
    /// that thread's own frames, the frames of its callers and its callee-saved registers. The
    /// drain in <see cref="AssertNoLeaks"/> can only overwrite stack below its own frame, so one
    /// of those values can pin a dead wrapper past every collection. When the churn thread
    /// exits, its stack and registers stop being roots. Before returning, the parked thread-pool
    /// workers are woken and scrubbed (see <see cref="RefreshParkedPoolWorkers"/>), because a
    /// stale word in a worker's saved state can pin a churned object the same way. A genuine
    /// leak survives both: a reachable reference or an orphaned native retain does not depend on
    /// any thread's stack.
    /// </para>
    /// <para>
    /// The result crosses back to the test thread, so it must not reference a churned object.
    /// Return counts, strings or weak observations. The churn must finish all asynchronous work
    /// before it returns. Use <see cref="RunIsolated(Func{Task})"/> for an async churn. It must
    /// not need the main thread: a call that has to run there deadlocks, because the test
    /// thread is blocked in the join. CoreCLR and NativeAOT scan stacks precisely, so there
    /// the isolation is inert and the probe keeps its meaning.
    /// </para>
    /// </summary>
    public static T RunIsolated<T>(Func<T> churn)
    {
        if (typeof(Task).IsAssignableFrom(typeof(T)))
            throw new InvalidOperationException(
                "RunIsolated<T> was handed an async churn: the Task would escape the isolated thread unawaited. " +
                "Use RunIsolated(Func<Task>) or RunIsolated(Func<Task<T>>).");

        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = churn(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        })
        { Name = "LifetimeTracker.RunIsolated" };
        thread.Start();
        thread.Join();
        RefreshParkedPoolWorkers();
        failure?.Throw();
        return result;
    }

    /// <inheritdoc cref="RunIsolated{T}(Func{T})"/>
    public static void RunIsolated(Action churn)
        => RunIsolated(() => { churn(); return true; });

    /// <summary>
    /// The async form of <see cref="RunIsolated{T}(Func{T})"/>. The churn runs on a dedicated
    /// thread under a private single-thread <see cref="SynchronizationContext"/>, so every
    /// continuation inside it also runs on that thread rather than on the main queue or the
    /// thread pool. The call blocks until the churn's task has completed and the thread has
    /// exited, then rethrows the churn's exception, if any. Swift completion callbacks still run
    /// on Swift's own threads. Only the C# side of the churn is isolated.
    /// </summary>
    public static T RunIsolated<T>(Func<Task<T>> churn)
        => RunIsolated(() => IsolatedPump.Run(churn));

    /// <inheritdoc cref="RunIsolated{T}(Func{Task{T}})"/>
    public static void RunIsolated(Func<Task> churn)
        => RunIsolated(() => IsolatedPump.Run(async () => { await churn(); return true; }));

    // A minimal single-thread synchronization context: the isolated thread drains posted
    // continuations until the churn's task completes, so the whole async churn, continuations
    // included, executes on the thread that is about to exit.
    private sealed class IsolatedPump : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback callback, object? state)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            // A continuation posted after the churn's task completed is stray work the churn did
            // not await; it no longer has a pump to run on, so hand it to the thread pool.
            try { _queue.Add((d, state)); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                ThreadPool.QueueUserWorkItem(_ => d(state));
            }
        }

        public override void Send(SendOrPostCallback d, object? state)
            => throw new NotSupportedException("Synchronous Send onto an isolated leak-probe churn thread");

        public override SynchronizationContext CreateCopy() => this;

        internal static T Run<T>(Func<Task<T>> churn)
        {
            var previous = Current;
            var pump = new IsolatedPump();
            SetSynchronizationContext(pump);
            try
            {
                var task = churn();
                task.ContinueWith(_ => pump._queue.CompleteAdding(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                foreach (var (callback, state) in pump._queue.GetConsumingEnumerable())
                    callback(state);
                return task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
                pump._queue.Dispose();
            }
        }
    }

    /// <summary>
    /// Tracks allocations during an action.
    /// </summary>
    public static (int allocsBefore, int allocsAfter, int newAllocs) TrackAllocations(Action action)
    {
        var before = GetAllocationCount();
        action();
        var after = GetAllocationCount();
        return (before, after, after - before);
    }

    /// <summary>
    /// Runs an action within a tracked scope that verifies cleanup. The action runs isolated
    /// (see <see cref="RunIsolated(Action)"/>), so it must not need the main thread.
    /// </summary>
    public static void RunWithLeakCheck(Action action, string context = "")
    {
        Reset();

        try
        {
            RunIsolated(action);
        }
        finally
        {
            // AssertNoLeaks now drains finalizers and waits out any slow async Swift release
            // itself (bounded), so no fixed pre-delay is needed here — a fixed sleep is exactly
            // the timing-fragile pattern this replaced.
            AssertNoLeaks(context);
        }
    }
}
