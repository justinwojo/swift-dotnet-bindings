// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Layer A coverage for <see cref="ClosureHandle"/>. Exercises both lifetime
/// policies plus the default-constructed / double-dispose corners that the
/// generator's optional-closure paths rely on.
/// </summary>
public class ClosureHandleTests
{
    private sealed class Sentinel
    {
        public int Value;
    }

    [Fact]
    public void NonEscaping_DisposeFreesHandle()
    {
        var sentinel = new Sentinel { Value = 17 };
        var handle = new ClosureHandle(sentinel, ClosureHandlePolicy.NonEscaping);

        Assert.True(handle.IsAllocated);
        Assert.NotEqual(IntPtr.Zero, handle.Context);
        // Round-trip the pointer back through the runtime to prove it still
        // refers to the sentinel before disposal.
        var roundTrip = (Sentinel?)GCHandle.FromIntPtr(handle.Context).Target;
        Assert.Same(sentinel, roundTrip);

        handle.Dispose();
        Assert.False(handle.IsAllocated);
        Assert.Equal(IntPtr.Zero, handle.Context);
    }

    [Fact]
    public void NonEscaping_MarkOwnershipTransferred_IsIgnored()
    {
        // Non-escaping closures never transfer ownership — Swift never retains
        // the trampoline past the call. MarkOwnershipTransferred must be a
        // no-op so a misuse from generated code doesn't leak the handle.
        var handle = new ClosureHandle(new Sentinel(), ClosureHandlePolicy.NonEscaping);

        handle.MarkOwnershipTransferred();
        handle.Dispose();

        Assert.False(handle.IsAllocated);
    }

    [Fact]
    public void NonEscaping_DoubleDispose_DoesNotThrow()
    {
        var handle = new ClosureHandle(new Sentinel(), ClosureHandlePolicy.NonEscaping);
        handle.Dispose();
        handle.Dispose(); // must not throw — finally blocks may run twice on nested exceptions.
        Assert.False(handle.IsAllocated);
    }

    [Fact]
    public void Escaping_MarkOwnershipTransferred_ThenDispose_LeavesHandleAllocated()
    {
        // The Swift-side `_SBClosureCtx` box now owns the handle — Dispose must
        // not free it (Swift's deinit upcall will).
        var handle = new ClosureHandle(new Sentinel(), ClosureHandlePolicy.Escaping);
        var ctx = handle.Context;
        Assert.NotEqual(IntPtr.Zero, ctx);

        handle.MarkOwnershipTransferred();
        handle.Dispose();

        Assert.True(handle.IsAllocated);

        // Free manually so the unit-test process doesn't leak the GCHandle.
        // In production Swift's deinit upcall handles this.
        GCHandle.FromIntPtr(ctx).Free();
    }

    [Fact]
    public void Escaping_NoTransfer_DisposeFreesHandle()
    {
        // P/Invoke threw before MarkOwnershipTransferred ran — wrapper still
        // owns the handle and must free it locally.
        var handle = new ClosureHandle(new Sentinel(), ClosureHandlePolicy.Escaping);
        handle.Dispose();
        Assert.False(handle.IsAllocated);
    }

    [Fact]
    public void Escaping_DoubleDispose_DoesNotThrow()
    {
        var handle = new ClosureHandle(new Sentinel(), ClosureHandlePolicy.Escaping);
        handle.Dispose();
        handle.Dispose();
        Assert.False(handle.IsAllocated);
    }

    [Fact]
    public void Escaping_TransferThenDoubleDispose_DoesNotFreeRetainedHandle()
    {
        // Second dispose must NOT undo the suppression — otherwise an
        // exception thrown inside the finally block (e.g. a downstream
        // Dispose) could re-enter and free the handle that Swift now owns.
        var handle = new ClosureHandle(new Sentinel(), ClosureHandlePolicy.Escaping);
        var ctx = handle.Context;

        handle.MarkOwnershipTransferred();
        handle.Dispose();
        handle.Dispose();

        Assert.True(handle.IsAllocated);
        GCHandle.FromIntPtr(ctx).Free();
    }

    [Fact]
    public void Default_DisposeIsSafe()
    {
        // Optional-closure emit sites pre-declare `ClosureHandle __gcHandle = default;`
        // so the finally can dispose unconditionally even when the caller
        // passed null and no handle was ever allocated.
        ClosureHandle handle = default;
        Assert.False(handle.IsAllocated);
        Assert.Equal(IntPtr.Zero, handle.Context);

        handle.Dispose();
        handle.Dispose();
        Assert.False(handle.IsAllocated);
    }

    [Fact]
    public void CopiedStruct_OriginalDisposeDoesNotPropagateToCopy_DocumentingNoCopyContract()
    {
        // ClosureHandle is a mutable struct wrapping an owning GCHandle. The
        // _disposed flag lives on the instance, so a copy carries its own flag
        // and the original's Dispose does not flip it. The copy still thinks
        // the underlying token is live — which is dangerous because the
        // original already freed it. The runtime's behavior on Dispose()ing
        // the stale copy is undefined (NET 10 silently no-ops; older runtimes
        // throw); either way the state is incoherent.
        //
        // Generated code keeps the instance as a single local and never
        // copies it. This test pins the no-copy contract by demonstrating the
        // observable divergence — if anyone ever makes the type a class for
        // shared ownership, both `handle.IsAllocated` and `copy.IsAllocated`
        // would agree after the original's Dispose, and this assertion would
        // fail, forcing a docs update.
        var handle = new ClosureHandle(new Sentinel(), ClosureHandlePolicy.NonEscaping);
        var copy = handle;

        handle.Dispose();

        Assert.False(handle.IsAllocated, "Original reports freed after Dispose");
        Assert.True(copy.IsAllocated,
            "Copy still holds the stale handle token — proves the do-not-copy contract");
    }
}
