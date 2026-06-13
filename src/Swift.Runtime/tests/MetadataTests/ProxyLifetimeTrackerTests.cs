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
