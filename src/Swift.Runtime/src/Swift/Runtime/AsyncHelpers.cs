// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Swift.Runtime
{
    /// <summary>
    /// Wraps a retained Swift class pointer for async operations.
    /// Used to track self pointers that were explicitly retained via the isa-dispatching
    /// Arc.UnknownObjectRetain before calling async Swift methods (self may be an
    /// @objc:NSObject-rooted class or a pure-Swift class). Must be balanced by
    /// Arc.UnknownObjectRelease in <see cref="SwiftAsyncCallHolder.Cleanup"/> after the callback —
    /// a native-only Arc.Release over-releases an @objc self (issue #40).
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
        /// boxable branch). False for a borrowed proxy container, whose +1 the proxy
        /// owns; destroying it would over-release. The foreground <c>finally</c> can't run this
        /// destroy because the Swift continuation reads the buffer after the wrapper returns, so the
        /// owns decision is carried here to the async-callback cleanup loop.
        /// </summary>
        public readonly bool OwnsContainer;

        /// <summary>Number of witness tables in the existential (1 for EC1) — selects the existential metadata used for the destroy.</summary>
        public readonly int WitnessTableCount;

        /// <summary>
        /// The proxy that owns the borrowed container's EveryProtocol construction +1 (R0), or
        /// <c>null</c> for an owned/boxable container (design change 4 — see
        /// <c>src/docs/Design/reverse-dispatch-lifetime.md</c>). The async analog of the
        /// synchronous <c>GC.KeepAlive</c>: because this struct is stored in the GCHandle-rooted async
        /// holder array, holding the proxy reference here keeps it (and therefore R0) alive across the
        /// Swift suspension, so an otherwise-unrooted auto-wrapped proxy cannot be finalized — and
        /// release R0 — while the continuation is still reading the <c>@in_guaranteed</c> buffer.
        /// </summary>
        public readonly object? KeepAlive;

        public ExistentialContainerHeap(IntPtr ptr) : this(ptr, false, 0) { }

        public ExistentialContainerHeap(IntPtr ptr, bool ownsContainer, int witnessTableCount)
            : this(ptr, ownsContainer, witnessTableCount, null) { }

        public ExistentialContainerHeap(IntPtr ptr, bool ownsContainer, int witnessTableCount, object? keepAlive)
        {
            Ptr = ptr;
            OwnsContainer = ownsContainer;
            WitnessTableCount = witnessTableCount;
            KeepAlive = keepAlive;
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
    /// Typed, GCHandle-rooted holder for the resources an async <c>[UnmanagedCallersOnly]</c>
    /// callback (success / fault / error / cancellation) and the foreground launch paths
    /// (pre-cancel / launch-catch) must release exactly once after the Swift continuation finishes
    /// reading them. Replaces the historical untyped <c>object[]</c> with named fields: each
    /// resource kind has its own field, so an emitter that stashes an unrecognized resource is a
    /// compile error (no field for it) rather than the silent leak the positional <c>object[]</c>
    /// + type-test walk allowed (Finding 16). <see cref="Tcs"/> is the completion source and is
    /// never released by <see cref="Cleanup"/>.
    ///
    /// <see cref="Cleanup"/> walks the fields and gives the cleanup two properties the inlined
    /// per-site loop lacked:
    ///
    /// <list type="number">
    /// <item><b>Exception-safe.</b> The success path runs cleanup inside the callback <c>try</c>;
    /// the fault path runs it again inside the guarding <c>catch</c>. A throw escaping a
    /// <c>[UnmanagedCallersOnly]</c> callback unwinds into the native Swift caller and aborts the
    /// process (SIGABRT) — the exact failure the async UCO hardening exists to prevent. Each
    /// field's release is wrapped so one faulting release (for example a user
    /// <see cref="IDisposable.Dispose"/> in a deferred list) can neither abort the process nor
    /// skip the remaining releases.</item>
    /// <item><b>Idempotent.</b> Each processed field is cleared (nulled / list emptied), so a
    /// second pass — the fault <c>catch</c> re-running after the success path freed some fields
    /// and then threw — cannot double <see cref="Arc.UnknownObjectRelease"/>,
    /// <c>DangerousRelease</c>, <c>NativeMemory.Free</c>, or dispose.</item>
    /// </list>
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class SwiftAsyncCallHolder
    {
        /// <summary>The completion source the callback resolves; never released by <see cref="Cleanup"/>.</summary>
        public required object Tcs { get; init; }

        /// <summary>
        /// A class self pointer retained via the isa-dispatching <see cref="Arc.UnknownObjectRetain"/>;
        /// released by the matching <see cref="Arc.UnknownObjectRelease"/>. Mutually exclusive with
        /// <see cref="DeferredSelfHandle"/> (a call is class-self, struct-self, or static).
        /// </summary>
        public RetainedSelfPtr? SelfRetain { get; set; }

        /// <summary>A struct receiver's SafeHandle whose DangerousAddRef is balanced by DangerousRelease here.</summary>
        public DeferredSafeHandleRelease? DeferredSelfHandle { get; set; }

        /// <summary>Non-frozen / frozen-blittable parameter copy buffers, destroyed then freed.</summary>
        public List<CopyBufferWithType> CopyBuffers { get; } = new();

        /// <summary>Existential-container heap buffers, destroyed (when owned) then freed.</summary>
        public List<ExistentialContainerHeap> ExistentialHeaps { get; } = new();

        /// <summary>Deferred <see cref="IDisposable"/> containers (serialization buffers) disposed after the call.</summary>
        public AsyncDeferredDisposeList? DeferredDisposes { get; set; }

        /// <summary>The cancellation-token registration disposed after the call.</summary>
        public CancellationRegistrationHolder? CancellationRegistration { get; set; }

        /// <summary>
        /// Managed references kept alive purely as GC roots for the duration of the call (the
        /// receiver <c>this</c>, original non-frozen parameter objects whose buffers were copied,
        /// and ISwiftObject-typed held arguments). No release action — cleared after the call so
        /// they become collectible.
        /// </summary>
        public List<object> KeepAlives { get; } = new();

        /// <summary>
        /// Releases every owned field. Safe to call more than once (processed fields are cleared)
        /// and never throws (each release is best-effort). <see cref="Tcs"/> is never released.
        /// </summary>
        public unsafe void Cleanup()
        {
            if (SelfRetain is { } retained)
            {
                try
                {
                    // Self was retained via the isa-dispatching Arc.UnknownObjectRetain at the
                    // emission site (self may be an @objc:NSObject-rooted class or a pure-Swift
                    // class), so the balancing release MUST also isa-dispatch. Native-only
                    // Arc.Release (swift_release) over-releases an @objc self — its
                    // swift_isDeallocating precheck misreads the ObjC refcount word and the
                    // decrement drives the object to premature deinit (issue #40 self-retain
                    // SIGSEGV). This runs on the Swift continuation thread, never the GC
                    // finalizer, so the direct UnknownObjectRelease is the correct entry point.
                    if (retained.Ptr != IntPtr.Zero)
                        Arc.UnknownObjectRelease(retained.Ptr);
                }
                catch { }
                finally { SelfRetain = null; }
            }

            if (DeferredSelfHandle is { } deferred)
            {
                try { deferred.Handle.DangerousRelease(); }
                catch { }
                finally { DeferredSelfHandle = null; }
            }

            if (CopyBuffers.Count > 0)
            {
                foreach (var copyBuffer in CopyBuffers)
                {
                    try
                    {
                        if (copyBuffer.Buffer != IntPtr.Zero)
                        {
                            copyBuffer.Metadata.ValueWitnessTable->Destroy((void*)copyBuffer.Buffer, copyBuffer.Metadata);
                            NativeMemory.Free((void*)copyBuffer.Buffer);
                        }
                    }
                    catch { }
                }
                CopyBuffers.Clear();
            }

            if (ExistentialHeaps.Count > 0)
            {
                foreach (var existentialHeap in ExistentialHeaps)
                {
                    try
                    {
                        // A boxable value conformer was freshly boxed at +1 into this buffer;
                        // balance it with the existential value-witness destroy now that the
                        // continuation has finished reading the @in_guaranteed buffer. The
                        // centralized helper applies the owns-gate (borrowed proxy containers,
                        // OwnsContainer == false, are freed but never destroyed), the
                        // metadata-unavailable try/catch, and the buffer free.
                        if (existentialHeap.Ptr != IntPtr.Zero)
                            ExistentialContainerFactory.DestroyAndFreeExistential(
                                (void*)existentialHeap.Ptr,
                                existentialHeap.WitnessTableCount,
                                existentialHeap.OwnsContainer);
                    }
                    catch { }
                }
                ExistentialHeaps.Clear();
            }

            if (DeferredDisposes is { } deferredList)
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
                DeferredDisposes = null;
            }

            if (CancellationRegistration is { } cancelReg)
            {
                try { cancelReg.Registration.Dispose(); }
                catch { }
                finally { CancellationRegistration = null; }
            }

            // GC roots only — no release action; clear so they become collectible.
            KeepAlives.Clear();
        }

        /// <summary>
        /// Returns the <see cref="System.Threading.CancellationToken"/> captured at registration
        /// time (or <c>default</c> if none). Read-only — does NOT free or clear any field — so it
        /// is safe to call before <see cref="Cleanup"/> on the Swift-reported cancellation path,
        /// where the token is needed for <c>TrySetCanceled</c>.
        /// </summary>
        public System.Threading.CancellationToken CaptureCancellationToken()
            => CancellationRegistration?.Token ?? default;
    }
}
