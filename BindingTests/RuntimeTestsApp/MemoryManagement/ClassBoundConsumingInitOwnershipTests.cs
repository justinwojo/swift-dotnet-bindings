// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// The class-constrained sibling of <see cref="ConsumingInitOwnershipTests"/>. Same question — an
/// initializer's value parameter is lowered <c>@owned</c>, so the direct arm's caller has to mint
/// the count the callee releases — but over a carrier with a different shape.
///
/// <para>An <c>any P</c> whose <c>P</c> is <c>AnyObject</c>-constrained is the compact
/// <c>[classRef][witnessTable]</c> value, which Swift passes in two registers rather than through
/// a pointer to the five-word opaque layout. So this arm differs from its sibling twice over: the
/// call site declares and hands over the two-word carrier, and the mint cannot be the opaque
/// existential's value-witness copy — the whole reference lives on word 0 and the count is a
/// retain there. This class is the runtime statement that both hold: the callee reaches a real
/// object at all, and the hand-over balances — the conformer survives the callee's release while
/// the proxy still owns its own reference, and dies exactly once when both sides let go.</para>
///
/// <para>Kept apart from the opaque-carrier class on purpose: the two arms mint through different
/// operations, so a regression in one must not be masked by the other's assertions.</para>
/// </summary>
public class ClassBoundConsumingInitOwnershipTests : TestBase
{
    public ClassBoundConsumingInitOwnershipTests(TestResults results) : base(results) { }

    /// <summary>
    /// C#-authored conformer, so reaching Swift means auto-wrapping it in the generated proxy whose
    /// construction reference is the conformer box's only one.
    /// </summary>
    private sealed class CountingClassBoundCarrier : IClassBoundConsumedCarrier
    {
        private readonly int _tag;

        public CountingClassBoundCarrier(int tag) => _tag = tag;

        public int GetClassBoundCarrierTag() => _tag;
    }

    private static void DrainFinalizers()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
    }

    private static bool ImplIsResolvable(IntPtr handle)
        => ProxyLifetimeTracker.ResolveImpl<IClassBoundConsumedCarrier>(handle) != null;

    /// <summary>
    /// Storing arm over the class-constrained carrier. The cell keeps the existential, so the
    /// callee's release is deferred to the struct's own destruction — and the assertion that
    /// separates a lent carrier from an owned one is the one taken after the cell is destroyed
    /// while the proxy is still alive and undisposed.
    /// </summary>
    public void TestClassBoundConsumingInitializerLeavesTheProxysReferenceIntact()
    {
        DrainFinalizers();

        var impl = new CountingClassBoundCarrier(11);
        var proxy = new ClassBoundConsumedCarrierProxy(impl);
        IntPtr handle = ((ISwiftObject)proxy).SwiftHandle;
        AssertTrue(ImplIsResolvable(handle), "the freshly built proxy must own a live conformer box");

        AssertTrue(ClassBoundConsumedCarrierCell.TryCreate(proxy, out var cell),
            "a non-negative tag must construct the cell");
        using (cell)
        {
            AssertEqual(11, cell.GetStoredTag(), "the cell must read back the tag it was constructed with");
            AssertEqual(11, cell.GetReread(),
                "reverse dispatch through the stored class-bound existential must still reach the live implementation");
            AssertTrue(ImplIsResolvable(handle), "the carrier must stay live while the cell holds it");
        }

        DrainFinalizers();
        AssertTrue(ImplIsResolvable(handle),
            "destroying a struct that consumed the class-bound carrier must not deinitialize a conformer box the proxy still owns");

        AssertEqual(11, proxy.GetClassBoundCarrierTag(), "the proxy must still forward to its implementation");

        proxy.Dispose();
        DrainFinalizers();
        AssertFalse(ImplIsResolvable(handle), "disposing the proxy must release the last reference");

        TestLogger.Info("class-bound consuming initializer: the proxy's construction reference survived the callee's release");
    }

    /// <summary>
    /// Repeats the hand-over on one proxy. An over-mint on this arm never crashes — it leaks — so
    /// the statement that the retain and the callee's release balance is that the conformer is gone
    /// after the proxy is dropped, however many carriers were handed over from it.
    /// </summary>
    public void TestRepeatedClassBoundHandOversLeaveNoStrandedReference()
    {
        DrainFinalizers();

        var impl = new CountingClassBoundCarrier(5);
        var proxy = new ClassBoundConsumedCarrierProxy(impl);
        IntPtr handle = ((ISwiftObject)proxy).SwiftHandle;

        for (int i = 0; i < 8; i++)
        {
            AssertTrue(ClassBoundConsumedCarrierCell.TryCreate(proxy, out var cell),
                "every hand-over from a live proxy must construct");
            using (cell)
            {
                AssertEqual(5, cell.GetReread(), "each consumed carrier must still reach the implementation");
            }
            DrainFinalizers();
            AssertTrue(ImplIsResolvable(handle),
                "no iteration may release a reference the proxy still owns");
        }

        proxy.Dispose();
        DrainFinalizers();
        AssertFalse(ImplIsResolvable(handle),
            "the conformer must not outlive the proxy — a surplus retain per hand-over would strand it");

        TestLogger.Info("class-bound consuming initializer: repeated hand-overs stranded no reference");
    }

    /// <summary>
    /// The shape a consumer actually writes: hand the plain C# implementation over and let the call
    /// site's own wrap fallback build the carrier. Nothing roots that proxy, so this is also the
    /// statement that the count the callee consumes is minted before the transient wrapper can be
    /// collected — a stranded or missing +1 here is a crash or a use-after-free, not a leak.
    /// </summary>
    public void TestAutoWrappedClassBoundConformerRoundTripsThroughTheHandOver()
    {
        DrainFinalizers();

        var impl = new CountingClassBoundCarrier(3);

        AssertTrue(ClassBoundConsumedCarrierCell.TryCreate(impl, out var cell),
            "a plain C# conformer must construct the cell through the wrap fallback");
        using (cell)
        {
            AssertEqual(3, cell.GetStoredTag(), "the auto-wrapped carrier must carry the conformer's tag");
            AssertEqual(3, cell.GetReread(),
                "reverse dispatch through the auto-wrapped carrier must reach the C# implementation");
        }

        DrainFinalizers();

        AssertTrue(ClassBoundConsumedCarrierCell.TryCreate(impl, out var second),
            "the implementation must still be usable after the first auto-wrapped hand-over");
        using (second)
        {
            AssertEqual(3, second.GetReread(), "the second auto-wrapped carrier must reach the same implementation");
        }

        DrainFinalizers();

        TestLogger.Info("class-bound consuming initializer: the auto-wrapped conformer round-tripped");
    }

    /// <summary>
    /// The failing exit. A negative tag makes the initializer return nil, but the parameter is
    /// consumed on that path exactly as on the storing one — Swift releases the carrier before
    /// unwinding. So the count the caller mints has to be there whether or not a value comes back:
    /// an unminted carrier is released out from under the proxy, and a mint the failing path
    /// forgot to account for strands the conformer.
    /// </summary>
    public void TestFailingClassBoundInitializerNeitherStrandsNorOverReleasesTheCarrier()
    {
        DrainFinalizers();

        var impl = new CountingClassBoundCarrier(-7);
        var proxy = new ClassBoundConsumedCarrierProxy(impl);
        IntPtr handle = ((ISwiftObject)proxy).SwiftHandle;

        for (int i = 0; i < 4; i++)
        {
            AssertFalse(ClassBoundConsumedCarrierCell.TryCreate(proxy, out var cell),
                "a negative tag must fail the initializer");
            AssertTrue(cell is null, "a failed construction must not hand back a cell");
            DrainFinalizers();
            AssertTrue(ImplIsResolvable(handle),
                "a failing initializer must not release a reference the proxy still owns");
        }

        AssertEqual(-7, proxy.GetClassBoundCarrierTag(),
            "the proxy must still forward to its implementation after the failed hand-overs");

        proxy.Dispose();
        DrainFinalizers();
        AssertFalse(ImplIsResolvable(handle),
            "the conformer must not outlive the proxy — a surplus retain on the failing path would strand it");

        TestLogger.Info("class-bound consuming initializer: the failing exit balanced");
    }

    /// <summary>
    /// The failing exit over a Swift-vended conformer, where the shared counters make the balance
    /// visible from Swift rather than only through the managed proxy's bookkeeping.
    /// </summary>
    public void TestFailingClassBoundInitializerOverASwiftVendedConformerBalances()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        using (var conformer = new ClassBoundConsumedCarrierCounterCell(-3))
        {
            LifetimeTracker.AssertLiveCount(1, "the Swift-vended conformer's tracked reference is live");

            for (int i = 0; i < 4; i++)
            {
                AssertFalse(ClassBoundConsumedCarrierCell.TryCreate(conformer, out var cell),
                    "a negative tag must fail the initializer for a Swift-vended conformer too");
                AssertTrue(cell is null, "a failed construction must not hand back a cell");
            }

            DrainFinalizers();
            LifetimeTracker.AssertLiveCount(1,
                "the failing hand-overs must not release the conformer the caller still holds");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0, "dropping the caller's conformer must leave nothing live");

        TestLogger.Info("class-bound consuming initializer: the Swift-vended failing exit balanced");
    }

    /// <summary>
    /// The Swift-vended conformer boxes itself fresh for every call, so the callee consumes a
    /// reference nobody else holds. Carried as the donate-arm control: the shared allocation
    /// counters must return to zero, which an over-mint on this arm would prevent.
    /// </summary>
    public void TestSwiftVendedClassBoundConformerBalancesAcrossTheHandOver()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        using (var conformer = new ClassBoundConsumedCarrierCounterCell(9))
        {
            LifetimeTracker.AssertLiveCount(1, "the Swift-vended conformer's tracked reference is live");

            AssertTrue(ClassBoundConsumedCarrierCell.TryCreate(conformer, out var cell),
                "a Swift-vended class-bound conformer must construct the cell");
            using (cell)
            {
                AssertEqual(9, cell.GetStoredTag(), "the cell must read back the conformer's tag");
                AssertEqual(9, cell.GetReread(), "the consumed conformer must still answer through the cell");
            }

            DrainFinalizers();
            LifetimeTracker.AssertLiveCount(1,
                "consuming a Swift-vended carrier must not release the conformer the caller still holds");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0, "dropping both sides must leave nothing live");

        TestLogger.Info("class-bound consuming initializer: the Swift-vended conformer balanced across the hand-over");
    }
}
