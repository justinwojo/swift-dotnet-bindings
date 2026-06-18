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
/// async-call holder on every async termination path. It is now a typed <c>sealed class</c> with
/// named fields rather than an untyped <c>object[]</c>; an emitter that stashes an unrecognized
/// resource has no field for it (a compile error) instead of the silent leak the positional walk
/// allowed. Two properties remain load-bearing and previously lived inlined in the emitters where
/// they could not be tested:
///
/// <list type="number">
/// <item><b>Exception-safe.</b> Cleanup runs on the [UnmanagedCallersOnly] callback thread that
/// re-enters from native Swift. A throw escaping it unwinds into native and aborts the process
/// (SIGABRT). The realistic trigger is a user <see cref="IDisposable.Dispose"/> in a deferred list;
/// it must be swallowed and must not skip sibling frees. (The native release branches —
/// Arc.UnknownObjectRelease / NativeMemory.Free / DangerousRelease — cannot be safely driven from a unit test
/// because they require live Swift pointers; their per-field guard is exercised here via the
/// zero-pointer skip path and covered end-to-end by BindingTests.)</item>
/// <item><b>Idempotent.</b> Each processed field is cleared (nulled / list emptied), so a second
/// pass — the fault catch re-running after the success path freed some fields and then threw — must
/// be a no-op.</item>
/// </list>
/// </summary>
public class SwiftAsyncCallHolderTests
{
    private const string Tcs = "tcs-never-touched";

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
        var holder = new SwiftAsyncCallHolder { Tcs = Tcs, DeferredDisposes = list };

        holder.Cleanup();

        Assert.Equal(1, a.DisposeCount);
        Assert.Equal(1, b.DisposeCount);
    }

    [Fact]
    public void Cleanup_IsIdempotent_SecondPassDoesNotReDispose()
    {
        var a = new CountingDisposable();
        var list = new AsyncDeferredDisposeList();
        list.Items.Add(a);
        var holder = new SwiftAsyncCallHolder { Tcs = Tcs, DeferredDisposes = list };

        holder.Cleanup();
        holder.Cleanup(); // re-run, e.g. fault catch after partial success

        Assert.Equal(1, a.DisposeCount);
        // The processed field was nulled so the second pass found nothing to free.
        Assert.Null(holder.DeferredDisposes);
    }

    [Fact]
    public void Cleanup_ClearsEveryProcessedField_ButLeavesTcs()
    {
        var holder = new SwiftAsyncCallHolder
        {
            Tcs = Tcs,
            DeferredDisposes = new AsyncDeferredDisposeList(),
            SelfRetain = new RetainedSelfPtr(IntPtr.Zero),
        };
        holder.ExistentialHeaps.Add(new ExistentialContainerHeap(IntPtr.Zero));
        holder.CopyBuffers.Add(new CopyBufferWithType(IntPtr.Zero, default));
        holder.KeepAlives.Add(new object());

        holder.Cleanup();

        Assert.Same(Tcs, holder.Tcs); // the TaskCompletionSource is never freed here
        Assert.Null(holder.DeferredDisposes);
        Assert.Null(holder.SelfRetain);
        Assert.Empty(holder.ExistentialHeaps);
        Assert.Empty(holder.CopyBuffers);
        Assert.Empty(holder.KeepAlives);
    }

    [Fact]
    public void Cleanup_DoesNotThrow_WhenUserDisposeThrows_AndStillDisposesSiblings()
    {
        // A faulting user Dispose is the realistic UCO-escape trigger the helper guards against.
        // The throw must be swallowed (no escape into native Swift → no SIGABRT) and the sibling
        // item after it must still be disposed.
        var throwing = new ThrowingDisposable();
        var sibling = new CountingDisposable();
        var list = new AsyncDeferredDisposeList();
        list.Items.Add(throwing);
        list.Items.Add(sibling);
        var holder = new SwiftAsyncCallHolder { Tcs = Tcs, DeferredDisposes = list };

        var ex = Record.Exception(() => holder.Cleanup());

        Assert.Null(ex); // never propagates
        Assert.Equal(1, throwing.DisposeCount);
        Assert.Equal(1, sibling.DisposeCount); // sibling freed despite the earlier throw
        Assert.Null(holder.DeferredDisposes);
    }

    [Fact]
    public void Cleanup_SkipsZeroPointerAndEmptyFields_WithoutThrowing()
    {
        // Zero-pointer payloads (guarded by `Ptr != IntPtr.Zero` / `Buffer != IntPtr.Zero`) and
        // absent fields must be skipped without touching native memory or throwing.
        var holder = new SwiftAsyncCallHolder { Tcs = Tcs, SelfRetain = new RetainedSelfPtr(IntPtr.Zero) };
        holder.ExistentialHeaps.Add(new ExistentialContainerHeap(IntPtr.Zero));
        holder.CopyBuffers.Add(new CopyBufferWithType(IntPtr.Zero, default));

        var ex = Record.Exception(() => holder.Cleanup());

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
        var holder = new SwiftAsyncCallHolder
        {
            Tcs = Tcs,
            CancellationRegistration = new CancellationRegistrationHolder(registration, cts.Token),
        };

        holder.Cleanup();
        cts.Cancel();

        Assert.False(fired); // registration was disposed, so the callback is gone
        Assert.Null(holder.CancellationRegistration);
    }

    [Fact]
    public void CaptureCancellationToken_ReturnsRegisteredToken_AndDoesNotMutateHolder()
    {
        using var cts = new CancellationTokenSource();
        var registration = cts.Token.Register(() => { });
        var holder = new SwiftAsyncCallHolder
        {
            Tcs = Tcs,
            SelfRetain = new RetainedSelfPtr(IntPtr.Zero),
            CancellationRegistration = new CancellationRegistrationHolder(registration, cts.Token),
        };

        var captured = holder.CaptureCancellationToken();

        Assert.Equal(cts.Token, captured);
        // Read-only: the registration must survive so Cleanup can later dispose it.
        Assert.NotNull(holder.CancellationRegistration);
        registration.Dispose();
    }

    [Fact]
    public void Cleanup_WithCancellationRegistrationAndDeferredList_ClearsBothIdempotently()
    {
        // Cross-field idempotency: the fault catch can re-run Cleanup after a partially-completed
        // success path. A second pass must neither re-dispose nor throw, with every field cleared.
        using var cts = new CancellationTokenSource();
        var disposable = new CountingDisposable();
        var list = new AsyncDeferredDisposeList();
        list.Items.Add(disposable);
        var registration = cts.Token.Register(() => { });
        var holder = new SwiftAsyncCallHolder
        {
            Tcs = Tcs,
            DeferredDisposes = list,
            CancellationRegistration = new CancellationRegistrationHolder(registration, cts.Token),
        };

        holder.Cleanup();
        holder.Cleanup();

        Assert.Equal(1, disposable.DisposeCount);
        Assert.Null(holder.DeferredDisposes);
        Assert.Null(holder.CancellationRegistration);
    }

    [Fact]
    public void Cleanup_ClearsKeepAlives_ButNeverDisposesThem()
    {
        // Keep-alives are pure GC roots (the receiver 'this', original parameter objects). Cleanup
        // must release them for collection but must NOT treat them as disposables.
        var keptAlive = new CountingDisposable();
        var holder = new SwiftAsyncCallHolder { Tcs = Tcs };
        holder.KeepAlives.Add(keptAlive);

        holder.Cleanup();

        Assert.Empty(holder.KeepAlives);
        Assert.Equal(0, keptAlive.DisposeCount); // keep-alives are roots, not disposables
    }

    [Fact]
    public void CaptureCancellationToken_ReturnsDefault_WhenNoRegistration()
    {
        var holder = new SwiftAsyncCallHolder
        {
            Tcs = Tcs,
            SelfRetain = new RetainedSelfPtr(IntPtr.Zero),
            DeferredDisposes = new AsyncDeferredDisposeList(),
        };

        var captured = holder.CaptureCancellationToken();

        Assert.Equal(default, captured);
    }
}
