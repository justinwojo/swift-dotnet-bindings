// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Reverse-dispatch receiver parameters that arrive in a BORROWED Swift slot: the conformance
/// copies the argument into its own local, passes that local's address, and deinitializes it as
/// soon as the receiver returns.
///
/// <para>
/// Two parameter families are unsound to read bitwise out of such a slot. Managed-wrapper value
/// types — a non-frozen struct, an associated-value enum, or an Optional of either — project to
/// C# wrapper <b>classes</b>, so a raw read reinterprets Swift's first payload word as a managed
/// object reference and faults the first time the value is used. Payload-free enums carry a
/// one-byte discriminator in Swift while their C# carrier is <c>enum : int</c>, so a four-byte
/// read reports a case the caller never passed. Both must instead be copied out of the borrowed
/// slot through the value witness, leaving the source intact.
/// </para>
///
/// <para>
/// Each test therefore asserts on <b>both sides</b> of the callback: the C# implementation must
/// see the value the driver sent, and the Swift driver's summary of the ORIGINAL — taken after
/// the receiver returned — must still be correct, which is what a receiver that consumed or
/// destroyed the borrowed source would break.
/// </para>
/// </summary>
public class BorrowedSlotParamDispatchTests : TestBase
{
    public BorrowedSlotParamDispatchTests(TestResults results) : base(results) { }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    // ---- Non-frozen struct with a class field and a String field ----

    /// <summary>
    /// The core managed-wrapper repro: a non-frozen struct mixing a class field with a String
    /// field. Describing the received value dereferences both reference fields.
    /// </summary>
    public void TestNonFrozenStructParamReceived()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveRecord(impl, name: "row", code: 17, tag: 5);

        AssertTrue(impl.RecordCalled, "onRecord(_:) fired into the C# impl");
        AssertEqual("row#17/5", impl.LastRecordSummary,
            "non-frozen struct with class + String fields read correctly out of the borrowed slot");
        AssertEqual("row#17/5", originalAfter,
            "Swift's original struct still intact after the receiver returned");
        GC.KeepAlive(impl);
    }

    // ---- Associated-value enum (class-payload and struct-payload cases) ----

    /// <summary>Associated-value enum, class-payload case.</summary>
    public void TestAssociatedValueEnumParamReceived_ClassPayload()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveTrackedItem(impl, tag: 9);

        AssertTrue(impl.ItemCalled, "onItem(_:) fired into the C# impl");
        AssertEqual("tracked/9", impl.LastItemSummary,
            "class-payload enum case read correctly out of the borrowed slot");
        AssertEqual("tracked/9", originalAfter, "Swift's original enum still intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>Associated-value enum, struct-payload case (a wrapper nested in a wrapper).</summary>
    public void TestAssociatedValueEnumParamReceived_StructPayload()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveRecordItem(impl, name: "cell", code: 3, tag: 8);

        AssertTrue(impl.ItemCalled, "onItem(_:) fired into the C# impl");
        AssertEqual("record/cell#3/8", impl.LastItemSummary,
            "struct-payload enum case read correctly out of the borrowed slot");
        AssertEqual("record/cell#3/8", originalAfter, "Swift's original enum still intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>Associated-value enum, payload-free case — no reference to copy, but the same carrier.</summary>
    public void TestAssociatedValueEnumParamReceived_PayloadFreeCase()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveBlankItem(impl);

        AssertTrue(impl.ItemCalled, "onItem(_:) fired into the C# impl");
        AssertEqual("blank", impl.LastItemSummary, "payload-free case of an associated-value enum");
        AssertEqual("blank", originalAfter, "Swift's original enum still intact after the callback");
        GC.KeepAlive(impl);
    }

    // ---- Payload-free one-byte enum ----

    /// <summary>
    /// Every case of a one-byte payload-free enum. A discriminator read at the C# carrier's
    /// four-byte width reports whatever follows the slot, so each case has to be checked — the
    /// first case is the one a stray zero would still get right by accident.
    /// </summary>
    public void TestPayloadFreeEnumParamReceived()
    {
        var driver = new BorrowedSlotDriver();

        AssertEnumCaseRoundTrips(driver, BorrowedSlotKind.Alpha, "alpha");
        AssertEnumCaseRoundTrips(driver, BorrowedSlotKind.Beta, "beta");
        AssertEnumCaseRoundTrips(driver, BorrowedSlotKind.Gamma, "gamma");
    }

    private void AssertEnumCaseRoundTrips(BorrowedSlotDriver driver, BorrowedSlotKind kind, string expected)
    {
        var impl = new BorrowedSlotReceiverImpl();

        var originalAfter = driver.DriveKind(impl, kind: kind);

        AssertTrue(impl.KindCalled, $"onKind(_:) fired into the C# impl for {expected}");
        AssertEqual(kind, impl.LastKind, $"one-byte enum discriminator '{expected}' read at the Swift width");
        AssertEqual(expected, impl.LastKindSummary,
            $"received '{expected}' case still names its own case when handed back to Swift");
        AssertEqual(expected, originalAfter, $"Swift's original '{expected}' case intact after the callback");
        GC.KeepAlive(impl);
    }

    // ---- Optionals of the two managed-wrapper shapes ----

    /// <summary>Optional non-frozen struct, non-nil.</summary>
    public void TestOptionalNonFrozenStructParamReceived_NonNil()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveOptionalRecord(impl, name: "opt", code: 4, tag: 2);

        AssertTrue(impl.OptionalRecordCalled, "onOptionalRecord(_:) fired into the C# impl");
        AssertTrue(impl.LastOptionalRecordPresent, "optional struct delivered non-nil");
        AssertEqual("opt#4/2", impl.LastOptionalRecordSummary,
            "Optional<non-frozen struct> read correctly out of the borrowed slot");
        AssertEqual("opt#4/2", originalAfter, "Swift's original optional struct intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional non-frozen struct, nil — the case that must not fault on the tag read.</summary>
    public void TestOptionalNonFrozenStructParamReceived_Nil()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        driver.DriveNilRecord(impl);

        AssertTrue(impl.OptionalRecordCalled, "onOptionalRecord(_:) fired into the C# impl");
        AssertFalse(impl.LastOptionalRecordPresent, "optional struct delivered nil");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional associated-value enum, non-nil.</summary>
    public void TestOptionalAssociatedValueEnumParamReceived_NonNil()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        var originalAfter = driver.DriveOptionalItem(impl, tag: 6);

        AssertTrue(impl.OptionalItemCalled, "onOptionalItem(_:) fired into the C# impl");
        AssertTrue(impl.LastOptionalItemPresent, "optional enum delivered non-nil");
        AssertEqual("tracked/6", impl.LastOptionalItemSummary,
            "Optional<associated-value enum> read correctly out of the borrowed slot");
        AssertEqual("tracked/6", originalAfter, "Swift's original optional enum intact after the callback");
        GC.KeepAlive(impl);
    }

    /// <summary>Optional associated-value enum, nil.</summary>
    public void TestOptionalAssociatedValueEnumParamReceived_Nil()
    {
        var impl = new BorrowedSlotReceiverImpl();
        var driver = new BorrowedSlotDriver();

        driver.DriveNilItem(impl);

        AssertTrue(impl.OptionalItemCalled, "onOptionalItem(_:) fired into the C# impl");
        AssertFalse(impl.LastOptionalItemPresent, "optional enum delivered nil");
        GC.KeepAlive(impl);
    }

    // ---- ARC balance across repeated dispatch ----

    /// <summary>
    /// The copy-out must take exactly one independent reference that the C# wrapper releases when
    /// it drains. Over-retaining strands one tracked payload per callback; under-retaining frees
    /// storage the Swift original still owns, which the driver's post-callback read would have
    /// caught above. Either way the live count must return to zero.
    /// </summary>
    public void TestBorrowedSlotParamRepeatedDispatchDoesNotLeak()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        // Finish the allocating/callback thread before observing collectability. A no-inline
        // call on the still-live test thread does not establish this prerequisite.
        var observed = RunOnFinishedThread(() => DriveRecordCallbacks(200));
        AssertEqual("iter#199/1", observed.received,
            "the final copied-out callback value remains readable");
        AssertEqual("iter#199/1", observed.original,
            "the final Swift original remains readable after its callback");

        // Cardinality, not just the last value: a regression that dropped earlier callbacks while
        // still delivering iteration 199 would satisfy every assertion above, and a balanced ledger
        // over fewer payloads than intended is a leak probe that passed for the wrong reason.
        AssertEqual(200, observed.callbacks, "every one of the 200 dispatches reached the C# receiver");
        DrainFinalizers();

        // The weak observations are read only AFTER the leak assertion has run its full drain.
        // Resolving a weak reference puts the target in this frame, where Mono's conservative stack
        // scan can pin it, so touching them first could keep alive the very wrapper the drain was
        // about to reclaim. Once the ledger has already failed, that no longer matters — and the
        // readout is what separates the causes: a still-live wrapper means a managed root; a dead
        // wrapper over a still-live SafeHandle means the handle itself is rooted; both dead while
        // the Swift registry still names a survivor means the native copy/destroy ledger, not
        // managed reachability, is short a release.
        try
        {
            LifetimeTracker.AssertNoLeaks(
                "repeated non-frozen-struct reverse-callback must not leak the copied-out payload");
        }
        catch (AssertionException ex)
        {
            throw new AssertionException(
                $"{ex.Message} {SummarizeObservations(observed.observations, observed.scopeState)}", ex);
        }

        var stats = LifetimeTracker.GetStats();
        AssertEqual(200, stats.allocations, "exactly 200 native payloads allocated");
        AssertEqual(200, stats.deallocations, "all 200 native payloads deallocated");
        AssertEqual(0, stats.live, "no copied-out native payload remains live");
        TestLogger.Memory("borrowed-slot repeated dispatch: "
            + SummarizeObservations(observed.observations, observed.scopeState));
        TestLogger.Info("borrowed-slot struct reverse-callback: 200 payloads copied out and released");
    }

    /// <summary>
    /// Positive control for the observation method used by the repeated-dispatch leak probe: it
    /// deliberately keeps ONE copied-out payload rooted from a static field and requires every
    /// channel to report that owner — the Swift registry names exactly one survivor in the
    /// borrowed-slot category, and that iteration's weak wrapper AND weak SafeHandle are both
    /// still alive after the allocating thread ended and the collections ran. Without this, a
    /// clean readout on the leak probe would be ambiguous: "nothing observed" could equally mean
    /// "no owner" or "the observation cannot see an owner". Releasing the root then has to drain
    /// the payload, so the control is not merely asserting that a rooted object stays alive.
    /// </summary>
    public void TestBorrowedSlotLeakObservationSeesADeliberatelyRootedPayload()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();
        s_deliberateRoot = null;

        const int iterations = 20;
        const int rootedIteration = 7;
        try
        {
            var observed = RunOnFinishedThread(() => DriveRecordCallbacksRootingOne(iterations, rootedIteration));
            AssertEqual($"iter#{iterations - 1}/1", observed.received,
                "the final copied-out callback value remains readable");
            AssertEqual(iterations, observed.callbacks,
                $"every one of the {iterations} dispatches reached the C# receiver");
            AssertEqual(iterations, observed.observations.Count,
                "one weak observation was taken per callback");
            DrainFinalizers();

            // Check the root through a thread that then ends, so the test thread's own frame never
            // holds the wrapper reference — a conservative stack scan over it would pin the payload
            // and make the release half below unfalsifiable.
            AssertTrue(RunOnFinishedThread(() => s_deliberateRoot is not null),
                "the control kept its chosen callback payload rooted");
            AssertEqual(iterations, LifetimeTracker.GetStats().allocations,
                $"exactly {iterations} native payloads allocated");

            // Assert the live count through the tracker's own quiescence path rather than a raw
            // snapshot: it only ever drains a transient OVER-count down toward the target, so it
            // cannot erode the root this control is holding, and its failure message already names
            // the survivors — which is the whole point of the probe this controls for.
            LifetimeTracker.AssertLiveCount(1, "exactly the one deliberately rooted payload is still live");

            // The Swift registry must NAME that survivor, not just count it: allocation order is
            // 1-based, so the rooted iteration reports allocOrder = rootedIteration + 1.
            var identities = LifetimeTracker.DescribeLiveIdentities();
            TestLogger.Memory($"borrowed-slot rooted-payload control: {identities}");
            AssertTrue(identities.Contains("category=BorrowedSlotRef"),
                $"the live survivor is named by category, not just counted (got: {identities})");
            AssertTrue(identities.Contains($"allocOrder={rootedIteration + 1}"),
                $"the named survivor is the iteration the control rooted (got: {identities})");

            // And the weak channels must see the same owner, so a dead-weak readout on the leak
            // probe is a real absence of a managed owner rather than a blind instrument.
            var control = observed.observations[rootedIteration];
            AssertTrue(control.WrapperAlive, "the weak wrapper observation reports the rooted wrapper alive");
            AssertTrue(control.HandleAlive, "the weak SafeHandle observation reports the rooted handle alive");
            TestLogger.Memory("borrowed-slot rooted-payload control: "
                + SummarizeObservations(observed.observations, observed.scopeState));

            // Release the one root and require the payload to drain. Disposal is explicit so this
            // half is decided by the ownership ledger rather than by collection timing: an "alive"
            // readout over something that was in fact already unowned would leave the live count at
            // zero here while the assertions above still demanded a survivor, so the two halves
            // cannot both pass on a blind instrument. The post-release weak readout is logged rather
            // than asserted — whether a dead wrapper has also been collected yet is exactly the
            // GC-timing question this fixture must not decide by luck; the leak probe's own green
            // runs are where the weak channel is seen reporting dead.
            ReleaseDeliberateRoot();
            DrainFinalizers();

            LifetimeTracker.AssertNoLeaks("releasing the deliberately rooted payload must drain it");
            TestLogger.Memory("borrowed-slot rooted-payload control after release: "
                + SummarizeObservations(observed.observations, observed.scopeState));
            TestLogger.Info("borrowed-slot observation control: one rooted payload named by the registry, "
                + "seen by both weak channels, and drained on release");
        }
        finally
        {
            // A static root that survives a failing assertion would follow this class out into
            // every later test: the next tracker reset would spend its whole quiescence timeout
            // trying to drain a payload this control is still holding. Releasing it here costs
            // nothing on the passing path, where the field is already null.
            ReleaseDeliberateRoot();
        }
    }

    // Drop the control's strong root from a thread that then ends, so the test thread's own frame
    // never held the wrapper — the same reason the root lives in a static rather than a local.
    private static void ReleaseDeliberateRoot()
    {
        if (s_deliberateRoot is null)
            return;
        RunOnFinishedThread(() =>
        {
            s_deliberateRoot!.Dispose();
            s_deliberateRoot = null;
        });
    }

    // The deliberate strong root of the positive control. A static field, not a local, because a
    // local on the test thread's frame could be kept alive by a conservative stack scan after it
    // is cleared — which would make the release half of the control unfalsifiable.
    private static BorrowedSlotRecord? s_deliberateRoot;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string received, string original, int callbacks,
        List<BorrowedSlotObservation> observations, string scopeState)
        DriveRecordCallbacks(int n)
    {
        var impl = new BorrowedSlotReceiverImpl { RecordObservations = new List<BorrowedSlotObservation>(n) };
        var driver = new BorrowedSlotDriver();
        var original = driver.DriveRecordRepeatedly(impl, iterations: n, tag: 1);
        var received = impl.LastRecordSummary;
        var callbacks = impl.RecordCallCount;
        var observations = impl.RecordObservations!;
        // An ambient dispose scope would root every wrapper it registered, so record whether one
        // was active on the callback thread rather than inferring it from the counts afterwards.
        var scopeState = SwiftDisposeScope.Current is null ? "none" : "ACTIVE";
        GC.KeepAlive(impl);
        // Only managed text, an integer count and weak observations cross back to the test thread;
        // none of them roots a callback payload.
        return (received, original, callbacks, observations, scopeState);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string received, string original, int callbacks,
        List<BorrowedSlotObservation> observations, string scopeState)
        DriveRecordCallbacksRootingOne(int n, int rootedIteration)
    {
        var impl = new BorrowedSlotReceiverImpl
        {
            RecordObservations = new List<BorrowedSlotObservation>(n),
            RootRecordAtIndex = rootedIteration,
        };
        var driver = new BorrowedSlotDriver();
        var original = driver.DriveRecordRepeatedly(impl, iterations: n, tag: 1);
        var received = impl.LastRecordSummary;
        var callbacks = impl.RecordCallCount;
        var observations = impl.RecordObservations!;
        var scopeState = SwiftDisposeScope.Current is null ? "none" : "ACTIVE";
        // Hand the chosen payload to a static field so the root outlives this thread; that is the
        // owner the control requires every observation channel to report.
        s_deliberateRoot = impl.RootedRecord;
        GC.KeepAlive(impl);
        return (received, original, callbacks, observations, scopeState);
    }

    // Collapse the per-callback weak observations into one line: how many wrappers and handles are
    // still alive and, when few, which iterations they were. Called only after the collections, and
    // it never holds a resolved target across one.
    private static string SummarizeObservations(List<BorrowedSlotObservation> observations, string scopeState)
    {
        var liveWrappers = new List<int>();
        var liveHandles = new List<int>();
        foreach (var observation in observations)
        {
            if (observation.WrapperAlive)
                liveWrappers.Add(observation.Index);
            if (observation.HandleAlive)
                liveHandles.Add(observation.Index);
        }

        const int cap = 8;
        return $"weak observations over {observations.Count} callbacks: "
            + $"liveWrappers={liveWrappers.Count}{DescribeIndices(liveWrappers, cap)}, "
            + $"liveHandles={liveHandles.Count}{DescribeIndices(liveHandles, cap)}; "
            + $"ambient dispose scope on the callback thread: {scopeState}";
    }

    private static string DescribeIndices(List<int> indices, int cap)
    {
        if (indices.Count == 0)
            return "";
        var listed = string.Join(",", indices.Take(cap));
        return indices.Count > cap ? $" (iterations {listed},…)" : $" (iterations {listed})";
    }
}

/// <summary>
/// One callback's weak observation: the copied-out wrapper and the SafeHandle that owns its native
/// buffer, each held only weakly so the observation itself cannot be the thing keeping the payload
/// alive. The two are separate channels on purpose — a collected wrapper whose SafeHandle is still
/// live is a rooted handle, which is a different fault from a rooted wrapper, and a single "is it
/// gone?" flag cannot tell them apart.
/// </summary>
internal sealed class BorrowedSlotObservation
{
    private readonly WeakReference<BorrowedSlotRecord> _wrapper;
    private readonly WeakReference<SwiftSafeHandle<BorrowedSlotRecord>> _handle;

    internal BorrowedSlotObservation(int index, BorrowedSlotRecord record)
    {
        Index = index;
        _wrapper = new WeakReference<BorrowedSlotRecord>(record);
        _handle = new WeakReference<SwiftSafeHandle<BorrowedSlotRecord>>(record.Payload);
    }

    /// <summary>Zero-based callback iteration this observation was taken on.</summary>
    internal int Index { get; }

    // Only the boolean escapes: the resolved target stays in the discard for the duration of the
    // call, so reading an observation cannot root what it observes for a later collection.
    internal bool WrapperAlive => _wrapper.TryGetTarget(out _);

    internal bool HandleAlive => _handle.TryGetTarget(out _);
}

/// <summary>
/// C# implementation of the generated <c>IBorrowedSlotReceiver</c>. Each callback hands the value
/// straight back to Swift for description, which is what proves the copy-out produced a usable
/// value rather than a reinterpreted word — and it deliberately keeps no reference to the payload
/// afterwards, so the leak test can watch the wrappers drain. The one exception is opt-in and
/// belongs to the observation control: <see cref="RootRecordAtIndex"/> keeps a single named
/// callback's payload, so a probe can show that a real owner is reported rather than missed.
/// </summary>
internal sealed class BorrowedSlotReceiverImpl : IBorrowedSlotReceiver
{
    public bool RecordCalled { get; private set; }
    public bool ItemCalled { get; private set; }
    public bool KindCalled { get; private set; }
    public bool OptionalRecordCalled { get; private set; }
    public bool OptionalItemCalled { get; private set; }

    public string LastRecordSummary { get; private set; } = "";
    public string LastItemSummary { get; private set; } = "";
    public string LastKindSummary { get; private set; } = "";
    public string LastOptionalRecordSummary { get; private set; } = "";
    public string LastOptionalItemSummary { get; private set; } = "";

    public BorrowedSlotKind LastKind { get; private set; }
    public bool LastOptionalRecordPresent { get; private set; }
    public bool LastOptionalItemPresent { get; private set; }

    /// <summary>
    /// Optional weak-observation sink for the repeated-dispatch leak probe. Null for the semantic
    /// cases, which have nothing to observe. Holding weak references only, the sink never becomes
    /// the reference that keeps a copied-out payload alive.
    /// </summary>
    internal List<BorrowedSlotObservation>? RecordObservations { get; init; }

    /// <summary>
    /// Zero-based callback index whose payload the positive control keeps strongly referenced;
    /// negative (the default) keeps no reference at all, which is what the leak probe needs.
    /// </summary>
    internal int RootRecordAtIndex { get; init; } = -1;

    /// <summary>The payload named by <see cref="RootRecordAtIndex"/>, for the positive control.</summary>
    internal BorrowedSlotRecord? RootedRecord { get; private set; }

    private int _recordCalls;

    /// <summary>
    /// How many times <see cref="OnRecord"/> actually ran. A repeated-dispatch probe that checks
    /// only the LAST delivered value cannot tell a full run from one that dropped earlier callbacks
    /// and still delivered the final iteration, and a leak ledger over fewer callbacks than intended
    /// balances for the wrong reason. Counting is independent of the observation sink, so it holds
    /// for the semantic cases too, and it retains nothing.
    /// </summary>
    internal int RecordCallCount => _recordCalls;

    public void OnRecord(BorrowedSlotRecord record)
    {
        RecordCalled = true;
        LastRecordSummary = TestLibFunctions.DescribeBorrowedRecord(record);

        var index = _recordCalls++;
        RecordObservations?.Add(new BorrowedSlotObservation(index, record));
        if (index == RootRecordAtIndex)
            RootedRecord = record;
    }

    public void OnItem(BorrowedSlotItem item)
    {
        ItemCalled = true;
        LastItemSummary = TestLibFunctions.DescribeBorrowedItem(item);
    }

    public void OnKind(BorrowedSlotKind kind)
    {
        KindCalled = true;
        LastKind = kind;
        LastKindSummary = TestLibFunctions.DescribeBorrowedKind(kind);
    }

    public void OnOptionalRecord(BorrowedSlotRecord? record)
    {
        OptionalRecordCalled = true;
        LastOptionalRecordPresent = record != null;
        LastOptionalRecordSummary = TestLibFunctions.DescribeOptionalBorrowedRecord(record);
    }

    public void OnOptionalItem(BorrowedSlotItem? item)
    {
        OptionalItemCalled = true;
        LastOptionalItemPresent = item != null;
        LastOptionalItemSummary = TestLibFunctions.DescribeOptionalBorrowedItem(item);
    }
}
