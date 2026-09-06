// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;
using SwiftBindingsTestLib.SwiftInterop;

namespace RuntimeTestsApp.MemoryManagement;

/// <summary>
/// Ownership probe for the arguments a Swift INITIALIZER consumes, on the two argument shapes the
/// direct <c>CallConvSwift</c> arm renders inline: an existential container backed by a generated
/// protocol proxy, and a non-frozen (resilient) struct carrying a class reference.
///
/// <para>SILGen lowers an initializer's value parameters <c>@owned</c> (<c>@in</c> when the type is
/// address-only), so the callee RELEASES what it was handed. A <c>@_cdecl</c> wrapper in between is
/// a borrowing frame — SILGen mints the transfer there itself — but the direct arm has no frame, so
/// the C# caller is the one that has to mint it. Handing a borrowed container to that callee is an
/// under-retain, and the interesting case is a PROXY-backed container: its bytes alias the
/// reference the proxy created and still owns, so the callee's release takes the conformer box out
/// from under a live C# object.</para>
///
/// <para>What makes that deterministic rather than a crash is <see cref="ProxyLifetimeTracker"/>.
/// The box's deinit drops the tracker entry for its handle, so a premature deinit is observable as
/// <see cref="ProxyLifetimeTracker.ResolveImpl{T}(IntPtr)"/> going null while the proxy that owns
/// the reference is still alive and undisposed — a pure managed read, no dereference of freed
/// memory. The resilient-struct arm is observable the same way through the shared allocation
/// counters <see cref="LifetimeTracker"/> reads: the embedded <c>TrackedRef</c> dying while the
/// caller's own box wrapper is still live.</para>
///
/// <para>Both directions matter. An over-mint on an arm that only borrows leaks instead of
/// crashing, so the <c>@_cdecl</c> arm is carried here as a control and the counters are asserted
/// back to zero at the end of every test rather than merely asserted non-zero in the middle.</para>
/// </summary>
public class ConsumingInitOwnershipTests : TestBase
{
    public ConsumingInitOwnershipTests(TestResults results) : base(results) { }

    /// <summary>
    /// C#-authored conformer. Reaching Swift means auto-wrapping it in the generated proxy, whose
    /// construction reference is the conformer box's only one — the shape a Swift-vended conformer
    /// cannot produce, because that one boxes itself fresh for every call.
    /// </summary>
    private sealed class CountingCarrier : IConsumedCarrier
    {
        private readonly int _tag;
        public int Calls;

        public CountingCarrier(int tag) => _tag = tag;

        public int GetConsumedCarrierTag()
        {
            Calls++;
            return _tag;
        }
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

    private static IntPtr HandleOf(ConsumedCarrierProxy proxy) => ((ISwiftObject)proxy).SwiftHandle;

    /// <summary>
    /// Reads the box's reference field and drops the transient wrapper the projection hands back,
    /// so the read itself does not extend the referenced object's lifetime past the assertion that
    /// follows it.
    /// </summary>
    private static int ReadRefTag(TrackedRefStruct box)
    {
        using var heldRef = box.Ref;
        return heldRef.Tag;
    }

    private static bool ImplIsResolvable(IntPtr handle)
        => ProxyLifetimeTracker.ResolveImpl<IConsumedCarrier>(handle) != null;

    /// <summary>
    /// Storing arm: <c>ConsumedCarrierCell.init?(optional:)</c> consumes the existential and KEEPS
    /// it, so the callee's release is deferred to the struct's own destruction. The assertion that
    /// separates a lent container from an owned one is the one taken after the struct is destroyed
    /// and while the proxy is still alive: at that point the proxy's construction reference must be
    /// the surviving one. Without a mint the callee stored the only reference there was, and
    /// destroying the struct deinitializes the box under the live proxy.
    /// </summary>
    public void TestStoringConsumingInitializerLeavesTheProxysReferenceIntact()
    {
        DrainFinalizers();

        var impl = new CountingCarrier(7);
        var proxy = new ConsumedCarrierProxy(impl);
        IntPtr handle = HandleOf(proxy);
        AssertTrue(ImplIsResolvable(handle), "the freshly built proxy must own a live conformer box");

        AssertTrue(ConsumedCarrierCell.TryCreate(proxy, out var cell),
            "a non-negative tag must construct the cell");
        using (cell)
        {
            AssertEqual(7, cell.GetStoredTag(), "the cell must read back the tag it was constructed with");
            AssertEqual(7, cell.GetReread(),
                "reverse dispatch through the stored existential must still reach the live implementation");
            AssertTrue(ImplIsResolvable(handle), "the carrier must stay live while the cell holds it");
        }

        DrainFinalizers();
        AssertTrue(ImplIsResolvable(handle),
            "destroying a struct that consumed the container must not deinitialize a conformer box the proxy still owns");

        AssertEqual(7, proxy.GetConsumedCarrierTag(), "the proxy must still forward to its implementation");

        proxy.Dispose();
        DrainFinalizers();
        AssertFalse(ImplIsResolvable(handle), "disposing the proxy must release the last reference");

        TestLogger.Info("storing consuming initializer: the proxy's construction reference survived the callee's release");
    }

    /// <summary>
    /// Dropping arm: <c>ConsumedCarrierProbe.init?(reading:)</c> consumes the existential and lets
    /// it go before returning, so the callee's release lands INSIDE the call. Under-retained, the
    /// box is already gone by the time the initializer returns — which is why this arm is kept
    /// separate from the storing one, where the same defect only surfaces later.
    /// </summary>
    public void TestDroppingConsumingInitializerLeavesTheProxysReferenceIntact()
    {
        DrainFinalizers();

        var impl = new CountingCarrier(3);
        var proxy = new ConsumedCarrierProxy(impl);
        IntPtr handle = HandleOf(proxy);

        AssertTrue(ConsumedCarrierProbe.TryCreate(proxy, out var probe),
            "a non-negative tag must construct the probe");
        using (probe)
        {
            AssertEqual(3, probe.GetStoredTag(), "the probe must read back the tag it was constructed with");
            AssertTrue(ImplIsResolvable(handle),
                "an initializer that consumes and drops the container must not release a reference it was only lent");
        }

        // Reusing the same proxy for a second consuming call is the strongest available statement
        // that the first call left it intact: a released box would fail this construction or
        // resolve to no implementation on the reverse-dispatch read inside it.
        AssertTrue(ConsumedCarrierCell.TryCreate(proxy, out var cell),
            "the same proxy must still be usable for a second consuming initializer");
        using (cell)
        {
            AssertEqual(3, cell.GetReread(), "the twice-consumed carrier must still reach the implementation");
        }

        DrainFinalizers();
        AssertTrue(ImplIsResolvable(handle), "the proxy must still own its conformer box");

        proxy.Dispose();
        DrainFinalizers();
        AssertFalse(ImplIsResolvable(handle), "disposing the proxy must release the last reference");

        TestLogger.Info("dropping consuming initializer: the borrowed container survived a release landing inside the call");
    }

    /// <summary>
    /// Failure exit. A failable initializer that returns nil still consumed its arguments, so the
    /// mint made before the call is balanced by the callee on the nil path exactly as on the
    /// success path — no second release on the way out, and no reference stranded either.
    /// </summary>
    public void TestFailedConsumingInitializerLeavesTheProxysReferenceIntact()
    {
        DrainFinalizers();

        var impl = new CountingCarrier(-1);
        var proxy = new ConsumedCarrierProxy(impl);
        IntPtr handle = HandleOf(proxy);

        AssertFalse(ConsumedCarrierCell.TryCreate(proxy, out _), "a negative tag must fail the initializer");
        AssertTrue(ImplIsResolvable(handle),
            "a nil return must leave the caller's carrier exactly as it found it");

        AssertFalse(ConsumedCarrierProbe.TryCreate(proxy, out _), "a negative tag must fail the probe too");
        DrainFinalizers();
        AssertTrue(ImplIsResolvable(handle), "repeated failed constructions must not accumulate releases");

        proxy.Dispose();
        DrainFinalizers();
        AssertFalse(ImplIsResolvable(handle), "disposing the proxy must release the last reference");

        TestLogger.Info("failed consuming initializer: the nil exit balanced the hand-over");
    }

    /// <summary>
    /// Throwing arm, reached through the <c>@_cdecl</c> wrapper rather than the direct symbol. The
    /// wrapper is a borrowing frame, so the C# side must NOT mint here — this is the over-mint
    /// control, and it covers the exceptional exit as well, where a caller-side mint would have no
    /// call to hand itself to.
    /// </summary>
    public void TestThrowingConsumingInitializerBalancesBothExits()
    {
        DrainFinalizers();

        var rejected = new CountingCarrier(-5);
        var rejectedProxy = new ConsumedCarrierProxy(rejected);
        IntPtr rejectedHandle = HandleOf(rejectedProxy);

        AssertThrows<SwiftException>(
            () => { using var _ = new ConsumedCarrierThrowingCell(rejectedProxy); },
            "a negative tag must throw out of the initializer");
        AssertTrue(ImplIsResolvable(rejectedHandle),
            "the thrown exit must leave the caller's carrier intact");

        rejectedProxy.Dispose();
        DrainFinalizers();
        AssertFalse(ImplIsResolvable(rejectedHandle), "disposing the proxy must release the last reference");

        var accepted = new CountingCarrier(9);
        var acceptedProxy = new ConsumedCarrierProxy(accepted);
        IntPtr acceptedHandle = HandleOf(acceptedProxy);

        using (var cell = new ConsumedCarrierThrowingCell(acceptedProxy))
        {
            AssertEqual(9, cell.GetStoredTag(), "the cell must read back the tag it was constructed with");
        }

        DrainFinalizers();
        AssertTrue(ImplIsResolvable(acceptedHandle),
            "the wrapper arm must not release a reference the proxy still owns");

        acceptedProxy.Dispose();
        DrainFinalizers();
        AssertFalse(ImplIsResolvable(acceptedHandle), "disposing the proxy must release the last reference");

        TestLogger.Info("throwing consuming initializer: both exits left the wrapper arm balanced");
    }

    /// <summary>
    /// Swift-vended conformer. Here the container is boxed fresh for the call rather than aliasing
    /// anyone's reference, so the hand-over has nothing to mint and must donate the box it already
    /// owns instead — minting a second time would leak it. The Swift class carries a tracked
    /// reference, so both failures are visible in the shared counters.
    /// </summary>
    public void TestSwiftVendedConformerIsBalancedThroughTheConsumingInitializer()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var conformer = new ConsumedCarrierCounterCell(4);
        LifetimeTracker.AssertLiveCount(1, "the Swift conformer's tracked reference is live once its wrapper exists");

        AssertTrue(ConsumedCarrierCell.TryCreate(conformer, out var cell), "the cell must construct");
        using (cell)
        {
            AssertEqual(4, cell.GetStoredTag(), "the cell must read back the conformer's tag");
            AssertEqual(4, cell.GetReread(), "reverse dispatch must reach the Swift conformer");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(1,
            "the boxed carrier is the callee's to release; the caller's own conformer wrapper still owns its reference");

        conformer.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0, "disposing the conformer must release the last reference");

        TestLogger.Info("Swift-vended conformer: the freshly boxed carrier was donated, not double-minted");
    }

    /// <summary>
    /// Resilient-struct arm. <c>TrackedRefStruct</c> is non-frozen, so it is address-only across the
    /// ABI and travels into the initializer's slot as a pointer to the caller's own buffer. The
    /// callee takes over the contents at that address; unless the caller mints the references first,
    /// the callee's eventual release is taken out of the caller's buffer, and the caller's wrapper
    /// is left owning a reference that is already gone.
    ///
    /// <para>The observable is the embedded <c>TrackedRef</c>: it must survive the struct that
    /// consumed a copy of it being destroyed, and must die exactly once, when the caller's own box
    /// wrapper is disposed.</para>
    /// </summary>
    public void TestResilientStructWithReferenceFieldSurvivesTheConsumingInitializer()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var box = new TrackedRefStruct(6);
        LifetimeTracker.AssertLiveCount(1, "the box's embedded reference is live once its wrapper exists");

        AssertTrue(ResilientRefConsumerCell.TryCreate(box, out var cell),
            "a non-negative value must construct the cell");
        using (cell)
        {
            AssertEqual(6, cell.GetHeldValue(), "the cell must read back the value it consumed");
            AssertEqual(6, cell.GetHeldRefTag(), "the cell must read through the reference field it consumed");
            AssertEqual(6, box.Value, "the caller's own box must survive the initializer intact");
            AssertEqual(6, ReadRefTag(box), "the caller's own reference field must survive the initializer intact");
            LifetimeTracker.AssertLiveCount(1, "both sides share the one tracked reference");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(1,
            "destroying the struct that consumed the value must not release a reference the caller still owns");
        AssertEqual(6, ReadRefTag(box), "the caller's box must still read through its reference field");

        box.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0, "disposing the caller's box must release the last reference");

        TestLogger.Info("resilient struct: the consuming initializer's slot took a copy, not the caller's only reference");
    }

    /// <summary>
    /// Failure exit for the resilient-struct slot: a nil return consumed the value all the same, so
    /// the mint has to be balanced there too. Repeated so a single stranded reference shows up as a
    /// count that climbs rather than one that could be read as noise.
    /// </summary>
    public void TestFailedResilientStructInitializerLeavesTheCallersBoxIntact()
    {
        DrainFinalizers();
        LifetimeTracker.Reset();

        var box = new TrackedRefStruct(-3);
        LifetimeTracker.AssertLiveCount(1, "the box's embedded reference is live once its wrapper exists");

        for (int i = 0; i < 8; i++)
        {
            AssertFalse(ResilientRefConsumerCell.TryCreate(box, out _), "a negative value must fail the initializer");
        }

        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(1, "eight nil exits must neither strand nor over-release a reference");
        AssertEqual(-3, ReadRefTag(box), "the caller's box must still read through its reference field");

        box.Dispose();
        DrainFinalizers();
        LifetimeTracker.AssertLiveCount(0, "disposing the caller's box must release the last reference");

        TestLogger.Info("resilient struct: eight failed consuming initializers left the caller's box balanced");
    }

    /// <summary>
    /// Churn under collection pressure. Each iteration builds a fresh proxy, drives it through both
    /// consuming arms, and drops it; the collections in between are what turn a stranded reference
    /// into an observable leak and a missing one into an observable early deinit. The handle of the
    /// LAST proxy is checked while it is still rooted, so an over-release anywhere in the loop that
    /// happened to be masked by a live cell is still caught.
    /// </summary>
    public void TestConsumingInitializersStayBalancedUnderCollectionPressure()
    {
        DrainFinalizers();

        for (int i = 0; i < 24; i++)
        {
            var impl = new CountingCarrier(i);
            var proxy = new ConsumedCarrierProxy(impl);
            IntPtr handle = HandleOf(proxy);

            AssertTrue(ConsumedCarrierProbe.TryCreate(proxy, out var probe), "the probe must construct");
            probe.Dispose();

            AssertTrue(ConsumedCarrierCell.TryCreate(proxy, out var cell), "the cell must construct");
            GC.Collect();
            AssertEqual(i, cell.GetReread(), "the retained carrier must survive a collection mid-flight");
            cell.Dispose();

            GC.Collect();
            AssertTrue(ImplIsResolvable(handle), "the proxy's reference must survive the whole iteration");

            proxy.Dispose();
            DrainFinalizers();
            AssertFalse(ImplIsResolvable(handle), "the iteration must end with the box released exactly once");
        }

        TestLogger.Info("consuming initializers: 24 churn iterations stayed balanced under collection pressure");
    }
}
