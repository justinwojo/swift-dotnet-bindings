// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// Tests for the two SafeHandle lifetime helpers added in 0.10.0 Bundle 01:
/// <see cref="DeferredSafeHandleRelease"/> (the async-cleanup holder used by every
/// async wrapper) and <see cref="SafeHandlePin"/> (the synchronous AddRef/Release
/// bracket used by emitted Equals overloads and bound-generic argument extraction
/// in the wrapper-marshalling path).
///
/// Both helpers exist so call sites that previously called
/// <see cref="SafeHandle.DangerousRelease"/> (deferred) or
/// <see cref="SafeHandle.DangerousGetHandle"/> (synchronous) without a balancing
/// <see cref="SafeHandle.DangerousAddRef(ref bool)"/> get the GC-pinning bracket
/// that property getters already enforce. Without the bracket, a concurrent GC
/// finalization between the handle access and the Swift function entry can free
/// the Swift heap payload mid-call (sync) or underflow the SafeHandle's
/// refcount on cancellation paths that run cleanup before any Swift continuation
/// lands (async).
///
/// These tests use a <see cref="CountingSafeHandle"/> mock — a SafeHandle whose
/// <c>ReleaseHandle</c> increments a counter, so we can assert the helper does
/// NOT prematurely release the underlying handle. We also assert that
/// <see cref="DeferredSafeHandleRelease"/> throws on a closed handle (the
/// AddRef-on-closed-handle contract) and that <see cref="SafeHandlePin"/>
/// re-exposes the original pointer through its <c>Handle</c> property.
/// </summary>
public class SafeHandleLifetimeHelpersTests
{
    /// <summary>
    /// Mock SafeHandle that tracks how many times <c>ReleaseHandle</c> ran.
    /// We never give it a real OS resource — the pointer is a fixed sentinel
    /// chosen so it cannot collide with a real allocation.
    /// </summary>
    private sealed class CountingSafeHandle : SafeHandle
    {
        public int ReleaseHandleCount { get; private set; }

        public CountingSafeHandle(IntPtr value) : base(IntPtr.Zero, ownsHandle: true)
        {
            SetHandle(value);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            ReleaseHandleCount++;
            return true;
        }
    }

    // ---- DeferredSafeHandleRelease ------------------------------------------

    [Fact]
    public void DeferredSafeHandleRelease_Constructor_TakesRefcount_NoPrematureReleaseHandle()
    {
        // Sentinel pointer (not a real allocation; ReleaseHandle is a no-op
        // counter, so dereferencing never happens).
        var sh = new CountingSafeHandle(new IntPtr(0xDEAF_BEEF));

        // Constructor must add a refcount; without it the next DangerousRelease
        // call would underflow.
        var holder = new DeferredSafeHandleRelease(sh);

        // Helper exposes the same SafeHandle without copying it.
        Assert.Same(sh, holder.Handle);

        // Holder existing alone must NOT have triggered ReleaseHandle —
        // the AddRef simply incremented the internal refcount.
        Assert.Equal(0, sh.ReleaseHandleCount);

        // Balance with the explicit DangerousRelease the async cleanup loop
        // calls. Refcount returns to 1 (caller's baseline), and Dispose then
        // hits zero and runs ReleaseHandle exactly once.
        sh.DangerousRelease();
        Assert.Equal(0, sh.ReleaseHandleCount);

        sh.Dispose();
        Assert.Equal(1, sh.ReleaseHandleCount);
    }

    [Fact]
    public void DeferredSafeHandleRelease_BalancesAddRef_RefcountReturnsToBaseline()
    {
        // Foreground caller's baseline: handle alive, refcount 1 (the
        // construction count). The async holder takes a +1, the cleanup
        // loop calls DangerousRelease, the foreground caller eventually
        // disposes — no underflow, ReleaseHandle runs exactly once.
        var sh = new CountingSafeHandle(new IntPtr(0xFEED_F00D));

        var holder = new DeferredSafeHandleRelease(sh);
        sh.DangerousRelease();              // simulates AsyncHelpers cleanup loop
        sh.Dispose();                       // foreground caller dispose

        Assert.Equal(1, sh.ReleaseHandleCount);
    }

    [Fact]
    public void DeferredSafeHandleRelease_OnClosedHandle_Throws()
    {
        // After Dispose, DangerousAddRef must throw on a closed handle —
        // ObjectDisposedException is the documented contract for SafeHandle.
        // Bundle 01's holder must propagate that failure so the async
        // wrapper surfaces it as a faulted Task to the consumer (correct:
        // a disposed receiver cannot back the in-flight call).
        var sh = new CountingSafeHandle(new IntPtr(0xABCD_1234));
        sh.Dispose();
        Assert.Equal(1, sh.ReleaseHandleCount);

        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = new DeferredSafeHandleRelease(sh);
        });
    }

    // ---- SafeHandlePin ------------------------------------------------------

    [Fact]
    public void SafeHandlePin_Constructor_AddsRef_HandlePropertyReturnsRawPointer()
    {
        var sh = new CountingSafeHandle(new IntPtr(0xC0DE_1000));

        // Pin scope: ctor takes a +1; the Handle property exposes the raw
        // pointer for the Swift function to dereference safely (no concurrent
        // GC finalization can free the underlying allocation).
        using (var pin = new SafeHandlePin(sh))
        {
            Assert.Equal(new IntPtr(0xC0DE_1000), pin.Handle);
            Assert.Equal(0, sh.ReleaseHandleCount);
        }

        // Disposing the pin runs the matching Release without firing
        // ReleaseHandle (the original SafeHandle is still held by the test
        // root). Foreground Dispose runs ReleaseHandle exactly once.
        sh.Dispose();
        Assert.Equal(1, sh.ReleaseHandleCount);
    }

    [Fact]
    public void SafeHandlePin_DoubleDispose_ReleasesOnlyOnce()
    {
        var sh = new CountingSafeHandle(new IntPtr(0xC0DE_2000));
        var pin = new SafeHandlePin(sh);

        pin.Dispose();
        // Second Dispose must be a no-op — calling DangerousRelease twice
        // would underflow the refcount.
        pin.Dispose();

        sh.Dispose();
        // ReleaseHandle ran exactly once total, matching the single
        // outstanding refcount on the foreground caller.
        Assert.Equal(1, sh.ReleaseHandleCount);
    }

    [Fact]
    public void SafeHandlePin_NestedAroundDangerousRelease_DoesNotReleaseHandle()
    {
        // Models the emitted Equals(other) shape: two pin scopes around a
        // PInvoke, both holding the underlying handle alive while Swift reads.
        // Concurrent GC finalization mid-PInvoke cannot free either side
        // because both refcounts are non-zero.
        var lhs = new CountingSafeHandle(new IntPtr(0xAA00_AA00));
        var rhs = new CountingSafeHandle(new IntPtr(0xBB00_BB00));

        using (var lhsPin = new SafeHandlePin(lhs))
        using (var rhsPin = new SafeHandlePin(rhs))
        {
            // PInvoke would happen here. Pin pointers are stable for the
            // duration of the using scope.
            Assert.Equal(new IntPtr(0xAA00_AA00), lhsPin.Handle);
            Assert.Equal(new IntPtr(0xBB00_BB00), rhsPin.Handle);

            // Foreground refcount remains at the baseline (+1 caller, +1 pin
            // = 2 each); ReleaseHandle has not run.
            Assert.Equal(0, lhs.ReleaseHandleCount);
            Assert.Equal(0, rhs.ReleaseHandleCount);
        }

        // Pins disposed (each released its +1) — ReleaseHandle still has not
        // run because the test root refcount remains at 1 each.
        Assert.Equal(0, lhs.ReleaseHandleCount);
        Assert.Equal(0, rhs.ReleaseHandleCount);

        lhs.Dispose();
        rhs.Dispose();
        Assert.Equal(1, lhs.ReleaseHandleCount);
        Assert.Equal(1, rhs.ReleaseHandleCount);
    }
}
