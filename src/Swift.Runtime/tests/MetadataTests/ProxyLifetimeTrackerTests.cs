// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;
using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// Unit tests for <see cref="ProxyLifetimeTracker"/>'s bookkeeping logic under "Design B2"
/// (handle-keyed strong impl root + <see cref="ProxyLifetimeTracker.ResolveImpl{T}"/> +
/// proxy-owned R0 release via <see cref="ProxyLifetimeTracker.ReleaseHandle"/>). Real ARC release
/// verification happens in BindingTests runtime tests (ProxyLifetimeTests.cs) — these unit tests
/// cover the managed data-structure side only, using mock pointers that are never dereferenced and
/// <see cref="SwiftExitGuard"/> to short-circuit any native release path.
/// </summary>
/// <remarks>
/// Uses xunit collection serialization (via <c>[Collection]</c>) AND a Monitor-lock scope
/// (<see cref="SwiftExitGuardTestScope"/>) because <c>SwiftExitGuard.SetProcessExitingForTest</c>
/// mutates a process-global flag. The Monitor lock is belt-and-suspenders on top of collection
/// isolation — we've observed rare flakes under full-suite runs where the collection alone didn't
/// serialize reliably, so every flag-touching test takes the lock explicitly.
/// </remarks>
[Collection(SwiftExitGuardCollection.Name)]
public class ProxyLifetimeTrackerTests
{
    // Mock "handle" values — never dereferenced. Tests drop tracker state via DropForTest (which
    // never calls swift_release) so no native release is ever attempted on these.
    private static IntPtr NewMockHandle() => new IntPtr(Random.Shared.NextInt64(0x10000, long.MaxValue));

    private interface IMockFace { }

    private sealed class MockImpl : IMockFace { }

    [Fact]
    public void Track_ThrowsOnNullImpl()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ProxyLifetimeTracker.Track(null!, new IntPtr(0x1234)));
    }

    [Fact]
    public void Track_ThrowsOnZeroHandle()
    {
        Assert.Throws<ArgumentException>(() =>
            ProxyLifetimeTracker.Track(new MockImpl(), IntPtr.Zero));
    }

    [Fact]
    public void Track_MarksHandleAsTracked()
    {
        var impl = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl, handle);
            Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle));
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void ResolveImpl_ReturnsTrackedImpl_AsRequestedInterface()
    {
        var impl = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl, handle);

            // Reverse dispatch resolves the impl through the requested interface view.
            Assert.Same(impl, ProxyLifetimeTracker.ResolveImpl<IMockFace>(handle));
            Assert.Same(impl, ProxyLifetimeTracker.ResolveImpl<MockImpl>(handle));
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void ResolveImpl_WrongType_ReturnsNull()
    {
        var impl = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl, handle);
            // The impl is rooted but is not a string — a mismatched view resolves to null.
            Assert.Null(ProxyLifetimeTracker.ResolveImpl<string>(handle));
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void ResolveImpl_UntrackedHandle_ReturnsNull()
    {
        Assert.Null(ProxyLifetimeTracker.ResolveImpl<MockImpl>(new IntPtr(0xDEADBEEF)));
        Assert.Null(ProxyLifetimeTracker.ResolveImpl<MockImpl>(IntPtr.Zero));
    }

    [Fact]
    public void StrongImplRoot_KeepsImplAliveAcrossGc()
    {
        // The B2 invariant: while Swift references the proxy (i.e. the handle is tracked), the
        // strong GCHandle keeps the impl alive even if the consumer drops every managed reference.
        // This is what makes reverse dispatch fabrication-free — ResolveImpl can always find it.
        var handle = NewMockHandle();
        TrackImplWithoutKeepingRef(handle);

        for (int i = 0; i < 8; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        try
        {
            var resolved = ProxyLifetimeTracker.ResolveImpl<IMockFace>(handle);
            Assert.NotNull(resolved);
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
        }
    }

    // No-inline helper so the impl reference cannot be kept alive by the test method's stack frame.
    // The only root after this returns is the tracker's strong GCHandle.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void TrackImplWithoutKeepingRef(IntPtr handle)
    {
        var impl = new MockImpl();
        ProxyLifetimeTracker.Track(impl, handle);
        Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle));
    }

    [Fact]
    public void TrackConsumerOwned_ThrowsOnNullImpl()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ProxyLifetimeTracker.TrackConsumerOwned(null!, new IntPtr(0x1234)));
    }

    [Fact]
    public void TrackConsumerOwned_ThrowsOnZeroHandle()
    {
        Assert.Throws<ArgumentException>(() =>
            ProxyLifetimeTracker.TrackConsumerOwned(new MockImpl(), IntPtr.Zero));
    }

    [Fact]
    public void TrackConsumerOwned_MarksHandleAsTrackedAndResolves()
    {
        // Same bookkeeping surface as Track — a consumer-owned carrier still needs the handle to
        // resolve back to the impl for reverse dispatch; only the root's strength differs.
        var impl = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.TrackConsumerOwned(impl, handle);
            Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle));
            Assert.Same(impl, ProxyLifetimeTracker.ResolveImpl<IMockFace>(handle));
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void ConsumerOwnedImplRoot_ResolvesWhileSomethingElseHoldsTheImpl()
    {
        // A consumer-owned carrier's root is weak, so resolution has to come from the impl still
        // being reachable elsewhere — here, the test frame. That is the whole point: the consumer's
        // reference, not Swift's, is what decides how long dispatch keeps working.
        var impl = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.TrackConsumerOwned(impl, handle);

            for (int i = 0; i < 8; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            Assert.Same(impl, ProxyLifetimeTracker.ResolveImpl<IMockFace>(handle));
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void ConsumerOwnedImplRoot_DoesNotKeepImplAliveAcrossGc()
    {
        // The inversion that makes a non-retaining Swift sink behave the way its declaration says:
        // tracking a consumer-owned carrier must NOT be a root. Once the consumer drops the impl,
        // it becomes collectable even though the handle is still tracked — which is what lets the
        // proxy and the Swift box that reads through it fall away together.
        // Contrast StrongImplRoot_KeepsImplAliveAcrossGc, which asserts the opposite for the
        // Swift-rooted lane under an otherwise identical setup.
        var handle = NewMockHandle();
        TrackConsumerOwnedImplWithoutKeepingRef(handle);

        for (int i = 0; i < 8; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        try
        {
            Assert.Null(ProxyLifetimeTracker.ResolveImpl<IMockFace>(handle));
            // The entry itself is untouched — only Swift's deinit removes it, so the release
            // bookkeeping the proxy still owns cannot be lost by a collection.
            Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle));
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
        }
    }

    // No-inline sibling of TrackImplWithoutKeepingRef: after this returns, nothing anywhere
    // references the impl, so a collection is free to take it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void TrackConsumerOwnedImplWithoutKeepingRef(IntPtr handle)
    {
        var impl = new MockImpl();
        ProxyLifetimeTracker.TrackConsumerOwned(impl, handle);
        Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle));
    }

    [Fact]
    public void ConsumerOwnedTracking_CountsInImplRoots()
    {
        // ImplRootCount is the leak census's "one entry per live EveryProtocol box" reading, so a
        // consumer-owned carrier has to be counted the same as a Swift-rooted one — otherwise a
        // whole lane's leaks would be invisible to the census.
        var impl = new MockImpl();
        var handle = NewMockHandle();
        var before = ProxyLifetimeTracker.ImplRootCount;
        try
        {
            ProxyLifetimeTracker.TrackConsumerOwned(impl, handle);
            Assert.Equal(before + 1, ProxyLifetimeTracker.ImplRootCount);
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl);
        }
        Assert.Equal(before, ProxyLifetimeTracker.ImplRootCount);
    }

    [Fact]
    public void IsConsumerOwnedCarrier_DistinguishesTheTwoLanes()
    {
        // The bit a reverse-dispatch receiver reads to decide what an unresolvable impl MEANS. One
        // emitted receiver thunk is shared by every proxy of its protocol, so the lane cannot be
        // baked in at emission — it has to be a property of the conformer-box handle, answered here.
        var swiftRootedImpl = new MockImpl();
        var consumerOwnedImpl = new MockImpl();
        var swiftRootedHandle = NewMockHandle();
        var consumerOwnedHandle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(swiftRootedImpl, swiftRootedHandle);
            ProxyLifetimeTracker.TrackConsumerOwned(consumerOwnedImpl, consumerOwnedHandle);

            Assert.False(ProxyLifetimeTracker.IsConsumerOwnedCarrier(swiftRootedHandle));
            Assert.True(ProxyLifetimeTracker.IsConsumerOwnedCarrier(consumerOwnedHandle));
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(swiftRootedHandle);
            ProxyLifetimeTracker.DropForTest(consumerOwnedHandle);
            GC.KeepAlive(swiftRootedImpl);
            GC.KeepAlive(consumerOwnedImpl);
        }
    }

    [Fact]
    public void IsConsumerOwnedCarrier_UnknownOrZeroHandle_IsFalse()
    {
        // Deliberately false rather than "unknown": a handle nobody tracks is not evidence that a
        // degradation is legitimate, so dispatch through it stays on the loud Swift-rooted terminal.
        Assert.False(ProxyLifetimeTracker.IsConsumerOwnedCarrier(IntPtr.Zero));
        Assert.False(ProxyLifetimeTracker.IsConsumerOwnedCarrier(NewMockHandle()));
    }

    [Fact]
    public void IsConsumerOwnedCarrier_SurvivesTheImplBeingCollected()
    {
        // The lane bit has to outlive the impl it describes — the whole point is to answer the
        // question ASKED BY a callback that could not resolve the impl. Reading it off the (now
        // cleared) weak root would make every degraded callback look Swift-rooted and kill the
        // process, which is the bug this lane exists to avoid.
        var handle = NewMockHandle();
        TrackConsumerOwnedImplWithoutKeepingRef(handle);

        for (int i = 0; i < 8; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        try
        {
            Assert.Null(ProxyLifetimeTracker.ResolveImpl<IMockFace>(handle));
            Assert.True(ProxyLifetimeTracker.IsConsumerOwnedCarrier(handle));
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
        }
    }

    [Fact]
    public void TrackConsumerOwned_DuplicateHandle_Throws()
    {
        // Same transactional publication guarantee as Track, and across lanes: a handle already
        // tracked one way cannot be re-tracked the other, so the two maps cannot drift.
        var impl1 = new MockImpl();
        var impl2 = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.TrackConsumerOwned(impl1, handle);
            Assert.Throws<InvalidOperationException>(() =>
                ProxyLifetimeTracker.TrackConsumerOwned(impl2, handle));
            Assert.Throws<InvalidOperationException>(() =>
                ProxyLifetimeTracker.Track(impl2, handle));

            Assert.Same(impl1, ProxyLifetimeTracker.ResolveImpl<MockImpl>(handle));
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl1);
            GC.KeepAlive(impl2);
        }
    }

    [Fact]
    public void OnEveryProtocolDeinit_ConsumerOwnedRoot_FreesEntry()
    {
        using var scope = SwiftExitGuardTestScope.Enter(processExiting: false);

        var impl = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.TrackConsumerOwned(impl, handle);
            var before = ProxyLifetimeTracker.ImplRootCount;

            ProxyLifetimeTracker.OnEveryProtocolDeinitCore(handle);

            Assert.False(ProxyLifetimeTracker.IsTrackedForTest(handle));
            Assert.Null(ProxyLifetimeTracker.ResolveImpl<MockImpl>(handle));
            Assert.Equal(before - 1, ProxyLifetimeTracker.ImplRootCount);
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void Track_MultipleHandlesForSameImpl_AllTracked()
    {
        var impl = new MockImpl();
        var handle1 = NewMockHandle();
        var handle2 = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl, handle1);
            ProxyLifetimeTracker.Track(impl, handle2);

            Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle1));
            Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle2));
            Assert.Same(impl, ProxyLifetimeTracker.ResolveImpl<MockImpl>(handle1));
            Assert.Same(impl, ProxyLifetimeTracker.ResolveImpl<MockImpl>(handle2));
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle1);
            ProxyLifetimeTracker.DropForTest(handle2);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void Track_DuplicateHandle_Throws()
    {
        // Transactional publication: a duplicate handle must be rejected so the two maps cannot
        // drift out of sync (and impl2's GCHandle is never allocated, so nothing leaks).
        var impl1 = new MockImpl();
        var impl2 = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl1, handle);
            Assert.Throws<InvalidOperationException>(() =>
                ProxyLifetimeTracker.Track(impl2, handle));

            // First tracking is still intact — no rollback side effects on impl1.
            Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle));
            Assert.Same(impl1, ProxyLifetimeTracker.ResolveImpl<MockImpl>(handle));
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl1);
            GC.KeepAlive(impl2);
        }
    }

    [Fact]
    public void OnEveryProtocolDeinit_FreesImplRootAndDropsRegistry()
    {
        // Hold the flag-sync lock for the whole test — this exercises the *non-exiting* code path,
        // and without the lock a racing SwiftClassHandle test that toggles the flag could make
        // OnEveryProtocolDeinitCore short-circuit.
        using var scope = SwiftExitGuardTestScope.Enter(processExiting: false);

        var impl = new MockImpl();
        var proxy = new MockImpl(); // stands in for a proxy
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl, handle);
            SwiftObjectRegistry.RegisterStrong(handle, proxy);
            Assert.Equal(proxy, SwiftObjectRegistry.GetProxy<MockImpl>(handle));

            // Swift's last retain dropped: deinit frees the impl root, drops the registry root,
            // and scrubs the R0 entry. No native release is attempted (R0 was already gone).
            ProxyLifetimeTracker.OnEveryProtocolDeinitCore(handle);

            Assert.False(ProxyLifetimeTracker.IsTrackedForTest(handle));
            Assert.Null(ProxyLifetimeTracker.ResolveImpl<MockImpl>(handle));
            Assert.False(SwiftObjectRegistry.TryGetProxy<MockImpl>(handle, out _));
        }
        finally
        {
            SwiftObjectRegistry.Unregister(handle);
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl);
            GC.KeepAlive(proxy);
        }
    }

    [Fact]
    public void OnEveryProtocolDeinit_DropsTheDegradationReportLatch()
    {
        using var scope = SwiftExitGuardTestScope.Enter(processExiting: false);

        var impl = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl, handle);

            // This carrier has already reported a degraded callback, so it is latched silent.
            Assert.True(ProxyDegradation.ReportCollectedImpl(handle, "IMockFace.Ping()"));
            Assert.False(ProxyDegradation.ReportCollectedImpl(handle, "IMockFace.Ping()"));

            // Deinit must clear the degradation latch as well as the roots. Handle values come from
            // the allocator and are recycled, so a new conformer box landing on this address would
            // otherwise inherit "already reported" and its own first degradation would be silent —
            // the one diagnostic that makes a stopped delegate discoverable, lost for that carrier.
            ProxyLifetimeTracker.OnEveryProtocolDeinitCore(handle);

            Assert.True(ProxyDegradation.ReportCollectedImpl(handle, "IMockFace.Ping()"));
        }
        finally
        {
            ProxyDegradation.Forget(handle);
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void OnEveryProtocolDeinit_UnknownHandle_IsNoOp()
    {
        using var scope = SwiftExitGuardTestScope.Enter(processExiting: false);
        // Must not throw on an unknown handle — deinit races are fine.
        ProxyLifetimeTracker.OnEveryProtocolDeinitCore(new IntPtr(0xDEADBEEF));
    }

    [Fact]
    public void OnEveryProtocolDeinit_ProcessExiting_IsNoOp()
    {
        var impl = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl, handle);

            // During process exit, the deinit callback short-circuits so we don't touch a
            // partially-torn-down Swift runtime or free roots out from under it.
            using (SwiftExitGuardTestScope.Enter(processExiting: true))
            {
                ProxyLifetimeTracker.OnEveryProtocolDeinitCore(handle);

                // Still tracked from the tracker's perspective — shutdown leaks are fine.
                Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle));
            }
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void ReleaseHandle_ProcessExiting_IsNoOp()
    {
        // R0 release is the proxy's responsibility; during process exit it must short-circuit
        // BEFORE the native trampoline call (we deliberately leak rather than touch a torn-down
        // runtime), leaving the entry in place.
        var impl = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl, handle);
            using (SwiftExitGuardTestScope.Enter(processExiting: true))
            {
                ProxyLifetimeTracker.ReleaseHandle(handle); // no native release attempted
                Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle));
            }
        }
        finally
        {
            ProxyLifetimeTracker.DropForTest(handle);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void ReleaseHandle_UnknownHandle_IsNoOp()
    {
        using var scope = SwiftExitGuardTestScope.Enter(processExiting: false);
        // Unknown handle: no entry to claim, returns without touching the trampoline.
        ProxyLifetimeTracker.ReleaseHandle(new IntPtr(0xDEADBEEF));
        ProxyLifetimeTracker.ReleaseHandle(IntPtr.Zero);
    }
}
