// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// What a reverse-dispatch callback does when it arrives at a <b>consumer-owned</b> carrier whose
/// implementation has already been collected.
///
/// <para>
/// A C# implementation assigned into a non-retaining Swift slot (<c>weak</c>/<c>unowned</c>) is
/// carried by a conformer box Swift never retained, so the carrier follows the consumer's own
/// implementation object. That makes one state reachable from perfectly ordinary application code
/// which the Swift-rooted lane can never reach: Swift keeps the conformer box alive through some
/// OTHER reference — a private observer list, a captured closure, an operation already in flight —
/// while the consumer drops the implementation behind it, and then calls back.
/// </para>
///
/// <para>
/// On the Swift-rooted lane an unresolvable implementation is an invariant violation and the
/// receiver takes the process down. On this lane it is a legal state, so the callback degrades the
/// way Swift itself treats a <c>nil</c> weak delegate: a <c>Void</c> requirement is dropped, an
/// Optional-returning one answers <c>nil</c>, a <c>Bool</c>-returning one answers <c>false</c>, and
/// an <c>async throws</c> one surfaces an error on the caller's own call path. The two throwing
/// shapes are NOT the same: only <c>async throws</c> reaches C# through a continuation that carries
/// an error function pointer, so a synchronous <c>throws</c> requirement — whose receiver is a plain
/// cdecl thunk returning a value buffer — degrades to its return type's identity value like any
/// non-throwing requirement. Because every one of those outcomes is
/// silent by construction, the carrier reports itself exactly once through
/// <see cref="ProxyDegradation"/> — which is the only thing that makes "my delegate stopped firing"
/// diagnosable, and so is asserted here as tightly as the values are.
/// </para>
/// </summary>
public class CollectedImplDegradationTests : TestBase
{
    public CollectedImplDegradationTests(TestResults results) : base(results) { }

    private const int GcCycles = 6;

    /// <summary>
    /// Force a GC on a worker thread (the main thread blocks on Join with a minimal live-local
    /// footprint) so Mono's conservative stack scan does not pin the dropped implementation.
    /// </summary>
    private static void ForceGc()
    {
        var worker = new System.Threading.Thread(ForceGcWorker) { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceGcWorker()
    {
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
    /// Runs <paramref name="action"/> on a worker thread and waits for it to exit, so every
    /// reference the action touched dies with a stack frame that no longer exists when the
    /// collection runs. Mono scans thread stacks conservatively: a dead slot left behind in a
    /// still-live frame reads as a root, which would pin the implementation under test.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunOnRetiredThread(Action action)
    {
        var worker = new System.Threading.Thread(() => action()) { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    // ---- F1: Swift kept its own reference; the consumer dropped theirs ------

    /// <summary>
    /// The failure this lane was designed around. The consumer assigns into the <c>weak</c> slot,
    /// the framework privately retains the delegate as well, the consumer drops their
    /// implementation — and Swift then calls all three synchronous requirement shapes through its
    /// private reference. The process must survive, each shape must answer with its own identity
    /// value, and the whole episode must report itself once (not once per call).
    /// </summary>
    public void TestCollectedImplDegradesVoidOptionalAndBoolRequirements()
    {
        var host = new CollectedDelegateHost();
        var implRef = AssignAndRetainInternally(host);

        ForceGc();

        AssertFalse(implRef.IsAlive,
            "the consumer's implementation is collectable once they drop it, even though Swift kept a private strong reference to the carrier");
        AssertTrue(host.RetainsDelegateInternally,
            "Swift still holds its private strong reference to the delegate");
        AssertTrue(host.HasWeakDelegate,
            "the weak slot still reads non-nil, because Swift's private reference keeps the conformer box alive");

        var reportsBefore = ProxyDegradation.ReportCount;
        var reported = new List<SwiftProxyImplCollectedEventArgs>();
        void OnImplCollected(object? sender, SwiftProxyImplCollectedEventArgs args)
        {
            lock (reported)
                reported.Add(args);
        }

        ProxyDegradation.ImplCollected += OnImplCollected;
        try
        {
            var callsBefore = CollectedDelegateImpl.DidUpdateCalls;
            AssertEqual(0, host.InvokeVoidFromRetained(11),
                "the fixture dispatched the void requirement from its private strong reference");
            AssertEqual(callsBefore, CollectedDelegateImpl.DidUpdateCalls,
                "the void requirement was dropped rather than dispatched into a collected implementation");

            AssertEqual(-2, host.InvokeOptionalFromRetained(13),
                "the Optional-returning requirement answered nil, the way a nil weak delegate would");
            AssertEqual(0, host.InvokeBoolFromRetained(17),
                "the Bool-returning requirement answered false rather than fabricating a value");
        }
        finally
        {
            ProxyDegradation.ImplCollected -= OnImplCollected;
        }

        AssertEqual(reportsBefore + 1, ProxyDegradation.ReportCount,
            "three degraded callbacks on one carrier report exactly once, so a per-frame delegate cannot flood the log");
        AssertEqual(1, reported.Count,
            "the diagnostic event fired exactly once for this carrier");
        AssertTrue(reported.Count == 1 && reported[0].Member.Length > 0,
            "the diagnostic names the member Swift called");

        TestLogger.Info($"[CollectedImpl] degraded carrier reported once: {(reported.Count == 1 ? reported[0].Member : "<none>")}");
        GC.KeepAlive(host);
    }

    /// <summary>
    /// The positive control for the same fixture: while the consumer still holds their
    /// implementation, every requirement dispatches normally and nothing degrades. Without this,
    /// the degradation assertions above could pass on a fixture that never reached managed code at
    /// all.
    /// </summary>
    public void TestLiveImplDispatchesNormallyThroughThePrivateReference()
    {
        var host = new CollectedDelegateHost();
        var impl = new CollectedDelegateImpl();
        host.WeakDelegate = impl;
        host.RetainDelegateInternally();

        ForceGc();

        var reportsBefore = ProxyDegradation.ReportCount;
        var callsBefore = CollectedDelegateImpl.DidUpdateCalls;

        AssertEqual(0, host.InvokeVoidFromRetained(11), "the fixture dispatched the void requirement");
        AssertEqual(callsBefore + 1, CollectedDelegateImpl.DidUpdateCalls,
            "the void requirement reached the live implementation");
        AssertEqual(3013, host.InvokeOptionalFromRetained(13),
            "the Optional-returning requirement returned the live value (13 + 3000)");
        AssertEqual(1, host.InvokeBoolFromRetained(17),
            "the Bool-returning requirement returned the live answer");
        AssertEqual(reportsBefore, ProxyDegradation.ReportCount,
            "a live implementation degrades nothing and reports nothing");

        TestLogger.Info("[CollectedImpl] live implementation dispatched all three requirement shapes");
        GC.KeepAlive(impl);
        GC.KeepAlive(host);
    }

    /// <summary>
    /// The one requirement shape with somewhere to put a failure. A throwing requirement on a
    /// collected consumer-owned carrier surfaces an error through the Swift error channel, so the
    /// consumer observes it as a failure of their own call rather than as a dead process.
    /// </summary>
    public async Task TestCollectedImplSurfacesAnErrorFromAThrowingRequirement()
    {
        var host = new CollectedThrowingDelegateHost();
        var implRef = AssignThrowingAndRetainInternally(host);

        ForceGc();

        AssertFalse(implRef.IsAlive, "the consumer's implementation is collectable once they drop it");
        AssertTrue(host.RetainsDelegateInternally, "Swift still holds its private strong reference");

        var reportsBefore = ProxyDegradation.ReportCount;
        Exception? caught = null;
        try
        {
            await host.InvokeFromRetainedAsync(21);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        AssertNotNull(caught,
            "the throwing requirement surfaced the collected implementation as an error on the consumer's own call path");
        AssertEqual(reportsBefore + 1, ProxyDegradation.ReportCount,
            "the degraded throwing callback reported itself exactly once");

        TestLogger.Info($"[CollectedImpl] throwing requirement surfaced: {caught?.GetType().Name}");
        GC.KeepAlive(host);
    }

    /// <summary>
    /// Positive control for the throwing requirement: a live implementation returns its value and
    /// nothing degrades.
    /// </summary>
    public async Task TestLiveImplReturnsAValueFromAThrowingRequirement()
    {
        var host = new CollectedThrowingDelegateHost();
        var impl = new CollectedThrowingDelegateImpl();
        host.WeakDelegate = impl;
        host.RetainDelegateInternally();

        ForceGc();

        var reportsBefore = ProxyDegradation.ReportCount;
        var result = await host.InvokeFromRetainedAsync(21);

        AssertEqual(4021, result, "the throwing requirement returned the live value (21 + 4000)");
        AssertEqual(reportsBefore, ProxyDegradation.ReportCount, "a live implementation degrades nothing");

        TestLogger.Info("[CollectedImpl] live throwing requirement returned its value");
        GC.KeepAlive(impl);
        GC.KeepAlive(host);
    }

    /// <summary>
    /// The OTHER throwing shape, and the one that behaves differently from the table above. A
    /// synchronous <c>throws</c> requirement reverse-dispatches through a plain cdecl thunk that
    /// returns a value buffer: the Swift-side conformance has no error out-slot to write and no
    /// <c>throw</c> in its body, so there is nowhere for the boundary to put a failure. A degraded
    /// synchronous throwing call therefore answers with its return type's identity value, exactly
    /// as a non-throwing one does — and still reports itself once, which is the only signal a
    /// consumer gets that it happened.
    ///
    /// <para>The fixture reserves <c>-3</c> for "Swift caught a thrown error". Asserting that the
    /// result is the degraded <c>0</c> and NOT <c>-3</c> is what pins the documented behaviour:
    /// if the synchronous shape ever gains a real error channel, this test goes red and says so
    /// rather than silently changing what consumers observe.</para>
    /// </summary>
    public void TestCollectedImplDegradesASynchronousThrowingRequirementToItsIdentityValue()
    {
        var host = new CollectedSyncThrowingDelegateHost();
        var implRef = AssignSyncThrowingAndRetainInternally(host);

        ForceGc();

        AssertFalse(implRef.IsAlive, "the consumer's implementation is collectable once they drop it");
        AssertTrue(host.RetainsDelegateInternally, "Swift still holds its private strong reference");

        var reportsBefore = ProxyDegradation.ReportCount;
        var result = host.InvokeFromRetained(31);

        AssertTrue(result != -1,
            "the fixture dispatched through its private strong reference, so -1 would mean the fixture lost the delegate rather than the binding degrading");
        AssertTrue(result != -3,
            "a synchronous throwing requirement has no error channel on the reverse-dispatch path, so Swift must NOT observe a thrown error; if this fires, the shape gained one and this behaviour is now documented wrong");
        AssertEqual(0, result,
            "the degraded synchronous throwing call answered with the return type's identity value, the same as a non-throwing requirement");
        AssertEqual(reportsBefore + 1, ProxyDegradation.ReportCount,
            "the degraded synchronous throwing callback still reported itself exactly once, which is the only signal that it happened");

        TestLogger.Info($"[CollectedImpl] synchronous throwing requirement degraded to {result} and reported once");
        GC.KeepAlive(host);
    }

    /// <summary>
    /// Positive control for the synchronous throwing requirement: a live implementation returns its
    /// value through the same path, so the degraded assertion above cannot pass on a fixture that
    /// never reached managed code.
    /// </summary>
    public void TestLiveImplReturnsAValueFromASynchronousThrowingRequirement()
    {
        var host = new CollectedSyncThrowingDelegateHost();
        var impl = new CollectedSyncThrowingDelegateImpl();
        host.WeakDelegate = impl;
        host.RetainDelegateInternally();

        ForceGc();

        var reportsBefore = ProxyDegradation.ReportCount;

        AssertEqual(6031, host.InvokeFromRetained(31),
            "the synchronous throwing requirement returned the live value (31 + 6000)");
        AssertEqual(reportsBefore, ProxyDegradation.ReportCount,
            "a live implementation degrades nothing and reports nothing");

        TestLogger.Info("[CollectedImpl] live synchronous throwing requirement returned its value");
        GC.KeepAlive(impl);
        GC.KeepAlive(host);
    }

    /// <summary>
    /// The requirement shape with no nil to hand back. Swift receives the reverse-dispatched
    /// result as a (function pointer, context) pair and wraps it into a real <c>() -&gt; Void</c>;
    /// for a NON-optional closure the zeroed pair is not a value of the type, and Swift traps on
    /// the null function pointer — which would turn a collected implementation into a process kill
    /// on the lane whose whole point is that it does not kill the process. So the degraded exit
    /// hands back the identity closure of the shape: a callable pair whose calls do nothing, the
    /// closure analogue of the calls Swift drops on a <c>nil</c> weak delegate. The fixture calls
    /// what it got back, so a pair Swift could not call would surface here.
    /// </summary>
    public void TestCollectedImplDegradesAClosureReturningRequirementToANoOpClosure()
    {
        var host = new CollectedClosureDelegateHost();
        var implRef = AssignClosureAndRetainInternally(host);

        ForceGc();

        AssertFalse(implRef.IsAlive, "the consumer's implementation is collectable once they drop it");
        AssertTrue(host.RetainsDelegateInternally, "Swift still holds its private strong reference");

        var reportsBefore = ProxyDegradation.ReportCount;
        var ticksBefore = CollectedClosureDelegateImpl.TickCalls;

        var result = host.InvokeAndFireFromRetained();

        AssertTrue(result != -1,
            "the fixture dispatched through its private strong reference, so -1 would mean the fixture lost the delegate rather than the binding degrading");
        AssertEqual(0, result,
            "Swift wrapped the degraded pair into a closure and called it — a null function pointer would have trapped inside Swift instead of returning here");
        AssertEqual(ticksBefore, CollectedClosureDelegateImpl.TickCalls,
            "the closure that came back does nothing, rather than reaching a collected implementation");
        AssertEqual(reportsBefore + 1, ProxyDegradation.ReportCount,
            "the degraded closure-returning callback reported itself exactly once");

        TestLogger.Info("[CollectedImpl] closure-returning requirement degraded to a callable no-op and reported once");
        GC.KeepAlive(host);
    }

    /// <summary>
    /// Positive control for the closure-returning requirement: while the consumer still holds
    /// their implementation, the closure Swift gets back runs the managed body, so the degraded
    /// assertion above cannot pass on a fixture whose closure never ran at all.
    /// </summary>
    public void TestLiveImplReturnsARunnableClosure()
    {
        var host = new CollectedClosureDelegateHost();
        var impl = new CollectedClosureDelegateImpl();
        host.WeakDelegate = impl;
        host.RetainDelegateInternally();

        ForceGc();

        var reportsBefore = ProxyDegradation.ReportCount;
        var ticksBefore = CollectedClosureDelegateImpl.TickCalls;

        AssertEqual(0, host.InvokeAndFireFromRetained(), "the fixture dispatched and fired the returned closure");
        AssertEqual(ticksBefore + 1, CollectedClosureDelegateImpl.TickCalls,
            "the closure Swift called reached the live implementation's body");
        AssertEqual(reportsBefore, ProxyDegradation.ReportCount,
            "a live implementation degrades nothing and reports nothing");

        TestLogger.Info("[CollectedImpl] live closure-returning requirement handed back a running closure");
        GC.KeepAlive(impl);
        GC.KeepAlive(host);
    }

    // ---- F2: the drop lands while a callback is already in flight ----------

    /// <summary>
    /// The other way a consumer-owned implementation goes missing: the callback is already running
    /// on a Swift background queue when the consumer drops it on another thread. The interleaving
    /// is pinned by the fixture's semaphores rather than by timing — Swift parks inside the
    /// callback, the consumer drops and collects, then Swift is released and makes the call — so
    /// the race is exercised on every run instead of occasionally.
    ///
    /// <para>The collection is as deterministic here as in the tests above: the parked frame is
    /// pure Swift, holding a strong reference to the conformer BOX and none to the managed
    /// implementation, and the implementation was created and dropped on a thread that has since
    /// exited. So this asserts the whole outcome rather than a survival range — the implementation
    /// is gone before the call is released, the call answers with the degraded value, and the
    /// carrier reports once. An in-flight frame must not buy the callback a live implementation
    /// the consumer no longer has, and it must not turn the degradation into a crash.</para>
    /// </summary>
    public void TestCallbackInFlightSurvivesTheConsumerDroppingTheImpl()
    {
        var host = new RaceDelegateHost();
        var implRef = AssignRaceAndRetainInternally(host);
        AssertTrue(host.RetainsDelegateInternally, "Swift privately retained the delegate before the callback started");

        host.BeginCallbackOnBackgroundQueue(23);
        host.WaitUntilCallbackStarted();

        // The callback is parked inside itself; drop and collect underneath it.
        ForceGc();

        AssertFalse(implRef.IsAlive,
            "the parked callback holds the conformer box, not the implementation, so the consumer's drop still collects it");

        var reportsBefore = ProxyDegradation.ReportCount;
        host.AllowCallbackToProceed();
        var result = host.WaitForCallbackResult();

        AssertTrue(result != -1,
            "the fixture's private strong reference was in place for the whole callback, so -1 would mean the fixture, not the binding, lost the delegate");
        AssertEqual(0, result,
            "the in-flight callback degraded to the return type's identity value rather than dispatching into a collected implementation or taking the process down");
        AssertEqual(reportsBefore + 1, ProxyDegradation.ReportCount,
            "the degraded in-flight callback reported itself, which is the only signal the consumer gets");

        TestLogger.Info($"[CollectedImpl] in-flight callback degraded to {result} after the consumer dropped the implementation");
        GC.KeepAlive(host);
    }

    // ---- Lane control: the Swift-rooted lane is untouched ------------------

    /// <summary>
    /// The control that keeps the two lanes apart. A strongly-stored existential roots its
    /// implementation by Swift liveness, so dropping the consumer's reference cannot make the
    /// implementation unresolvable: dispatch still reaches it, and nothing on that lane degrades
    /// or reports. If this ever started reporting a degradation, the consumer-owned terminal would
    /// have leaked onto the lane whose null resolve is still an invariant violation.
    /// </summary>
    public void TestStrongSlotStaysRootedAndNeverDegrades()
    {
        var harness = new ReverseInvariantHarness();
        AssignStoredAndDropImpl(harness);

        var reportsBefore = ProxyDegradation.ReportCount;
        ForceGc();

        AssertEqual(1005, harness.InvokeStored(value: 5),
            "a strongly-stored existential still dispatches after the consumer drops their reference — Swift liveness roots the implementation");
        AssertEqual(reportsBefore, ProxyDegradation.ReportCount,
            "the Swift-rooted lane never degrades: its implementation cannot be collected while Swift holds the box");

        TestLogger.Info("[CollectedImpl] Swift-rooted lane dispatched normally and reported nothing");
        GC.KeepAlive(harness);
    }

    // ---- Assignment helpers (all drop their impl on a retired thread) ------

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AssignAndRetainInternally(CollectedDelegateHost host)
    {
        WeakReference? implRef = null;
        RunOnRetiredThread(() =>
        {
            var impl = new CollectedDelegateImpl();
            host.WeakDelegate = impl;
            host.RetainDelegateInternally();
            implRef = new WeakReference(impl);
        });
        return implRef!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AssignThrowingAndRetainInternally(CollectedThrowingDelegateHost host)
    {
        WeakReference? implRef = null;
        RunOnRetiredThread(() =>
        {
            var impl = new CollectedThrowingDelegateImpl();
            host.WeakDelegate = impl;
            host.RetainDelegateInternally();
            implRef = new WeakReference(impl);
        });
        return implRef!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AssignSyncThrowingAndRetainInternally(CollectedSyncThrowingDelegateHost host)
    {
        WeakReference? implRef = null;
        RunOnRetiredThread(() =>
        {
            var impl = new CollectedSyncThrowingDelegateImpl();
            host.WeakDelegate = impl;
            host.RetainDelegateInternally();
            implRef = new WeakReference(impl);
        });
        return implRef!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AssignClosureAndRetainInternally(CollectedClosureDelegateHost host)
    {
        WeakReference? implRef = null;
        RunOnRetiredThread(() =>
        {
            var impl = new CollectedClosureDelegateImpl();
            host.WeakDelegate = impl;
            host.RetainDelegateInternally();
            implRef = new WeakReference(impl);
        });
        return implRef!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AssignRaceAndRetainInternally(RaceDelegateHost host)
    {
        WeakReference? implRef = null;
        RunOnRetiredThread(() =>
        {
            var impl = new RaceDelegateImpl();
            host.WeakDelegate = impl;
            host.RetainDelegateInternally();
            implRef = new WeakReference(impl);
        });
        return implRef!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssignStoredAndDropImpl(ReverseInvariantHarness harness)
    {
        RunOnRetiredThread(() =>
        {
            var impl = new StoredReverseDelegateImpl();
            harness.StoredDelegate = impl;
        });
    }
}

/// <summary>
/// Implementation for the three synchronous requirement shapes. The <c>didUpdate</c> counter is
/// static because the point of the degraded case is that the instance is gone by the time the
/// callback arrives — a per-instance counter would be unreadable exactly when it matters.
/// </summary>
internal sealed class CollectedDelegateImpl : IReverseCollectedDelegate
{
    private static int s_didUpdateCalls;

    internal static int DidUpdateCalls => Volatile.Read(ref s_didUpdateCalls);

    public void DidUpdate(int value) => Interlocked.Increment(ref s_didUpdateCalls);

    public int? OptionalValue(int value) => value + 3000;

    public bool ShouldProceed(int value) => value > 0;
}

/// <summary>Implementation for the throwing requirement.</summary>
internal sealed class CollectedThrowingDelegateImpl : IReverseCollectedThrowingDelegate
{
    public Task<int> ComputeAsync(int value, CancellationToken cancellationToken = default)
        => Task.FromResult(value + 4000);
}

/// <summary>
/// Implementation for the synchronous throwing requirement. It never throws: the point of the
/// fixture is what the BOUNDARY does when the implementation is gone, so an implementation that
/// threw of its own accord would make the two indistinguishable.
/// </summary>
internal sealed class CollectedSyncThrowingDelegateImpl : IReverseCollectedSyncThrowingDelegate
{
    public int ComputeNow(int value) => value + 6000;
}

/// <summary>
/// Implementation for the closure-returning requirement. The closure it vends captures nothing
/// and counts on a static, so firing it does not itself keep the implementation alive — the
/// degraded test's assertion that the counter did NOT move is about the closure Swift got back,
/// not about what the closure happens to reference.
/// </summary>
internal sealed class CollectedClosureDelegateImpl : IReverseCollectedClosureDelegate
{
    private static int s_tickCalls;

    internal static int TickCalls => Volatile.Read(ref s_tickCalls);

    public Action MakeTick() => static () => Interlocked.Increment(ref s_tickCalls);
}

/// <summary>Implementation for the in-flight race.</summary>
internal sealed class RaceDelegateImpl : IReverseRaceDelegate
{
    public int Step(int value) => value + 5000;
}
