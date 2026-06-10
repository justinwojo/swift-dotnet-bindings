// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Threading;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Behavioural coverage for <see cref="SwiftAsyncCallHolder"/> — the runtime helper that frees the
/// async-call holder array on every async termination path (S2 round-3 extraction). Two properties
/// are load-bearing and previously lived inlined in the emitters where they could not be tested:
///
/// <list type="number">
/// <item><b>Exception-safe.</b> Cleanup runs on the [UnmanagedCallersOnly] callback thread that
/// re-enters from native Swift. A throw escaping it unwinds into native and aborts the process
/// (SIGABRT). The realistic trigger is a user <see cref="IDisposable.Dispose"/> in a deferred list;
/// it must be swallowed and must not skip sibling frees. (The native release branches —
/// Arc.UnknownObjectRelease / NativeMemory.Free / DangerousRelease — cannot be safely driven from a unit test
/// because they require live Swift pointers; their per-slot guard is exercised here via the
/// zero-pointer skip path and covered end-to-end by BindingTests.)</item>
/// <item><b>Idempotent.</b> Each processed slot is nulled, so a second pass — the fault catch
/// re-running after the success path freed some slots and then threw — must be a no-op.</item>
/// </list>
/// </summary>
public class SwiftAsyncCallHolderTests
{
    private const string Tcs = "tcs-slot-0-never-touched";

    private sealed class CountingDisposable : IDisposable
    {
        public int DisposeCount;
        public void Dispose() => DisposeCount++;
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        public int DisposeCount;
        public void Dispose()
        {
            DisposeCount++;
            throw new InvalidOperationException("user Dispose threw");
        }
    }

    [Fact]
    public void Cleanup_DisposesAllDeferredItems_ExactlyOnce()
    {
        var a = new CountingDisposable();
        var b = new CountingDisposable();
        var list = new AsyncDeferredDisposeList();
        list.Items.Add(a);
        list.Items.Add(b);
        var holder = new object[] { Tcs, list };

        SwiftAsyncCallHolder.Cleanup(holder);

        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(1, b.DisposeCount);
    }

    [Fact]
    public void Cleanup_IsIdempotent_SecondPassDoesNotReDispose()
    {
        var a = new CountingDisposable();
        var list = new AsyncDeferredDisposeList();
        list.Items.Add(a);
        var holder = new object[] { Tcs, list };

        SwiftAsyncCallHolder.Cleanup(holder);
        SwiftAsyncCallHolder.Cleanup(holder); // re-run, e.g. fault catch after partial success

        Assert.Equal(1, a.DisposeCount);
        // The processed slot was nulled so the second pass found nothing to free.
        Assert.Null(holder[1]);
    }

    [Fact]
    public void Cleanup_NullsEveryProcessedSlot_ButLeavesSlotZero()
    {
        var holder = new object[]
        {
            Tcs,
            new AsyncDeferredDisposeList(),
            new RetainedSelfPtr(IntPtr.Zero),
            new ExistentialContainerHeap(IntPtr.Zero),
            new CopyBufferWithType(IntPtr.Zero, default),
        };

        SwiftAsyncCallHolder.Cleanup(holder);

        Assert.Same(Tcs, holder[0]); // slot 0 (the TaskCompletionSource) is never freed here
        for (int i = 1; i < holder.Length; i++)
            Assert.Null(holder[i]);
    }

    [Fact]
    public void Cleanup_DoesNotThrow_WhenUserDisposeThrows_AndStillDisposesSiblings()
    {
        // A faulting user Dispose is the realistic UCO-escape trigger the extraction guards against.
        // The throw must be swallowed (no escape into native Swift → no SIGABRT) and the sibling
        // item after it must still be disposed.
        var throwing = new ThrowingDisposable();
        var sibling = new CountingDisposable();
        var list = new AsyncDeferredDisposeList();
        list.Items.Add(throwing);
        list.Items.Add(sibling);
        var holder = new object[] { Tcs, list };

        var ex = Record.Exception(() => SwiftAsyncCallHolder.Cleanup(holder));

        Assert.Null(ex); // never propagates
        Assert.Equal(1, throwing.DisposeCount);
        Assert.Equal(1, sibling.DisposeCount); // sibling freed despite the earlier throw
        Assert.Null(holder[1]);
    }

    [Fact]
    public void Cleanup_SkipsZeroPointerAndNullSlots_WithoutThrowing()
    {
        // Zero-pointer payloads (guarded by `Ptr != IntPtr.Zero` / `Buffer != IntPtr.Zero`) and a
        // bare null slot must be skipped without touching native memory or throwing.
        var holder = new object[]
        {
            Tcs,
            new RetainedSelfPtr(IntPtr.Zero),
            new ExistentialContainerHeap(IntPtr.Zero),
            new CopyBufferWithType(IntPtr.Zero, default),
            null!,
        };

        var ex = Record.Exception(() => SwiftAsyncCallHolder.Cleanup(holder));

        Assert.Null(ex);
    }

    [Fact]
    public void Cleanup_DisposesCancellationRegistration()
    {
        // Disposing the registration unregisters the callback. Prove it ran: after Cleanup, a
        // subsequent Cancel must NOT fire the callback.
        using var cts = new CancellationTokenSource();
        bool fired = false;
        var registration = cts.Token.Register(() => fired = true);
        var holder = new object[] { Tcs, new CancellationRegistrationHolder(registration, cts.Token) };

        SwiftAsyncCallHolder.Cleanup(holder);
        cts.Cancel();

        Assert.False(fired); // registration was disposed, so the callback is gone
        Assert.Null(holder[1]);
    }

    [Fact]
    public void Cleanup_RespectsStartIndex()
    {
        var atZeroPlus = new CountingDisposable();
        var skipped = new CountingDisposable();
        var listSkipped = new AsyncDeferredDisposeList();
        listSkipped.Items.Add(skipped);
        var listFreed = new AsyncDeferredDisposeList();
        listFreed.Items.Add(atZeroPlus);
        var holder = new object[] { Tcs, listSkipped, listFreed };

        SwiftAsyncCallHolder.Cleanup(holder, startIndex: 2);

        Assert.Equal(0, skipped.DisposeCount); // slot 1 skipped
        Assert.Equal(1, atZeroPlus.DisposeCount); // slot 2 freed
        Assert.NotNull(holder[1]); // skipped slot untouched
        Assert.Null(holder[2]);
    }

    [Fact]
    public void CaptureCancellationToken_ReturnsRegisteredToken_AndDoesNotMutateHolder()
    {
        using var cts = new CancellationTokenSource();
        var registration = cts.Token.Register(() => { });
        var regHolder = new CancellationRegistrationHolder(registration, cts.Token);
        var holder = new object[] { Tcs, new RetainedSelfPtr(IntPtr.Zero), regHolder };

        var captured = SwiftAsyncCallHolder.CaptureCancellationToken(holder);

        Assert.Equal(cts.Token, captured);
        // Read-only: the registration slot must survive so Cleanup can later dispose it.
        Assert.IsType<CancellationRegistrationHolder>(holder[2]);
        registration.Dispose();
    }

    [Fact]
    public void CaptureCancellationToken_ReturnsDefault_WhenNoRegistration()
    {
        var holder = new object[] { Tcs, new RetainedSelfPtr(IntPtr.Zero), new AsyncDeferredDisposeList() };

        var captured = SwiftAsyncCallHolder.CaptureCancellationToken(holder);

        Assert.Equal(default, captured);
    }
}
