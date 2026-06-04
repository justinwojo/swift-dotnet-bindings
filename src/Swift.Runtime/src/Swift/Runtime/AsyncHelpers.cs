// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Swift.Runtime
{
    /// <summary>
    /// Wraps a retained Swift class pointer for async operations.
    /// Used to track self pointers that were explicitly retained via Arc.Retain()
    /// before calling async Swift methods. Must be released via Arc.Release() after callback.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct RetainedSelfPtr
    {
        public readonly IntPtr Ptr;
        public RetainedSelfPtr(IntPtr ptr) => Ptr = ptr;
    }

    /// <summary>
    /// Wraps a SafeHandle that needs DangerousRelease() called after async completion.
    /// Used for async instance methods on structs where the SafeHandle must stay alive
    /// until the Swift async operation completes.
    ///
    /// The constructor calls <see cref="SafeHandle.DangerousAddRef(ref bool)"/> to take
    /// a refcount that the async holder cleanup loop balances with a corresponding
    /// <see cref="SafeHandle.DangerousRelease"/>. Without the AddRef the cleanup
    /// underflows the SafeHandle's refcount — most visibly on cancellation paths that
    /// run cleanup before any Swift continuation lands. <see cref="SafeHandle.DangerousAddRef(ref bool)"/>
    /// throws <see cref="ObjectDisposedException"/> for closed handles, which propagates
    /// to the calling async wrapper and surfaces as a faulted Task to the consumer
    /// (correct: a disposed receiver cannot back the in-flight call).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct DeferredSafeHandleRelease
    {
        public readonly SafeHandle Handle;

        public DeferredSafeHandleRelease(SafeHandle handle)
        {
            bool addedRef = false;
            handle.DangerousAddRef(ref addedRef);
            // DangerousAddRef throws ObjectDisposedException on a closed handle, so
            // success is implied if we reach this point — but assert defensively in
            // case a future SafeHandle subclass returns false without throwing.
            if (!addedRef)
                throw new InvalidOperationException(
                    "DeferredSafeHandleRelease: DangerousAddRef did not take a reference. " +
                    "The handle may already be closed.");
            Handle = handle;
        }
    }

    /// <summary>
    /// Wraps a copy buffer pointer with its TypeMetadata for proper cleanup.
    /// Used for non-frozen struct parameters in async operations.
    /// Destroy must be called before freeing the buffer to release Swift references.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct CopyBufferWithType
    {
        public readonly IntPtr Buffer;
        public readonly TypeMetadata Metadata;
        public CopyBufferWithType(IntPtr buffer, TypeMetadata metadata)
        {
            Buffer = buffer;
            Metadata = metadata;
        }
    }

    /// <summary>
    /// Holds <see cref="IDisposable"/> instances whose lifetime must extend past the
    /// completion of an async Swift call. The foreground async wrapper allocates one
    /// of these, stores it in the GCHandle holder array, and appends serialization
    /// containers (e.g. <c>SwiftArray&lt;T&gt;</c>) to <see cref="Items"/> in place of
    /// a <c>using var</c>. The async-callback cleanup loop disposes each item after
    /// the Swift continuation has finished reading the underlying buffer.
    ///
    /// Without this, <c>using var paramSwift = SwiftArray&lt;T&gt;.FromEnumerable(...)</c>
    /// would dispose the Swift array as soon as the foreground wrapper returns
    /// <c>tcs.Task</c>, freeing the buffer that Swift dereferences on the
    /// continuation thread.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class AsyncDeferredDisposeList
    {
        public List<IDisposable> Items { get; } = new();
    }

    /// <summary>
    /// Wraps a <see cref="NativeMemory.Alloc(nuint)"/> buffer that holds a Swift
    /// existential container (e.g. <see cref="ExistentialContainer1"/>) handed
    /// to an async Swift entry point. The async-callback cleanup loop frees
    /// the buffer with <see cref="NativeMemory.Free(void*)"/> after the Swift
    /// continuation has finished reading it. Freeing in the foreground
    /// wrapper's <c>finally</c> would dangle the pointer Swift still holds.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct ExistentialContainerHeap
    {
        public readonly IntPtr Ptr;

        /// <summary>
        /// True when the buffer holds a freshly boxed existential whose +1 the caller owns and
        /// must release (the <see cref="ExistentialContainerFactory.GetOrCreate{TProtocol}(TProtocol, out bool)"/>
        /// boxable branch — audit P1-03). False for a borrowed proxy container, whose +1 the proxy
        /// owns; destroying it would over-release. The foreground <c>finally</c> can't run this
        /// destroy because the Swift continuation reads the buffer after the wrapper returns, so the
        /// owns decision is carried here to the async-callback cleanup loop.
        /// </summary>
        public readonly bool OwnsContainer;

        /// <summary>Number of witness tables in the existential (1 for EC1) — selects the existential metadata used for the destroy.</summary>
        public readonly int WitnessTableCount;

        public ExistentialContainerHeap(IntPtr ptr) : this(ptr, false, 0) { }

        public ExistentialContainerHeap(IntPtr ptr, bool ownsContainer, int witnessTableCount)
        {
            Ptr = ptr;
            OwnsContainer = ownsContainer;
            WitnessTableCount = witnessTableCount;
        }
    }

    /// <summary>
    /// Supplies process-wide monotonic keys that identify in-flight async Swift tasks in
    /// the per-module Swift cancellation registry (<c>_sbwActiveTasks</c>).
    ///
    /// The cancellation key MUST be distinct from the GCHandle-derived callback context.
    /// A <see cref="GCHandle"/> cookie value is recycled after <see cref="GCHandle.Free"/>,
    /// so a later <see cref="GCHandle.Alloc(object)"/> can hand back the same numeric value.
    /// Using that recyclable value as the registry key lets a just-completed task's
    /// <c>defer { _sbwUnregisterTask }</c> evict a newer task that happened to reuse the
    /// cookie, and lets a racing cancellation cancel unrelated in-flight work. A strictly
    /// increasing counter never reuses a key that is still live (64-bit wraparound is not
    /// reachable in practice), so the registry key is collision-free regardless of how the
    /// GCHandle context is allocated or freed.
    ///
    /// The counter is process-wide rather than per-module. Per-module uniqueness is all the
    /// Swift registry needs, and a single global counter is trivially unique within every
    /// module's dictionary while requiring no per-module state on the C# side.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class SwiftAsyncCancellation
    {
        private static long s_nextCancelKey;

        /// <summary>
        /// Returns the next process-wide unique cancellation key. Keys start at 1 (0 is
        /// never returned, leaving it free as a sentinel). Safe to call from any thread.
        /// </summary>
        /// <remarks>
        /// 64-bit wraparound is not reachable in practice, but should the counter ever wrap
        /// past <see cref="long.MaxValue"/> it passes through 0 — the documented sentinel
        /// value. The increment is atomic, so only the single caller whose increment lands on
        /// 0 retries (advancing to 1); every other caller still receives a distinct value, so
        /// the 0-is-never-returned guarantee holds without sacrificing thread-safety.
        /// </remarks>
        public static long NextCancelKey()
        {
            long key;
            do
            {
                key = System.Threading.Interlocked.Increment(ref s_nextCancelKey);
            }
            while (key == 0);
            return key;
        }
    }

    /// <summary>
    /// Wraps a CancellationTokenRegistration for disposal in async callbacks.
    /// Stored in the async holder array so the callback can dispose the registration
    /// after completion, cancellation, or error.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct CancellationRegistrationHolder
    {
        public readonly System.Threading.CancellationTokenRegistration Registration;
        public readonly System.Threading.CancellationToken Token;
        public CancellationRegistrationHolder(System.Threading.CancellationTokenRegistration registration, System.Threading.CancellationToken token)
        {
            Registration = registration;
            Token = token;
        }
    }

    /// <summary>
    /// Exception-safe, idempotent cleanup of the async-call holder array shared by every
    /// async <c>[UnmanagedCallersOnly]</c> callback (success / fault / error / cancellation)
    /// and the foreground launch paths (pre-cancel / launch-catch).
    ///
    /// The holder's slots own native resources that must be released exactly once after the
    /// Swift continuation finishes reading them: a retained self pointer, a deferred SafeHandle
    /// release, non-frozen-parameter copy buffers, an existential-container heap buffer,
    /// deferred <see cref="IDisposable"/> containers, and the cancellation registration. Slot 0
    /// is always the <c>TaskCompletionSource</c> and is never freed here.
    ///
    /// Centralizing the slot walk in one runtime helper — instead of inlining the loop at every
    /// emission site — gives it two properties the inlined loop lacked:
    ///
    /// <list type="number">
    /// <item><b>Exception-safe.</b> The success path runs cleanup inside the callback <c>try</c>;
    /// the fault path runs it again inside the guarding <c>catch</c>. A throw escaping a
    /// <c>[UnmanagedCallersOnly]</c> callback unwinds into the native Swift caller and aborts the
    /// process (SIGABRT) — the exact failure the async UCO hardening exists to prevent. Each
    /// slot's release is wrapped so one faulting release (for example a user
    /// <see cref="IDisposable.Dispose"/> in a deferred list) can neither abort the process nor
    /// skip the remaining slots.</item>
    /// <item><b>Idempotent.</b> Each processed slot is cleared to <c>null</c>, so a second pass —
    /// the fault <c>catch</c> re-running after the success path freed some slots and then threw —
    /// cannot double <see cref="Arc.Release"/>, <c>DangerousRelease</c>,
    /// <c>NativeMemory.Free</c>, or dispose.</item>
    /// </list>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class SwiftAsyncCallHolder
    {
        /// <summary>
        /// Releases every owned slot in <paramref name="holder"/> from
        /// <paramref name="startIndex"/> onward. Safe to call more than once on the same array
        /// (processed slots are nulled) and never throws (per-slot releases are best-effort).
        /// </summary>
        /// <param name="holder">The GCHandle-rooted holder array; slot 0 is the TaskCompletionSource.</param>
        /// <param name="startIndex">First slot to clean. 1 by default — slot 0 (the TCS) is never freed here.</param>
        public static unsafe void Cleanup(object[] holder, int startIndex = 1)
        {
            for (int i = startIndex; i < holder.Length; i++)
            {
                try
                {
                    if (holder[i] is RetainedSelfPtr retained && retained.Ptr != IntPtr.Zero)
                        Arc.Release(retained.Ptr);
                    else if (holder[i] is DeferredSafeHandleRelease deferred)
                        deferred.Handle.DangerousRelease();
                    else if (holder[i] is CopyBufferWithType copyBuffer && copyBuffer.Buffer != IntPtr.Zero)
                    {
                        copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                        NativeMemory.Free((void*)copyBuffer.Buffer);
                    }
                    else if (holder[i] is ExistentialContainerHeap existentialHeap && existentialHeap.Ptr != IntPtr.Zero)
                    {
                        // P1-03: a boxable value conformer was freshly boxed at +1 into this
                        // buffer; balance it with the existential value-witness destroy now that
                        // the continuation has finished reading the @in_guaranteed buffer. The
                        // centralized helper applies the owns-gate (borrowed proxy containers,
                        // OwnsContainer == false, are freed but never destroyed), the
                        // metadata-unavailable try/catch, and the buffer free.
                        ExistentialContainerFactory.DestroyAndFreeExistential(
                            (void*)existentialHeap.Ptr,
                            existentialHeap.WitnessTableCount,
                            existentialHeap.OwnsContainer);
                    }
                    else if (holder[i] is AsyncDeferredDisposeList deferredList)
                    {
                        foreach (var item in deferredList.Items)
                        {
                            try { item.Dispose(); }
                            catch
                            {
                                // Best-effort: a faulting user Dispose must not abort the
                                // [UnmanagedCallersOnly] callback or skip its sibling frees.
                            }
                        }
                    }
                    else if (holder[i] is CancellationRegistrationHolder cancelReg)
                        cancelReg.Registration.Dispose();
                }
                catch
                {
                    // Best-effort: cleanup runs on the callback thread that re-enters from native
                    // Swift. A throw escaping here unwinds into native and aborts the process
                    // (SIGABRT). Swallow per-slot so one faulting release can neither abort the
                    // process nor skip the remaining slots.
                }
                finally
                {
                    // Idempotent: clear the slot so a second cleanup pass (the fault catch
                    // re-running after a partially-completed success path) cannot double-free.
                    holder[i] = null!;
                }
            }
        }

        /// <summary>
        /// Returns the <see cref="System.Threading.CancellationToken"/> captured at registration
        /// time from the holder's <see cref="CancellationRegistrationHolder"/> slot (or
        /// <c>default</c> if none). Read-only — does NOT free or clear any slot — so it is safe
        /// to call before <see cref="Cleanup"/> on the Swift-reported cancellation path, where the
        /// token is needed for <c>TrySetCanceled</c>.
        /// </summary>
        public static System.Threading.CancellationToken CaptureCancellationToken(object[] holder, int startIndex = 1)
        {
            for (int i = startIndex; i < holder.Length; i++)
            {
                if (holder[i] is CancellationRegistrationHolder cancelReg)
                    return cancelReg.Token;
            }
            return default;
        }
    }
}
