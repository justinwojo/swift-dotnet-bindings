// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.Lifetime;

/// <summary>
/// The <c>@objc protocol</c> flavour of a non-retaining sink: an <c>NSObject</c>-rooted Swift
/// host holding <c>weak var delegate: (any P)?</c> where <c>P</c> is an <c>@objc</c> protocol —
/// the delegate shape used across Apple's own frameworks.
///
/// <para>
/// This shape does not travel as an opaque existential container. An <c>@objc</c> existential is
/// a bare ObjC object pointer, so the setter hands Swift a single pointer to the conformer box
/// and the storage is an ObjC zeroing weak reference. The narrower wire width changes nothing
/// about ownership: the sink still takes no reference, so the carrier has to follow the only
/// object whose lifetime matches what the Swift declaration promises — the consumer's own
/// implementation. The invariants are the same two halves the opaque-container sinks are held
/// to, and they are asserted here on the ObjC pointer arm.
/// </para>
///
/// <para>
/// Sentinels make a lost carrier observable as a wrong value rather than as a crash: a live
/// delegate returns <c>value + 3000</c>, an emptied slot returns <c>-1</c>.
/// </para>
/// </summary>
public class ObjCNonRetainingSinkRootingTests : TestBase
{
    public ObjCNonRetainingSinkRootingTests(TestResults results) : base(results) { }

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
    /// collection runs. A dead slot left behind in a still-live frame reads as a root under a
    /// conservative stack scan, which would pin the very carrier under test.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunOnRetiredThread(Action action)
    {
        var worker = new System.Threading.Thread(() => action()) { IsBackground = true };
        worker.Start();
        worker.Join();
    }

    // ---- The carrier follows the consumer's implementation ------------------

    /// <summary>
    /// Assign a C# implementation into the <c>@objc weak</c> Swift property, drop the carrier the
    /// setter minted (the consumer never sees it), collect, and prove the delegate is still there
    /// while the consumer holds the implementation: the Swift property reads non-nil, Swift-side
    /// dispatch reaches the C# implementation, and reading the property back vends a working
    /// carrier.
    /// </summary>
    public void TestObjCWeakSinkSurvivesGcWhileConsumerHoldsImpl()
    {
        var harness = new ObjCReverseWeakSinkHarness();
        var impl = new ObjCWeakSinkDelegateImpl();
        AssignObjCWeak(harness, impl);

        // Everything the setter allocated internally is now unreferenced by the test; only the
        // consumer's own `impl` reference is left.
        ForceGc();

        AssertTrue(harness.HasObjCDelegate,
            "@objc weak Swift storage still reads non-nil after a full collection");
        AssertEqual(3005, harness.InvokeObjCWeak(value: 5),
            "Swift dispatches into the C# implementation through the @objc weak sink (5 + 3000)");

        var readBack = harness.ObjcDelegate;
        AssertNotNull(readBack, "@objc weak property re-vends a non-null carrier");
        AssertEqual(3007, readBack!.ObjcWeakValue(7),
            "the re-vended carrier round-trips a value (7 + 3000)");

        TestLogger.Info("[ObjCNonRetainingSink] @objc weak sink survived GC while the consumer held the implementation");
        GC.KeepAlive(impl);
        GC.KeepAlive(harness);
    }

    /// <summary>
    /// Repeated assignment of the same implementation must reuse one carrier rather than mint a
    /// conformer box per assignment, and the slot has to still dispatch afterwards.
    /// </summary>
    public void TestObjCWeakSinkRepeatedAssignmentReusesOneCarrier()
    {
        var harness = new ObjCReverseWeakSinkHarness();
        var impl = new ObjCWeakSinkDelegateImpl();

        AssignObjCWeak(harness, impl);
        ForceGc();
        var afterFirst = SwiftLeakCensus.Report().ProxyImplRoots;

        for (int i = 0; i < 8; i++)
            AssignObjCWeak(harness, impl);
        ForceGc();
        var afterMany = SwiftLeakCensus.Report().ProxyImplRoots;

        AssertTrue(afterMany <= afterFirst,
            $"re-assigning the same implementation does not accumulate carriers (first={afterFirst}, after 8 more={afterMany})");
        AssertEqual(3013, harness.InvokeObjCWeak(value: 13),
            "the reused carrier still dispatches (13 + 3000)");

        TestLogger.Info($"[ObjCNonRetainingSink] repeated @objc weak assignment: first={afterFirst}, afterMany={afterMany}");
        GC.KeepAlive(impl);
        GC.KeepAlive(harness);
    }

    // ---- The carrier dies with the implementation ---------------------------

    /// <summary>
    /// The other half. The consumer drops the implementation while still holding the receiver:
    /// <c>weak</c> means Swift promised not to keep the delegate alive, so the ObjC zeroing weak
    /// reference must clear, dispatch must report the nil sentinel rather than reach a dead
    /// object, and none of it may fault.
    /// </summary>
    public void TestObjCWeakSinkGoesNilWhenConsumerDropsImplButKeepsReceiver()
    {
        var harness = new ObjCReverseWeakSinkHarness();
        var implRef = AssignObjCWeakAndDropImpl(harness);

        ForceGc();

        AssertFalse(implRef.IsAlive, "the implementation is collectable once the consumer drops it");
        AssertFalse(harness.HasObjCDelegate,
            "@objc weak Swift storage reads nil after the consumer dropped the implementation");
        AssertEqual(-1, harness.InvokeObjCWeak(value: 5),
            "dispatch through the emptied @objc weak sink reports the nil sentinel instead of touching a dead box");
        AssertNull(harness.ObjcDelegate, "the emptied @objc weak property vends null");

        TestLogger.Info("[ObjCNonRetainingSink] @objc weak sink nilled when the consumer dropped the implementation");
        GC.KeepAlive(harness);
    }

    /// <summary>
    /// Assigns a freshly-created implementation into the receiver's <c>@objc weak</c> sink and
    /// returns only a weak handle to it, so nothing in the caller's frame keeps it alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AssignObjCWeakAndDropImpl(ObjCReverseWeakSinkHarness harness)
    {
        WeakReference? implRef = null;
        RunOnRetiredThread(() =>
        {
            var impl = new ObjCWeakSinkDelegateImpl();
            harness.ObjcDelegate = impl;
            implRef = new WeakReference(impl);
        });
        return implRef!;
    }

    /// <summary>
    /// Assigning null clears the Swift storage, and the carrier the previous assignment minted
    /// has nothing else holding it once the consumer drops the implementation too.
    /// </summary>
    public void TestObjCWeakSinkClearedByNullAssignment()
    {
        ForceGc();
        var baseline = SwiftLeakCensus.Report().ProxyImplRoots;

        var harness = new ObjCReverseWeakSinkHarness();
        var whileAlive = AssignObjCWeakThenClearAndDrop(harness);
        AssertTrue(whileAlive > baseline,
            $"the conformer box exists after the assignment (baseline={baseline}, live={whileAlive})");

        ForceGc();

        AssertFalse(harness.HasObjCDelegate, "@objc weak Swift storage reads nil after a null assignment");
        AssertEqual(-1, harness.InvokeObjCWeak(value: 5),
            "dispatch through a cleared @objc weak sink reports the nil sentinel");
        var after = SwiftLeakCensus.Report().ProxyImplRoots;
        AssertTrue(after <= baseline,
            $"nothing outlives the cleared assignment (baseline={baseline}, after={after})");

        TestLogger.Info($"[ObjCNonRetainingSink] null assignment cleared the @objc weak sink: baseline={baseline}, live={whileAlive}, after={after}");
        GC.KeepAlive(harness);
    }

    /// <summary>
    /// Assigns a fresh implementation into the <c>@objc weak</c> sink, samples the census while
    /// the consumer still holds that implementation, then clears the slot and drops the
    /// implementation — all inside a worker thread that has exited before the caller collects.
    /// Returns the census reading taken while the implementation was alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int AssignObjCWeakThenClearAndDrop(ObjCReverseWeakSinkHarness harness)
    {
        var whileAlive = 0;
        RunOnRetiredThread(() =>
        {
            var impl = new ObjCWeakSinkDelegateImpl();
            harness.ObjcDelegate = impl;

            ForceGc();
            whileAlive = SwiftLeakCensus.Report().ProxyImplRoots;

            harness.ObjcDelegate = null;
            GC.KeepAlive(impl);
        });
        return whileAlive;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssignObjCWeak(ObjCReverseWeakSinkHarness harness, IObjCReverseWeakDelegate impl)
    {
        harness.ObjcDelegate = impl;
        // The carrier the setter minted is unreferenced from here on: only the consumer's own
        // reference to `impl` keeps the conformer box alive.
    }
}

/// <summary>
/// Conformer for the <c>@objc</c> non-retaining sink. Deliberately its own type so this arm can
/// never alias the opaque-container conformers.
/// </summary>
internal sealed class ObjCWeakSinkDelegateImpl : IObjCReverseWeakDelegate
{
    public int ObjcWeakValue(int value) => value + 3000;
}
