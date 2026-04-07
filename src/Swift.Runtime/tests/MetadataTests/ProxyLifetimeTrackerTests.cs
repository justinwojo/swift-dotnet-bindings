// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using Swift.Runtime;
using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// Unit tests for <see cref="ProxyLifetimeTracker"/>'s bookkeeping logic.
/// Real ARC release verification happens in BindingTests runtime tests (ProxyLifetimeTests.cs) —
/// these unit tests cover the managed data-structure side only, using <see cref="SwiftExitGuard"/>
/// to short-circuit release calls on mock pointers.
/// </summary>
/// <remarks>
/// Uses xunit collection serialization because <c>SwiftExitGuard.SetProcessExitingForTest</c>
/// mutates a process-global flag. Parallel execution with other tests in this class
/// (or the existing <c>SwiftClassHandle</c> tests) would race on the flag and produce
/// non-deterministic failures.
/// </remarks>
public class ProxyLifetimeTrackerTests
{
    // Mock "handle" values — never dereferenced. The process-exit guard prevents
    // Arc.Release from being called on these when the tracker's finalizer runs.
    private static IntPtr NewMockHandle() => new IntPtr(Random.Shared.NextInt64(0x10000, long.MaxValue));

    private sealed class MockImpl { }

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
            // Drain the tracker entry during process-exit mode so the finalizer
            // doesn't later try to Arc.Release(mockHandle).
            ProxyLifetimeTracker.TryDropAllForTest(impl);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void NotifyDeinit_RemovesHandleFromTracker()
    {
        var impl = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl, handle);
            Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle));

            ProxyLifetimeTracker.NotifyDeinit(handle);
            Assert.False(ProxyLifetimeTracker.IsTrackedForTest(handle));
        }
        finally
        {
            ProxyLifetimeTracker.TryDropAllForTest(impl);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void NotifyDeinit_UnknownHandle_IsNoOp()
    {
        // Must not throw on an unknown handle — deinit races are fine.
        ProxyLifetimeTracker.NotifyDeinit(new IntPtr(0xDEADBEEF));
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
        }
        finally
        {
            ProxyLifetimeTracker.TryDropAllForTest(impl);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void OnEveryProtocolDeinit_ProcessExiting_IsNoOp()
    {
        var impl = new MockImpl();
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl, handle);

            // During process exit, the deinit callback short-circuits so we don't
            // touch a partially-torn-down Swift runtime or managed state.
            SwiftExitGuard.SetProcessExitingForTest(true);
            ProxyLifetimeTracker.OnEveryProtocolDeinitCore(handle);

            // The handle is still "tracked" from the tracker's perspective because
            // NotifyDeinit was skipped — this is expected (shutdown leaks are fine).
            Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle));
        }
        finally
        {
            // Drop the entry so the finalizer doesn't try to Arc.Release a mock handle.
            ProxyLifetimeTracker.TryDropAllForTest(impl);
            SwiftExitGuard.SetProcessExitingForTest(false);
            GC.KeepAlive(impl);
        }
    }

    [Fact]
    public void OnEveryProtocolDeinit_RemovesRegistryAndHandle()
    {
        // Guard against a concurrent test leaving the process-exit flag set —
        // this test exercises the *non-exiting* code path, and any racing
        // SwiftClassHandle test that toggles the flag would otherwise make
        // OnEveryProtocolDeinitCore short-circuit.
        SwiftExitGuard.SetProcessExitingForTest(false);

        // Register a dummy proxy in the strong registry; OnEveryProtocolDeinit
        // should drop both the strong registry root and the tracker entry.
        var impl = new MockImpl();
        var proxy = new MockImpl(); // stands in for a proxy
        var handle = NewMockHandle();
        try
        {
            ProxyLifetimeTracker.Track(impl, handle);
            SwiftObjectRegistry.RegisterStrong(handle, proxy);
            Assert.Equal(proxy, SwiftObjectRegistry.GetProxy<MockImpl>(handle));

            ProxyLifetimeTracker.OnEveryProtocolDeinitCore(handle);

            Assert.False(ProxyLifetimeTracker.IsTrackedForTest(handle));
            Assert.False(SwiftObjectRegistry.TryGetProxy<MockImpl>(handle, out _));
        }
        finally
        {
            SwiftObjectRegistry.Unregister(handle);
            ProxyLifetimeTracker.TryDropAllForTest(impl);
            GC.KeepAlive(impl);
            GC.KeepAlive(proxy);
        }
    }

    [Fact]
    public void Track_DuplicateHandle_Throws()
    {
        // Transactional publication: a duplicate handle must be rejected so the
        // secondary map / cleanup list cannot drift out of sync.
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
        }
        finally
        {
            ProxyLifetimeTracker.TryDropAllForTest(impl1);
            ProxyLifetimeTracker.TryDropAllForTest(impl2);
            GC.KeepAlive(impl1);
            GC.KeepAlive(impl2);
        }
    }

    [Fact]
    public void NotifyDeinit_DetachesEvenWhenImplIsAlreadyCollected()
    {
        // Regression for Codex P1 #2: the secondary map must hold a direct reference
        // to the per-handle entry (not a WeakReference<impl>), so NotifyDeinit can
        // still detach the handle from the cleanup bundle after the impl itself is
        // already garbage-collected. Otherwise the finalizer would later re-Release
        // the handle (or never release at all).
        //
        // Test ordering matters: we must FIRST drive the impl to GC-collected state,
        // THEN call NotifyDeinit. The previous version of this test called
        // NotifyDeinit before forcing GC and therefore did not actually exercise
        // the dead-impl path it claimed to cover (Codex P2 #2).
        //
        // Mock-handle safety: ProxyCleanup's finalizer fires when impl is collected
        // and (without the process-exit guard) would call Arc.Release on the mock
        // pointer — undefined behaviour. We deliberately set IsProcessExiting = true
        // for the duration of the GC cycles so the finalizer short-circuits without
        // touching the mock handle. NotifyDeinit's own logic does NOT check the
        // process-exit flag, so the dead-impl detach path is still exercised.
        var handle = NewMockHandle();
        var weakImpl = TrackAndReturnWeakRef(handle);

        // Cleanup finalizer would otherwise run swift_release on the mock pointer.
        SwiftExitGuard.SetProcessExitingForTest(true);
        try
        {
            // Drive the impl to "definitely collected" state. Conservative-stack-scan
            // GCs (like Mono iOS sim) may keep the local alive across a single GC
            // cycle, so we loop up to a small bound and bail out as soon as the
            // WeakReference reports the target is gone. This is a unit test running
            // on the .NET 10 server GC, which is precise — typically 1-2 cycles.
            for (int i = 0; i < 8 && weakImpl.IsAlive; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            Assert.False(weakImpl.IsAlive,
                "Impl should be GC'd before NotifyDeinit runs — otherwise this test does NOT cover the dead-impl path it claims to.");
        }
        finally
        {
            SwiftExitGuard.SetProcessExitingForTest(false);
        }

        // CRITICAL: by the time NotifyDeinit runs, the impl object is already gone.
        // The pre-fix design stored a WeakReference<impl> in the secondary map and
        // would have failed to find the cleanup entry here, leaving the handle in
        // ProxyCleanup's _entries list for the finalizer to (eventually) re-release.
        // The fixed design holds the per-handle state directly and detaches cleanly.
        ProxyLifetimeTracker.NotifyDeinit(handle);

        Assert.False(ProxyLifetimeTracker.IsTrackedForTest(handle),
            "NotifyDeinit must detach the handle even though the owning impl was already collected.");
    }

    // No-inline helper so the impl reference cannot be kept alive by the test
    // method's stack frame after it returns. Returns a WeakReference so the
    // test can observe when the impl actually becomes collected.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference TrackAndReturnWeakRef(IntPtr handle)
    {
        var impl = new MockImpl();
        ProxyLifetimeTracker.Track(impl, handle);
        Assert.True(ProxyLifetimeTracker.IsTrackedForTest(handle));
        // Return a non-tracking WeakReference. impl is NOT KeptAlive past this
        // statement, so the only managed reference to it is the CWT key (weak),
        // and the cleanup becomes eligible for finalization.
        return new WeakReference(impl);
    }
}
