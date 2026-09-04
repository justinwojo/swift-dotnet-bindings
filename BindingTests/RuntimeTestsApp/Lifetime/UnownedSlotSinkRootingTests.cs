// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// The non-optional flavour of a non-retaining sink: a Swift host holding
/// <c>unowned var delegate: any P</c> for a class-bound protocol <c>P</c>.
///
/// <para>
/// Because the storage is not optional, the setter's value crosses as a plain existential
/// argument rather than through the decomposed optional path — a different marshalling arm from
/// the <c>weak</c>/optional sinks, but the same ownership question. <c>unowned</c> takes no
/// reference, so the conformer box must follow the consumer's implementation object or the slot
/// is left pointing at storage nothing keeps alive.
/// </para>
///
/// <para>
/// Reading a dangling <c>unowned</c> slot traps by design, so no assertion here reads the slot
/// after the object behind it is gone: the drop case restores the slot to a live implementation
/// before anything touches Swift again, and proves the released carrier through the census
/// instead. The host is constructed through an initializer, which stores its argument into the
/// same non-retaining slot; whether an initializer argument ends up in non-retaining storage is
/// not knowable at that call site, so every test here re-assigns through the setter before it
/// collects.
/// </para>
///
/// <para>
/// Sentinels make a lost carrier observable as a wrong value: a live delegate returns
/// <c>value + 4000</c>.
/// </para>
/// </summary>
public class UnownedSlotSinkRootingTests : TestBase
{
    public UnownedSlotSinkRootingTests(TestResults results) : base(results) { }

    private const int GcCycles = 6;

    /// <summary>
    /// Force a GC on a worker thread (the main thread blocks on Join with a minimal live-local
    /// footprint) so a conservative stack scan does not pin the dropped carrier.
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
    /// collection runs.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunOnRetiredThread(Action action)
    {
        var worker = new System.Threading.Thread(() => action()) { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    /// <summary>
    /// Builds a host and settles its slot through the setter — the arm under test — so nothing
    /// later reads a slot that only the initializer ever populated.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ReverseUnownedSlotHarness CreateHarness(UnownedSlotDelegateImpl anchor)
    {
        var harness = new ReverseUnownedSlotHarness(anchor);
        harness.SlotDelegate = anchor;
        return harness;
    }

    // ---- The carrier follows the consumer's implementation ------------------

    /// <summary>
    /// Assign a C# implementation into the non-optional <c>unowned</c> Swift property, drop the
    /// carrier the setter minted (the consumer never sees it), collect, and prove Swift still
    /// dispatches into the implementation the consumer is still holding.
    /// </summary>
    public void TestUnownedSlotSurvivesGcWhileConsumerHoldsImpl()
    {
        var impl = new UnownedSlotDelegateImpl();
        var harness = CreateHarness(impl);

        // Everything the setter allocated internally is now unreferenced by the test; only the
        // consumer's own `impl` reference is left.
        ForceGc();

        AssertEqual(4005, harness.InvokeSlot(value: 5),
            "Swift dispatches into the C# implementation through the unowned slot (5 + 4000)");

        var readBack = harness.SlotDelegate;
        AssertNotNull(readBack, "the unowned property re-vends a carrier while the implementation is alive");
        AssertEqual(4007, readBack.UnownedSlotValue(7),
            "the re-vended carrier round-trips a value (7 + 4000)");

        TestLogger.Info("[UnownedSlotSink] unowned slot survived GC while the consumer held the implementation");
        GC.KeepAlive(impl);
        GC.KeepAlive(harness);
    }

    /// <summary>
    /// Repeated assignment of the same implementation must reuse one carrier rather than mint a
    /// conformer box per assignment, and the slot has to still dispatch afterwards.
    /// </summary>
    public void TestUnownedSlotRepeatedAssignmentReusesOneCarrier()
    {
        var impl = new UnownedSlotDelegateImpl();
        var harness = CreateHarness(impl);

        ForceGc();
        var afterFirst = SwiftLeakCensus.Report().ProxyImplRoots;

        for (int i = 0; i < 8; i++)
            AssignSlot(harness, impl);
        ForceGc();
        var afterMany = SwiftLeakCensus.Report().ProxyImplRoots;

        AssertTrue(afterMany <= afterFirst,
            $"re-assigning the same implementation does not accumulate carriers (first={afterFirst}, after 8 more={afterMany})");
        AssertEqual(4013, harness.InvokeSlot(value: 13),
            "the reused carrier still dispatches (13 + 4000)");

        TestLogger.Info($"[UnownedSlotSink] repeated unowned assignment: first={afterFirst}, afterMany={afterMany}");
        GC.KeepAlive(impl);
        GC.KeepAlive(harness);
    }

    // ---- The carrier dies with the implementation ---------------------------

    /// <summary>
    /// The other half, asserted without ever reading a dangling slot. A second implementation is
    /// assigned and then dropped; the slot is restored to the still-live anchor before anything
    /// touches Swift again, and the census proves the dropped implementation's conformer box went
    /// with it rather than outliving the consumer's reference.
    /// </summary>
    public void TestUnownedSlotCarrierReleasedWhenConsumerDropsImpl()
    {
        var anchor = new UnownedSlotDelegateImpl();
        var harness = CreateHarness(anchor);

        // Two drains: the initializer's own carrier is unreferenced once the setter overwrote the
        // slot, and it must be off the census before the baseline reading is taken.
        ForceGc();
        ForceGc();
        var baseline = SwiftLeakCensus.Report().ProxyImplRoots;

        var whileAlive = AssignTransientThenRestoreAnchor(harness, anchor);
        AssertTrue(whileAlive > baseline,
            $"the transient implementation's conformer box exists while it is alive (baseline={baseline}, live={whileAlive})");

        ForceGc();

        var after = SwiftLeakCensus.Report().ProxyImplRoots;
        AssertTrue(after <= baseline,
            $"no conformer box outlives the dropped implementation (baseline={baseline}, after={after})");
        AssertEqual(4003, harness.InvokeSlot(value: 3),
            "the restored anchor still dispatches through the unowned slot (3 + 4000)");

        TestLogger.Info($"[UnownedSlotSink] dropped implementation released its carrier: baseline={baseline}, live={whileAlive}, after={after}");
        GC.KeepAlive(anchor);
        GC.KeepAlive(harness);
    }

    /// <summary>
    /// Assigns a fresh implementation into the unowned slot, samples the census while the
    /// consumer still holds it, then puts the anchor back so the slot is never left pointing at
    /// an object about to be collected — all inside a worker thread that has exited before the
    /// caller collects. Returns the census reading taken while the transient was alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AssignTransientThenRestoreAnchor(ReverseUnownedSlotHarness harness, UnownedSlotDelegateImpl anchor)
    {
        var whileAlive = 0;
        RunOnRetiredThread(() =>
        {
            var transient = new UnownedSlotDelegateImpl();
            harness.SlotDelegate = transient;

            ForceGc();
            whileAlive = SwiftLeakCensus.Report().ProxyImplRoots;

            harness.SlotDelegate = anchor;
            GC.KeepAlive(transient);
        });
        return whileAlive;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssignSlot(ReverseUnownedSlotHarness harness, IReverseUnownedSlotDelegate impl)
    {
        harness.SlotDelegate = impl;
        // The carrier the setter minted is unreferenced from here on: only the consumer's own
        // reference to `impl` keeps the conformer box alive.
    }
}

/// <summary>
/// Conformer for the non-optional unowned sink. Deliberately its own type so this arm can never
/// alias the optional-sink conformers.
/// </summary>
internal sealed class UnownedSlotDelegateImpl : IReverseUnownedSlotDelegate
{
    public int UnownedSlotValue(int value) => value + 4000;
}
