// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Swift.Runtime;
using Swift.Runtime.InteropServices;

// RuntimeTestsApp's end-to-end AsyncStream lifetime probe reads IsContextHandleAllocated to assert that
// completion frees the rooting GCHandle deterministically — Mono's conservative stack scan makes
// weak-reference collectability an unreliable proxy for handle-freedom on the simulator. Mirrors the
// internal access the Swift.Runtime unit tests already use for the same invariant. The Mac/Catalyst/tvOS
// runner projects compile the same Async/**/*.cs sources into their own assemblies, so each needs access.
[assembly: InternalsVisibleTo("RuntimeTestsApp")]
[assembly: InternalsVisibleTo("RuntimeTestsApp.Mac")]
[assembly: InternalsVisibleTo("RuntimeTestsApp.MacCatalyst")]
[assembly: InternalsVisibleTo("RuntimeTestsApp.tvOS")]

namespace Swift;

/// <summary>
/// Represents a Swift AsyncStream as a C# IAsyncEnumerable.
/// This type bridges Swift's async iteration model with C#'s IAsyncEnumerable pattern.
///
/// Usage pattern:
/// 1. Swift code provides a function pointer that starts iteration
/// 2. For each element, Swift calls the element callback with the element data
/// 3. When iteration completes, Swift calls the completion callback
/// 4. C# code consumes elements via IAsyncEnumerable
///
/// <para><b>Context-handle lifetime (Defect I).</b> <see cref="GetContext"/> pins this instance
/// with a strong <see cref="GCHandle"/> so Swift can resolve it from the opaque context across
/// element and completion callbacks. That handle is the ONLY root keeping the instance alive while
/// Swift iterates, so it can be freed only when Swift guarantees it will not call back again. The
/// emitted Swift wrapper always invokes the completion callback LAST — after the element loop ends,
/// whether it finished normally or broke early (a faulted element callback returns "stop" and the
/// loop still falls through to completion). Completion is therefore the single point at which no
/// further callback can resolve the context, so the free is owned EXCLUSIVELY by
/// <see cref="Complete"/> (idempotent via <see cref="FreeContextHandleOnce"/>).
/// <see cref="FaultChannel"/> must NOT free: it is reachable from the element trampoline on a
/// mid-stream marshal fault, and the element callback is never last — freeing there would drop the
/// rooting handle while the pending completion can still resolve the context, reopening the GCHandle
/// cookie-recycling window (a recycled cookie could resolve and complete a DIFFERENT live instance),
/// the exact use-after-free this class is built to avoid. The faulted run still frees, via the
/// always-following completion. <see cref="Dispose"/> likewise does NOT free it (see the remark on
/// Dispose).</para>
///
/// <para><b>Producer cancel.</b> A consumer-initiated stop (<see cref="Cancel"/>, <see cref="Dispose"/>,
/// or enumerator disposal) routes through <see cref="SignalProducerStop"/>, which — beyond completing
/// the channel — task-cancels the Swift producer via the cancellation registry (<see cref="SetProducerCancellation"/>,
/// wired by the generated getter). This wakes a SUSPENDED producer (one awaiting a slow/never-yielding
/// upstream) so it runs its completion path and frees the context handle, rather than lingering until a
/// next element boundary that may never arrive. The cancel fires at most once per stream (Interlocked
/// one-shot) and is gated on the handle still being live (<c>_handleFreed == 0</c>), so a cleanly
/// completed stream never calls into the registry.</para>
///
/// <para><b>No finalizer — by design.</b> A finalizer cannot back-stop the handle: the strong
/// <see cref="GCHandle"/> roots <c>this</c>, so while the handle is allocated the instance is never
/// eligible for finalization (it self-roots). A finalizer would be dead code. This mirrors the
/// project's existing decisions where the native side holds a context cookie the managed side cannot
/// safely reclaim from a finalizer — KVO (KeyValueObserving) omits a finalizer outright, and the
/// owns-context SwiftClosure path leaks rather than risk a use-after-free. With producer cancel in
/// place, the remaining residual is narrow: a producer that never completes AND is never stopped (an
/// infinite stream whose only reference is dropped without enumerating OR disposing) — no stop path
/// runs, so nothing reclaims it. Closing THAT fully would need a finalizer the self-rooting handle
/// makes impossible. Choosing a bounded leak over a recycled-cookie use-after-free is the project's
/// standing policy for this class of native/managed lifetime mismatch.</para>
/// </summary>
/// <typeparam name="TElement">The element type in the stream.</typeparam>
public class SwiftAsyncStream<TElement> : IAsyncEnumerable<TElement>, IDisposable
{
    private readonly Channel<TElement> _channel;
    private readonly CancellationTokenSource _cts;
    // volatile for cross-thread visibility consistency with _handleFreed: _disposed is written by the
    // consumer's Dispose and read by DeliverElement on the Swift executor thread. The read is only a
    // best-effort early exit (a stale-false read is benign — the element path falls through to a
    // TryWrite the disposed channel rejects, with no UAF), but leaving the one cross-thread-read flag
    // non-volatile while _handleFreed is volatile is a needless inconsistency.
    private volatile bool _disposed;
    private GCHandle _thisHandle;

    // 0 = context handle live (or never allocated), 1 = freed. Interlocked one-shot: the completion
    // callback is the sole freer (Complete), but it runs on the Swift executor thread while
    // DeliverElement reads this flag from the same/another thread and GCHandle.Free is not a
    // concurrency primitive — the one-shot guarantees exactly one free even if completion is doubled.
    private int _handleFreed;

    // Producer-cancel registry wiring. Set ONCE by the generated getter (before the stream escapes to
    // the consumer) via SetProducerCancellation: _producerCancelKey is the registry key under which
    // the Swift wrapper registered its producer Task, and _producerCancel invokes the per-module
    // SBW_CancelTask P/Invoke. SignalProducerStop fires it to task-cancel a SUSPENDED producer so it
    // runs its completion path (which frees the context handle) instead of leaking. See the producer-
    // cancel note on the class. The key is DISTINCT from the GCHandle context cookie by design.
    private long _producerCancelKey;
    private Action<long>? _producerCancel;
    // 0 = producer cancel not yet requested, 1 = requested. Interlocked one-shot so at most ONE
    // SBW_CancelTask fires per stream — bounding the registry tombstone residual to ≤1 entry even
    // under a cancel/complete race (see SignalProducerStop).
    private int _producerCancelRequested;

    /// <summary>
    /// Creates a new SwiftAsyncStream.
    /// </summary>
    public SwiftAsyncStream()
    {
        // Use unbounded channel for simplicity - Swift produces, C# consumes
        _channel = Channel.CreateUnbounded<TElement>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Gets a context value that can be passed to Swift and used to retrieve this instance.
    /// </summary>
    public long GetContext()
    {
        ThrowIfDisposed();
        if (!_thisHandle.IsAllocated)
        {
            _thisHandle = GCHandle.Alloc(this);
        }
        return GCHandle.ToIntPtr(_thisHandle).ToInt64();
    }

    /// <summary>
    /// Wires the producer-cancel registry hook. Called ONCE by the generated property getter, before
    /// this instance escapes to the consumer, so the fields are effectively immutable by the time any
    /// stop path can read them. <paramref name="cancelKey"/> is the registry key the Swift wrapper
    /// registered its producer Task under (distinct from the GCHandle context cookie);
    /// <paramref name="cancel"/> invokes the per-module <c>SBW_CancelTask</c> P/Invoke.
    /// <see cref="SignalProducerStop"/> fires it to task-cancel a suspended producer.
    /// </summary>
    public void SetProducerCancellation(long cancelKey, Action<long> cancel)
    {
        _producerCancelKey = cancelKey;
        _producerCancel = cancel;
    }

    /// <summary>
    /// Delivers one element from the Swift producer into the channel. Called by the generated
    /// <c>[UnmanagedCallersOnly]</c> element trampoline after it has resolved this instance from the
    /// context. Returns <c>true</c> to continue iteration, <c>false</c> to stop.
    /// </summary>
    /// <remarks>
    /// This method MUST NOT throw across the native boundary: the structural guards (disposed /
    /// completed / cancelled) return <c>false</c> instead of throwing. A marshalling failure IS
    /// allowed to propagate — the trampoline's <c>StreamFault</c> envelope catches it and routes it
    /// to <see cref="FaultChannel"/> so the consumer observes the error rather than silently losing
    /// the element (the previous swallow-and-stop behaviour truncated the stream invisibly).
    /// </remarks>
    public unsafe bool DeliverElement(IntPtr elementPtr)
    {
        // Deliberately does NOT read _cts.Token here. This runs on the Swift executor thread, and a
        // racing consumer Dispose() disposes _cts (Dispose → SignalProducerStop → _cts.Dispose); a
        // post-dispose _cts.Token getter throws ObjectDisposedException, which would escape to the
        // trampoline's StreamFault catch and call FaultChannel — turning a clean consumer-side dispose
        // into a spurious error-completion the consumer observes via await foreach. (FaultChannel no
        // longer frees the handle, so this is no longer a mid-stream free / recycle hazard, but a
        // bogus internal exception surfacing to the consumer is reason enough to not read it.) The check
        // is also redundant: every stop path (Cancel/Dispose/enumerator-disposal) routes through
        // SignalProducerStop, which completes the channel, so a cancelled stream makes the TryWrite
        // below return false and stops the producer on its own. The two reads kept here are cheap and
        // never throw — _disposed is a best-effort early exit, _handleFreed guards against extracting
        // onto a torn-down instance.
        if (_disposed || Volatile.Read(ref _handleFreed) != 0)
        {
            return false; // Stop iteration
        }

        // Swift passes a BORROWED element pointer: withUnsafePointer(to: element) is valid only for
        // the duration of this callback, and the Swift producer still owns (and will release) its own
        // reference. The element escapes via the channel, so we must copy out an INDEPENDENT reference
        // now — a bare MarshalFromSwift would either alias the soon-to-die slot (class/non-frozen-struct
        // shapes → use-after-free once the closure returns) or bitwise-move a borrowed +0 as if it were
        // a +1 (SwiftString and other move-on-construction shapes → double-release). ExtractCopiedValue
        // dereferences + retains a class payload and takes a value-witness +1 for reference-backed
        // payloads, leaving Swift's borrow intact.
        //
        // A throw here is intentional: it escapes to the trampoline's StreamFault catch, which faults
        // the channel via FaultChannel(ex). Do NOT wrap this in a swallow-and-return-false catch.
        nuint payloadSize =
            TypeMetadata.TryGetTypeMetadata<TElement>(out var elementMd) && elementMd.Value.IsValid
                ? elementMd.Value.Size
                : (nuint)Unsafe.SizeOf<TElement>();
        var element = SwiftMarshal.ExtractCopiedValue<TElement>((void*)elementPtr, payloadSize);

        // Write to channel (this should not block since it's unbounded)
        return _channel.Writer.TryWrite(element); // false ⇒ channel closed ⇒ stop iteration
    }

    /// <summary>
    /// Signals normal completion of the stream. Called by the generated completion trampoline as the
    /// LAST Swift→C# callback for this context — after it runs, Swift makes no further callbacks, so
    /// this is the single safe point to free the context handle.
    /// </summary>
    public void Complete()
    {
        _channel.Writer.TryComplete();
        FreeContextHandleOnce();
    }

    /// <summary>
    /// Faults the channel with <paramref name="error"/> so a consumer iterating via
    /// <c>await foreach</c> observes the exception instead of the stream silently truncating. Invoked
    /// by the element/completion trampolines' <c>StreamFault</c> catch when a managed exception would
    /// otherwise unwind across the native boundary.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT free the context handle. This is reachable from the ELEMENT trampoline on
    /// a mid-stream marshal fault, and the element callback is never the last Swift→C# callback — the
    /// faulted element returns "stop", the Swift wrapper breaks its loop and then always invokes the
    /// completion callback, which frees the handle via <see cref="Complete"/>. Freeing here would drop
    /// the rooting handle while that pending completion can still resolve the context, reopening the
    /// GCHandle cookie-recycling window. The faulted run frees on the completion path exactly as a
    /// clean finish does (<see cref="Complete"/>'s <c>TryComplete()</c> is a no-op once the channel is
    /// already faulted, then it frees).
    /// </remarks>
    public void FaultChannel(Exception error)
    {
        // TryComplete(error) is a no-op if the channel is already completed (normal completion won
        // the race), so a late fault cannot clobber a clean finish. The context handle is intentionally
        // NOT freed here — completion (always invoked last) owns the free; see the remarks above and
        // the context-handle lifetime note on the class.
        _channel.Writer.TryComplete(error);
    }

    /// <summary>
    /// Returns an async enumerator for consuming elements.
    /// </summary>
    public async IAsyncEnumerator<TElement> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // Link the provided token with our internal token
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

        try
        {
            await foreach (var element in _channel.Reader.ReadAllAsync(linkedCts.Token))
            {
                yield return element;
            }
        }
        finally
        {
            // Consumer broke out of the loop, threw, or had its token cancelled, disposing this
            // enumerator. Signal the Swift producer to stop: an active producer winds down at its next
            // element boundary, and SignalProducerStop additionally task-cancels a SUSPENDED producer
            // via the cancellation registry. Either way the producer runs its completion callback,
            // which frees the context handle.
            SignalProducerStop();
        }
    }

    /// <summary>
    /// Cancels the stream iteration. Active Swift producers stop at their next element boundary
    /// (the element trampoline returns <c>false</c>); the producer's completion callback then frees
    /// the context handle.
    /// </summary>
    public void Cancel()
    {
        ThrowIfDisposed();
        SignalProducerStop();
    }

    /// <summary>
    /// Disposes resources used by the stream.
    /// </summary>
    /// <remarks>
    /// Dispose signals the producer to stop and completes the reader side, but deliberately does NOT
    /// free the context handle: the Swift producer may still deliver an in-flight element, and freeing
    /// the handle while a callback can still resolve it engages the GCHandle cookie-recycling hazard
    /// (a recycled cookie could resolve a different live instance). The handle is freed when the
    /// producer runs its completion callback (<see cref="Complete"/>): an active producer follows
    /// promptly from the stop signal, and a SUSPENDED producer is task-cancelled by
    /// <see cref="SignalProducerStop"/> via the cancellation registry so it too reaches completion —
    /// not freed by an unsafe early free here.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        SignalProducerStop();
        _cts.Dispose();
    }

    /// <summary>
    /// Cancels the internal token (idempotent, disposal-safe) and completes the channel reader so a
    /// blocked consumer unblocks. Used by <see cref="Cancel"/>, <see cref="Dispose"/>, and enumerator
    /// disposal.
    /// </summary>
    private void SignalProducerStop()
    {
        try
        {
            if (!_cts.IsCancellationRequested)
                _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // _cts already disposed — the stop was already signalled.
        }
        _channel.Writer.TryComplete();

        // Task-cancel a SUSPENDED Swift producer so it runs its completion path (freeing the context
        // handle) instead of lingering until its next element boundary — which may never arrive for a
        // producer awaiting a slow/never-completing upstream. Completing the channel above does not
        // wake such a producer; only task cancellation does. Gated two ways:
        //  - _handleFreed == 0: normal completion (Complete) sets _handleFreed on the Swift executor
        //    thread BEFORE the producer Task's `defer { _sbwUnregisterTask }` runs, so a stream that
        //    finished cleanly never reaches SBW_CancelTask here, and whenever this read sees 0 the
        //    registry entry is still live — no per-iteration tombstone growth on the happy path.
        //  - _producerCancelRequested one-shot: at most one SBW_CancelTask per stream. Under a genuine
        //    cancel/complete race (read 0, then the producer completes + unregisters, then this fires)
        //    the residual is at most ONE never-reclaimed registry tombstone per stream, not unbounded.
        if (_producerCancel is { } cancel
            && Volatile.Read(ref _handleFreed) == 0
            && Interlocked.Exchange(ref _producerCancelRequested, 1) == 0)
        {
            cancel(_producerCancelKey);
        }
    }

    /// <summary>
    /// Frees the context handle exactly once. Called only from <see cref="Complete"/> — the
    /// completion callback owns the free (see the context-handle lifetime note on the class). Kept as
    /// an Interlocked one-shot so a doubled completion callback cannot double-free, since
    /// <see cref="GCHandle.Free"/> is not a concurrency primitive.
    /// </summary>
    private void FreeContextHandleOnce()
    {
        if (Interlocked.Exchange(ref _handleFreed, 1) == 0)
        {
            if (_thisHandle.IsAllocated)
            {
                _thisHandle.Free();
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Test-only probe: whether the context <see cref="GCHandle"/> is still allocated. Lets lifetime
    /// tests assert that completion (<see cref="Complete"/>) frees the handle and that a channel fault
    /// (<see cref="FaultChannel"/>) and Dispose both leave the free to the completion path.
    /// </summary>
    internal bool IsContextHandleAllocated => _thisHandle.IsAllocated;

    /// <summary>
    /// Retrieves a SwiftAsyncStream instance from a context value.
    /// </summary>
    public static SwiftAsyncStream<TElement>? FromContext(long context)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(new IntPtr(context));
            // Guard against a freed handle whose cookie has not been recycled: IsAllocated is false,
            // so resolve to null rather than dereferencing a dead slot. Mirrors SwiftClosureContext.
            if (!handle.IsAllocated)
                return null;
            return handle.Target as SwiftAsyncStream<TElement>;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SwiftAsyncStream.FromContext: Invalid context {context} - {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Static helper methods for SwiftAsyncStream interop.
/// </summary>
public static class SwiftAsyncStreamInterop
{
    /// <summary>
    /// Callback signature for receiving elements from Swift.
    /// Used as an [UnmanagedCallersOnly] target.
    /// </summary>
    /// <param name="elementPtr">Pointer to the element.</param>
    /// <param name="context">Context identifying the stream instance.</param>
    /// <returns>1 (byte true) to continue, 0 to stop.</returns>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static unsafe byte OnElementCallback(void* elementPtr, long context)
    {
        // This is a generic callback that would need to be specialized per element type.
        // In practice, the binding generator will create type-specific callbacks.
        return 0;
    }

    /// <summary>
    /// Callback signature for stream completion from Swift.
    /// Used as an [UnmanagedCallersOnly] target.
    /// </summary>
    /// <param name="context">Context identifying the stream instance.</param>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    public static void OnCompleteCallback(long context)
    {
        // This is called when the Swift stream completes.
        // The actual implementation would be in generated code.
    }
}
