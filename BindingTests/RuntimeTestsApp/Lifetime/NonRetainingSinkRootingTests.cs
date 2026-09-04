// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// Lifetime invariants for assigning a C# protocol implementation into Swift storage that
/// does NOT retain — a <c>weak</c> or <c>unowned</c> stored property, the shape every Apple
/// framework delegate uses.
///
/// <para>
/// A protocol value crossing into Swift is carried by a conformer box whose construction
/// <c>+1</c> belongs to the managed carrier. A strong sink is safe because Swift's own
/// store-retain keeps the box alive. A non-retaining sink takes no such retain, so the
/// carrier has to follow something on the managed side instead — and the only object whose
/// lifetime matches what the Swift declaration promises is the consumer's own implementation
/// object. The rule under test:
/// </para>
///
/// <list type="bullet">
/// <item><b>Follows the implementation</b> — while the consumer holds their implementation,
/// the Swift slot reads non-nil and dispatch reaches the implementation, across any number
/// of collections.</item>
/// <item><b>Dies with the implementation</b> — once the consumer drops it, the carrier is
/// collected with it and the Swift slot goes nil, which is exactly what <c>weak</c> means.
/// A receiver the consumer still holds does not extend that.</item>
/// <item><b>Roots nothing</b> — an implementation that references the receiver peer (the
/// ordinary delegate shape) leaves no residue once the consumer drops both.</item>
/// </list>
///
/// <para>
/// The sentinels make a lost carrier observable as a wrong value: a live delegate returns
/// <c>value + 2000</c>, a collected one returns <c>-1</c>.
/// </para>
/// </summary>
public class NonRetainingSinkRootingTests : TestBase
{
    public NonRetainingSinkRootingTests(TestResults results) : base(results) { }

    private const int GcCycles = 6;

    /// <summary>
    /// Force a GC on a worker thread (the main thread blocks on Join with a minimal live-local
    /// footprint) so Mono's conservative stack scan does not pin the dropped carrier. Mirrors
    /// <see cref="ReverseDispatchInvariantTests"/>'s helper.
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

    // ---- The carrier follows the consumer's implementation ------------------

    /// <summary>
    /// Assign a C# implementation into a <c>weak</c> Swift property, drop the carrier the
    /// setter minted (the consumer never sees it), collect, and prove the delegate is still
    /// there while the consumer holds the implementation: the Swift property reads non-nil,
    /// Swift-side dispatch reaches the C# implementation, and reading the property back vends
    /// a working carrier.
    /// </summary>
    public void TestWeakSinkSurvivesGcWhileConsumerHoldsImpl()
    {
        var harness = new ReverseWeakSinkHarness();
        var impl = new WeakSinkDelegateImpl();
        AssignWeak(harness, impl);

        // Everything the setter allocated internally is now unreferenced by the test; only the
        // consumer's own `impl` reference is left.
        ForceGc();

        AssertTrue(harness.HasWeakDelegate,
            "weak Swift storage still reads non-nil after a full collection");
        AssertEqual(2005, harness.InvokeWeak(value: 5),
            "Swift dispatches into the C# implementation through the weak sink (5 + 2000)");

        var readBack = harness.WeakDelegate;
        AssertNotNull(readBack, "weak property re-vends a non-null carrier");
        AssertEqual(2007, readBack!.WeakValue(7),
            "the re-vended carrier round-trips a value (7 + 2000)");

        TestLogger.Info("[NonRetainingSink] weak sink survived GC while the consumer held the implementation");
        GC.KeepAlive(impl);
        GC.KeepAlive(harness);
    }

    /// <summary>
    /// The <c>unowned</c> flavour of the same shape. Read back only while the conformer is
    /// known live — a dangling <c>unowned</c> traps by design, so the assertion that matters
    /// is that it is NOT dangling while the consumer holds the implementation.
    /// </summary>
    public void TestUnownedSinkSurvivesGcWhileConsumerHoldsImpl()
    {
        var harness = new ReverseWeakSinkHarness();
        var impl = new WeakSinkDelegateImpl();
        AssignUnowned(harness, impl);

        ForceGc();

        AssertEqual(2011, harness.InvokeUnowned(value: 11),
            "Swift dispatches into the C# implementation through the unowned sink (11 + 2000)");

        TestLogger.Info("[NonRetainingSink] unowned sink survived GC while the consumer held the implementation");
        GC.KeepAlive(impl);
        GC.KeepAlive(harness);
    }

    /// <summary>
    /// Repeated assignment of the same implementation must reuse one carrier rather than mint
    /// a Swift box per assignment: the census reading has to be flat across the loop, and the
    /// slot has to still dispatch afterwards.
    /// </summary>
    public void TestWeakSinkRepeatedAssignmentReusesOneCarrier()
    {
        var harness = new ReverseWeakSinkHarness();
        var impl = new WeakSinkDelegateImpl();

        AssignWeak(harness, impl);
        ForceGc();
        var afterFirst = SwiftLeakCensus.Report().ProxyImplRoots;

        for (int i = 0; i < 8; i++)
            AssignWeak(harness, impl);
        ForceGc();
        var afterMany = SwiftLeakCensus.Report().ProxyImplRoots;

        AssertTrue(afterMany <= afterFirst,
            $"re-assigning the same implementation does not accumulate carriers (first={afterFirst}, after 8 more={afterMany})");
        AssertEqual(2013, harness.InvokeWeak(value: 13),
            "the reused carrier still dispatches (13 + 2000)");

        TestLogger.Info($"[NonRetainingSink] repeated weak assignment: first={afterFirst}, afterMany={afterMany}");
        GC.KeepAlive(impl);
        GC.KeepAlive(harness);
    }

    // ---- The carrier dies with the implementation ---------------------------

    /// <summary>
    /// The half a receiver-anchored design gets wrong. The consumer drops the implementation
    /// while still holding the receiver: <c>weak</c> means Swift promised not to keep the
    /// delegate alive, so the slot must go nil, dispatch must report the nil sentinel rather
    /// than reaching a dead object, and none of it may fault.
    /// </summary>
    public void TestWeakSinkGoesNilWhenConsumerDropsImplButKeepsReceiver()
    {
        var harness = new ReverseWeakSinkHarness();
        var implRef = AssignWeakAndDropImpl(harness);

        ForceGc();

        AssertFalse(implRef.IsAlive, "the implementation is collectable once the consumer drops it");
        AssertFalse(harness.HasWeakDelegate,
            "weak Swift storage reads nil after the consumer dropped the implementation");
        AssertEqual(-1, harness.InvokeWeak(value: 5),
            "dispatch through the emptied weak sink reports the nil sentinel instead of touching a dead box");
        AssertNull(harness.WeakDelegate, "the emptied weak property vends null");

        TestLogger.Info("[NonRetainingSink] weak sink nilled when the consumer dropped the implementation");
        GC.KeepAlive(harness);
    }

    /// <summary>
    /// Assigns a freshly-created implementation into the receiver's weak sink and returns only
    /// a weak handle to it, so nothing in the caller's frame keeps it alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AssignWeakAndDropImpl(ReverseWeakSinkHarness harness)
    {
        WeakReference? implRef = null;
        RunOnRetiredThread(() =>
        {
            var impl = new WeakSinkDelegateImpl();
            harness.WeakDelegate = impl;
            implRef = new WeakReference(impl);
        });
        return implRef!;
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a worker thread and waits for it to exit, so every
    /// reference the action touched dies with a stack frame that no longer exists when the
    /// collection runs. Mono scans thread stacks conservatively: a dead slot left behind in a
    /// still-live frame reads as a root, which would pin the very carrier under test.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunOnRetiredThread(Action action)
    {
        var worker = new System.Threading.Thread(() => action()) { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    /// <summary>
    /// The graph a receiver-anchored root leaks: the implementation references the receiver
    /// peer it was assigned to — the ordinary delegate shape, where a view controller's
    /// delegate holds the controller. Everything here is managed and cyclic, so once the
    /// consumer lets go of both, the collector is free to take the whole cycle. If the carrier
    /// were reachable from the receiver, the receiver would be reachable from the
    /// implementation, and neither would ever be collected.
    /// </summary>
    public void TestWeakSinkImplHoldingReceiverLeavesNoResidue()
    {
        // Drain first: an earlier test's carrier still on the finalization queue would otherwise
        // be counted in the baseline and freed before the live reading, hiding this test's own box.
        ForceGc();
        var baseline = SwiftLeakCensus.Report().ProxyImplRoots;

        var (implRef, harnessRef, whileAlive) = AssignCyclicPairAndDrop();
        AssertTrue(whileAlive > baseline,
            $"the conformer box exists while the pair is alive (baseline={baseline}, live={whileAlive})");

        ForceGc();

        AssertFalse(implRef.IsAlive, "the implementation is collected even though it references the receiver");
        AssertFalse(harnessRef.IsAlive, "the receiver is collected even though its Swift slot referenced the implementation");

        var after = SwiftLeakCensus.Report().ProxyImplRoots;
        AssertTrue(after <= baseline,
            $"no conformer box outlives the pair (baseline={baseline}, after={after})");

        TestLogger.Info($"[NonRetainingSink] impl<->receiver cycle: baseline={baseline}, live={whileAlive}, after={after}");
    }

    /// <summary>
    /// Builds the implementation/receiver cycle, assigns it into the weak sink, measures the
    /// census while both are alive, and returns weak handles to both. Both strong references
    /// fall out of scope on return, which is the condition under test.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Impl, WeakReference Harness, int WhileAlive) AssignCyclicPairAndDrop()
    {
        var harness = new ReverseWeakSinkHarness();
        var impl = new ReceiverHoldingWeakSinkDelegateImpl(harness);
        harness.WeakDelegate = impl;

        ForceGc();
        var whileAlive = SwiftLeakCensus.Report().ProxyImplRoots;

        GC.KeepAlive(impl);
        GC.KeepAlive(harness);
        return (new WeakReference(impl), new WeakReference(harness), whileAlive);
    }

    /// <summary>
    /// The <c>unowned</c> counterpart to the drop case. A dangling <c>unowned</c> slot traps
    /// on read by design, so nothing here reads it back: the receiver is dropped along with
    /// the implementation and the assertion is purely that neither leaves residue.
    /// </summary>
    public void TestUnownedSinkLeavesNoResidueWhenConsumerDropsEverything()
    {
        ForceGc();
        var baseline = SwiftLeakCensus.Report().ProxyImplRoots;

        var whileAlive = AssignUnownedAndDropEverything();
        AssertTrue(whileAlive > baseline,
            $"the conformer box exists while the pair is alive (baseline={baseline}, live={whileAlive})");

        ForceGc();

        var after = SwiftLeakCensus.Report().ProxyImplRoots;
        AssertTrue(after <= baseline,
            $"no conformer box outlives the unowned assignment (baseline={baseline}, after={after})");

        TestLogger.Info($"[NonRetainingSink] unowned drop: baseline={baseline}, live={whileAlive}, after={after}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AssignUnownedAndDropEverything()
    {
        var harness = new ReverseWeakSinkHarness();
        var impl = new WeakSinkDelegateImpl();
        harness.UnownedDelegate = impl;

        ForceGc();
        var whileAlive = SwiftLeakCensus.Report().ProxyImplRoots;
        GC.KeepAlive(impl);
        GC.KeepAlive(harness);
        return whileAlive;
    }

    /// <summary>
    /// Assigning null clears the Swift storage, and the carrier the previous assignment minted
    /// has nothing else holding it once the consumer drops the implementation too.
    /// </summary>
    public void TestWeakSinkClearedByNullAssignment()
    {
        ForceGc();
        var baseline = SwiftLeakCensus.Report().ProxyImplRoots;

        var harness = new ReverseWeakSinkHarness();
        var whileAlive = AssignWeakThenClearAndDrop(harness);
        AssertTrue(whileAlive > baseline,
            $"the conformer box exists after the assignment (baseline={baseline}, live={whileAlive})");

        ForceGc();

        AssertFalse(harness.HasWeakDelegate, "weak Swift storage reads nil after a null assignment");
        AssertEqual(-1, harness.InvokeWeak(value: 5), "dispatch through a cleared weak sink reports the nil sentinel");
        var after = SwiftLeakCensus.Report().ProxyImplRoots;
        AssertTrue(after <= baseline,
            $"nothing outlives the cleared assignment (baseline={baseline}, after={after})");

        TestLogger.Info($"[NonRetainingSink] null assignment cleared the weak sink: baseline={baseline}, live={whileAlive}, after={after}");
        GC.KeepAlive(harness);
    }

    // ---- A consumer-constructed carrier keeps its own lifetime --------------

    /// <summary>
    /// When the consumer builds the carrier themselves and assigns that, the value crossing
    /// into Swift is a carrier they already hold — so their reference to the carrier, not to
    /// the implementation behind it, is what decides how long the weak slot stays populated.
    /// Holding the carrier keeps the slot live even though the implementation reference is
    /// long gone.
    /// </summary>
    public void TestWeakSinkWithConsumerConstructedCarrierFollowsTheCarrier()
    {
        var harness = new ReverseWeakSinkHarness();
        var carrier = BuildAndAssignCarrierDroppingImplReference(harness);

        ForceGc();

        AssertTrue(harness.HasWeakDelegate,
            "weak Swift storage stays populated while the consumer holds the carrier they built");
        AssertEqual(2017, harness.InvokeWeak(value: 17),
            "dispatch through the consumer-built carrier reaches the implementation (17 + 2000)");

        TestLogger.Info("[NonRetainingSink] consumer-constructed carrier held the weak slot open");
        GC.KeepAlive(carrier);
        GC.KeepAlive(harness);
    }

    /// <summary>
    /// Builds the proxy explicitly, assigns it, and returns only the proxy — the caller never
    /// sees the implementation, so the carrier's own strong reference to it is the only one.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReverseWeakDelegateProxy BuildAndAssignCarrierDroppingImplReference(ReverseWeakSinkHarness harness)
    {
        var carrier = new ReverseWeakDelegateProxy(new WeakSinkDelegateImpl());
        harness.WeakDelegate = carrier;
        return carrier;
    }

    // ---- Strong sink: read-back carrier finalization is still safe ----------

    /// <summary>
    /// Reading a class-bound stored existential back mints a fresh managed carrier that owns
    /// a reference into the same conformer box. Dropping and finalizing that carrier must not
    /// disturb the value the Swift property still holds — the strong-sink counterpart to the
    /// rule above, and the shape most likely to over-release if the two lanes were confused.
    /// </summary>
    public void TestStoredExistentialReadBackCarrierFinalizesSafely()
    {
        var harness = new ReverseInvariantHarness();
        var impl = new StoredReverseDelegateImpl();
        harness.StoredDelegate = impl;

        ReadBackAndDropCarrier(harness);
        ForceGc();

        AssertEqual(1005, harness.InvokeStored(value: 5),
            "the strongly-stored existential still dispatches after a read-back carrier was finalized");
        AssertNotNull(harness.StoredDelegate, "the strongly-stored existential still re-vends a carrier");

        TestLogger.Info("[NonRetainingSink] read-back carrier finalization left the stored existential intact");
        GC.KeepAlive(impl);
        GC.KeepAlive(harness);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadBackAndDropCarrier(ReverseInvariantHarness harness)
    {
        var carrier = harness.StoredDelegate;
        GC.KeepAlive(carrier);
        // carrier falls out of scope; its finalizer must not release anything Swift still owns.
    }

    /// <summary>
    /// Assigns a fresh implementation into the weak sink, samples the census while the consumer
    /// still holds that implementation, then clears the slot and drops the implementation — all
    /// inside a worker thread that has exited before the caller collects, so no dead stack slot
    /// pins the carrier the assertion is about. Returns the census reading taken while the
    /// implementation was alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AssignWeakThenClearAndDrop(ReverseWeakSinkHarness harness)
    {
        var whileAlive = 0;
        RunOnRetiredThread(() =>
        {
            var impl = new WeakSinkDelegateImpl();
            harness.WeakDelegate = impl;

            ForceGc();
            whileAlive = SwiftLeakCensus.Report().ProxyImplRoots;

            harness.WeakDelegate = null;
            GC.KeepAlive(impl);
        });
        return whileAlive;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssignWeak(ReverseWeakSinkHarness harness, IReverseWeakDelegate impl)
    {
        harness.WeakDelegate = impl;
        // The carrier the setter minted is unreferenced from here on: only the consumer's own
        // reference to `impl` keeps the conformer box alive.
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssignUnowned(ReverseWeakSinkHarness harness, IReverseWeakDelegate impl)
    {
        harness.UnownedDelegate = impl;
    }
}

/// <summary>
/// Conformer for the non-retaining sinks. Deliberately a different type from the strong-sink
/// conformer so the two storage flavours can never alias one another.
/// </summary>
internal sealed class WeakSinkDelegateImpl : IReverseWeakDelegate
{
    public int WeakValue(int value) => value + 2000;
}

/// <summary>
/// The ordinary delegate shape: the implementation keeps a reference to the receiver it was
/// assigned to, the way a delegate object usually keeps its owner. Combined with the receiver's
/// weak slot referencing the implementation, this closes a purely-managed cycle that the
/// collector can only take if nothing anchors either end to a longer-lived root.
/// </summary>
internal sealed class ReceiverHoldingWeakSinkDelegateImpl : IReverseWeakDelegate
{
    private readonly ReverseWeakSinkHarness _receiver;

    public ReceiverHoldingWeakSinkDelegateImpl(ReverseWeakSinkHarness receiver) => _receiver = receiver;

    public int WeakValue(int value) => value + 2000;

    public ReverseWeakSinkHarness Receiver => _receiver;
}
