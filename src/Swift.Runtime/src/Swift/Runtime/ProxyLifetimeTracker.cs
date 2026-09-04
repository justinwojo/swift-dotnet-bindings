// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

#nullable enable

namespace Swift.Runtime;

/// <summary>
/// Lifetime broker for an auto-wrapped C#-implementation protocol proxy and the
/// Swift <c>EveryProtocol</c> instance that carries it across the ABI boundary.
/// Fixes the inverted-lifetime / silent-value-fabrication defect where the proxy's
/// managed lifetime and the Swift existential's lifetime were not co-rooted.
///
/// <para>
/// Two independent roots, both keyed by the EveryProtocol <c>handle</c>:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>Impl root</b> (<see cref="s_implRoots"/>): a <see cref="GCHandle"/> on the user's C#
///     implementation object. Allocated from the proxy ctor and freed in
///     <see cref="OnEveryProtocolDeinitCore"/> (Swift's last retain dropped), so reverse dispatch
///     can resolve the impl via <see cref="ResolveImpl{T}"/> and never has to fabricate a value.
///     <see cref="Track"/> allocates it <i>strong</i>, rooting the impl by Swift liveness — the
///     right shape when the Swift sink retains the conformer box. <see cref="TrackConsumerOwned"/>
///     allocates it as a long weak handle instead, for a <c>weak</c>/<c>unowned</c> sink where the
///     consumer's impl owns the carrier and a strong root here would be a permanent pin.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>R0 ownership</b> (<see cref="s_entries"/>): the construction <c>+1</c> retain the
///     <c>SBW_Create…</c> factory put on the EveryProtocol (via <c>Unmanaged.passRetained</c>).
///     This is owned by the <i>proxy</i> and released on the proxy's finalizer/Dispose through
///     <see cref="ReleaseHandle"/> → <see cref="SwiftReleaseTrampoline.Release"/> (the
///     Mono-finalizer-safe Cdecl path). The per-handle <see cref="HandleEntry.Released"/> atomic
///     flag guarantees exactly-one native release even if Dispose and the finalizer race.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// Why two roots and not one: the teardown chain is
/// <c>R0 released → Swift's last store-retain released → EveryProtocol deinit →
/// OnEveryProtocolDeinit → free impl GCHandle + Unregister</c>. Gating R0 release on
/// EveryProtocol liveness deadlocks (deinit needs R0 released; R0 release needs deinit), so R0
/// is released on a signal independent of EveryProtocol liveness — <b>proxy collection</b> —
/// and the proxy is registered only <i>weakly</i> in <see cref="SwiftObjectRegistry"/> so it can
/// be collected once the consumer drops it. Reverse dispatch therefore must NOT depend on a live
/// proxy; it resolves the impl from the strong root instead. The full cycle/impossibility
/// analysis is in the design doc.
/// </para>
///
/// <para>
/// Process-exit safety: <see cref="ReleaseHandle"/> and <see cref="OnEveryProtocolDeinitCore"/>
/// both short-circuit on <see cref="SwiftExitGuard.IsProcessExiting"/>, mirroring
/// <see cref="SwiftClassHandle{T}"/>'s release path — calls into a partially torn-down Swift
/// runtime during shutdown crash iOS processes, so we deliberately leak in that case.
/// </para>
/// </summary>
public static class ProxyLifetimeTracker
{
    // handle -> the impl root, freed in OnEveryProtocolDeinitCore (Swift's last retain). The root's
    // KIND encodes who owns whom: a Normal handle for a retaining Swift sink (the impl lives for
    // exactly as long as Swift holds the EveryProtocol — see Track), a long weak handle for a
    // non-retaining sink where the consumer's impl owns the carrier instead (see TrackConsumerOwned).
    // Either kind resolves through Target and either is freed by deinit, so the bookkeeping is
    // kind-agnostic; the recorded lane is read only by IsConsumerOwnedCarrier, which is what lets a
    // reverse-dispatch receiver tell an invariant violation from a legal collected-delegate state.
    private static readonly ConcurrentDictionary<IntPtr, ImplRoot> s_implRoots = new();

    // handle -> per-handle R0 state. The atomic Released flag serializes the proxy's Dispose
    // path against its finalizer so SwiftReleaseTrampoline.Release runs exactly once per handle.
    private static readonly ConcurrentDictionary<IntPtr, HandleEntry> s_entries = new();

    /// <summary>
    /// Associates an EveryProtocol <paramref name="handle"/> with the lifetime of
    /// <paramref name="impl"/>. Allocates a strong <see cref="GCHandle"/> rooting the impl (freed
    /// when Swift's deinit fires via <see cref="OnEveryProtocolDeinitCore"/>) and a
    /// <see cref="HandleEntry"/> tracking the construction <c>+1</c> the proxy will release on its
    /// finalizer/Dispose (via <see cref="ReleaseHandle"/>).
    /// </summary>
    /// <remarks>
    /// Transactional publication: both maps are written, and if the second write fails the first
    /// is rolled back, so the handle index is all-or-nothing. A duplicate handle is rejected.
    /// </remarks>
    public static void Track(object impl, IntPtr handle)
        => TrackCore(impl, handle, GCHandleType.Normal);

    /// <summary>
    /// Consumer-owned variant of <see cref="Track"/>, for a Swift sink that does NOT retain the
    /// conformer box — a <c>weak</c>/<c>unowned</c> stored property. Same bookkeeping as
    /// <see cref="Track"/> (an <see cref="s_implRoots"/> entry plus the <see cref="s_entries"/> R0
    /// record, freed by the same deinit callback), but the impl root is a
    /// <see cref="GCHandleType.WeakTrackResurrection"/> handle rather than a strong one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A non-retaining sink inverts who owns whom. The consumer's implementation object owns the
    /// carrier (the proxy is held strongly by an impl-keyed memo and holds the impl strongly back,
    /// which the memo's ephemeron semantics make collectable as one unit), so a <i>strong</i> root
    /// here would be a process-lifetime pin: the strong root keeps the impl alive, the impl keeps
    /// the carrier alive, the carrier's R0 keeps the Swift box alive, and only the box's deinit
    /// frees the root.
    /// </para>
    /// <para>
    /// The handle must nonetheless be a <b>long</b> weak handle. When the consumer drops the impl,
    /// the impl and its proxy die together, and the proxy's finalizer is what releases R0. A Swift
    /// callback arriving in that window — after the pair became unreachable, before the finalizer
    /// ran — still has to resolve the impl, and the impl is finalization-reachable through the
    /// queued proxy for exactly that window. A short (<see cref="GCHandleType.Weak"/>) handle is
    /// already cleared by then and the receiver would hit its null-impl backstop.
    /// </para>
    /// </remarks>
    public static void TrackConsumerOwned(object impl, IntPtr handle)
        => TrackCore(impl, handle, GCHandleType.WeakTrackResurrection);

    private static void TrackCore(object impl, IntPtr handle, GCHandleType rootKind)
    {
        if (impl is null)
            throw new ArgumentNullException(nameof(impl));
        if (handle == IntPtr.Zero)
            throw new ArgumentException("Handle cannot be zero", nameof(handle));

        var entry = new HandleEntry(handle);
        if (!s_entries.TryAdd(handle, entry))
            throw new InvalidOperationException($"Handle {handle.ToString($"X{IntPtr.Size * 2}")} is already tracked");

        var implRoot = new ImplRoot(GCHandle.Alloc(impl, rootKind),
            consumerOwned: rootKind == GCHandleType.WeakTrackResurrection);
        if (!s_implRoots.TryAdd(handle, implRoot))
        {
            // Roll back the entry write so a subsequent Track/NotifyDeinit sees no leftover state.
            implRoot.Root.Free();
            s_entries.TryRemove(handle, out _);
            throw new InvalidOperationException($"Handle {handle.ToString($"X{IntPtr.Size * 2}")} is already tracked");
        }
    }

    /// <summary>
    /// Resolves the C# implementation rooted for <paramref name="handle"/>, viewed as
    /// <typeparamref name="T"/>. This is the reverse-dispatch entry point: receiver thunks call
    /// <c>ResolveImpl&lt;IFace&gt;(handle)</c> instead of locating a (possibly already-collected)
    /// proxy.
    /// <para>A <c>null</c> result means different things on the two lanes, which is why the receiver's
    /// terminal consults <see cref="IsConsumerOwnedCarrier"/> before deciding what to do about it. On a
    /// <see cref="Track"/>ed (Swift-rooted) carrier the root is strong for exactly as long as Swift holds
    /// the box, so a null resolve cannot happen and is treated as a hard invariant violation. On a
    /// <see cref="TrackConsumerOwned"/> carrier the root is weak by design and a null resolve is the
    /// ordinary "the consumer dropped their delegate while Swift still holds the conformer" state.</para>
    /// </summary>
    public static T? ResolveImpl<T>(IntPtr handle) where T : class
    {
        if (handle != IntPtr.Zero && s_implRoots.TryGetValue(handle, out var implRoot) && implRoot.Root.IsAllocated)
            return implRoot.Root.Target as T;
        return null;
    }

    /// <summary>
    /// True when <paramref name="handle"/>'s carrier was published through
    /// <see cref="TrackConsumerOwned"/> — i.e. the implementation was assigned into a non-retaining
    /// Swift slot and nothing on the Swift side roots it. Reverse-dispatch receivers ask this when
    /// <see cref="ResolveImpl{T}"/> comes back null: on this lane a collected implementation is a legal
    /// state the callback degrades through (see <see cref="ProxyDegradation"/>), while on the
    /// Swift-rooted lane it is the invariant violation the loud backstop exists for.
    /// </summary>
    /// <remarks>
    /// Returns <c>false</c> for an untracked handle. That is deliberate: a handle with no root at all is
    /// not a live consumer-owned carrier whose delegate merely went away — it is a box being dispatched
    /// after its own teardown, which stays on the loud path.
    /// </remarks>
    public static bool IsConsumerOwnedCarrier(IntPtr handle)
        => handle != IntPtr.Zero && s_implRoots.TryGetValue(handle, out var implRoot) && implRoot.ConsumerOwned;

    /// <summary>
    /// Releases the construction <c>+1</c> (R0) on <paramref name="handle"/>'s EveryProtocol.
    /// Called from the C#-impl proxy's finalizer and <c>Dispose</c>. Routes through
    /// <see cref="SwiftReleaseTrampoline.Release"/> — the finalizer-safe Cdecl path — NOT
    /// <c>Arc.Release</c>, which crashes Mono with <c>!ji-&gt;async</c> after CallConvSwift JIT
    /// contamination on the finalizer thread. The per-handle <see cref="HandleEntry.Released"/>
    /// flag makes this exactly-once even if Dispose and the finalizer both call it.
    /// </summary>
    public static void ReleaseHandle(IntPtr handle)
    {
        // Mirror SwiftClassHandle's process-exit guard: skip native release during shutdown
        // because the Swift runtime may be partially torn down.
        if (SwiftExitGuard.IsProcessExiting)
            return;
        if (handle == IntPtr.Zero)
            return;

        if (!s_entries.TryGetValue(handle, out var entry))
            return;

        // Atomically claim the release — the loser (other of Dispose/finalizer) becomes a no-op.
        if (Interlocked.Exchange(ref entry.Released, 1) == 1)
            return;

        s_entries.TryRemove(handle, out _);
        try
        {
            SwiftReleaseTrampoline.Release(handle);
        }
        catch
        {
            // Already deallocating via a race — ignore.
        }
    }

    /// <summary>
    /// Unmanaged callback invoked from the generated Swift <c>EveryProtocol.deinit</c> after
    /// Swift's last retain has dropped. Frees the impl's strong <see cref="GCHandle"/> (making the
    /// impl collectable), drops the <see cref="SwiftObjectRegistry"/> root, and scrubs the R0
    /// entry. Called on an arbitrary Swift release thread — must be idempotent and non-throwing
    /// across the ABI boundary. <c>[UnmanagedCallersOnly]</c> methods cannot be called from
    /// managed code directly; the body delegates to <see cref="OnEveryProtocolDeinitCore"/> so the
    /// managed unit tests can exercise the same logic.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static void OnEveryProtocolDeinit(IntPtr handle)
    {
        OnEveryProtocolDeinitCore(handle);
    }

    /// <summary>
    /// Shared implementation for the EveryProtocol deinit callback, callable from both the
    /// unmanaged trampoline and managed unit tests.
    /// </summary>
    internal static void OnEveryProtocolDeinitCore(IntPtr handle)
    {
        if (SwiftExitGuard.IsProcessExiting)
            return;

        try
        {
            // Swift's last retain is gone: the impl no longer needs to be kept alive, so free its
            // strong root. By the time deinit fires the proxy has already released R0 (that is what
            // drove the retain to zero), so the R0 entry is normally already gone; scrub it anyway
            // so a never-disposed entry does not linger.
            if (s_implRoots.TryRemove(handle, out var implRoot) && implRoot.Root.IsAllocated)
                implRoot.Root.Free();

            SwiftObjectRegistry.Unregister(handle);
            // The once-per-carrier degradation latch is keyed by this handle; drop it so a recycled
            // handle value does not inherit an already-reported state from the box that just died.
            ProxyDegradation.Forget(handle);

            if (s_entries.TryRemove(handle, out var entry))
                Interlocked.Exchange(ref entry.Released, 1);
        }
        catch
        {
            // Cannot propagate exceptions across the Swift ABI boundary.
        }
    }

    /// <summary>
    /// Diagnostic: the number of EveryProtocol impl roots currently held — one entry per live
    /// conformer box, regardless of whether its root is strong (retaining sink) or long-weak
    /// (consumer-owned sink). Read by <see cref="SwiftLeakCensus"/> to surface cross-heap
    /// conformer leaks.
    /// </summary>
    internal static int ImplRootCount => s_implRoots.Count;

    /// <summary>
    /// Test helper: reports whether a handle is still being tracked (either root present).
    /// </summary>
    internal static bool IsTrackedForTest(IntPtr handle)
        => s_implRoots.ContainsKey(handle) || s_entries.ContainsKey(handle);

    /// <summary>
    /// Test helper: drops every tracked root for <paramref name="handle"/> WITHOUT calling the
    /// native release path (<see cref="SwiftReleaseTrampoline.Release"/>). Unit tests register mock
    /// pointers that cannot be passed to <c>swift_release</c>, so this frees the impl GCHandle,
    /// marks the R0 entry released, and scrubs both indices. Returns whether anything was dropped.
    /// </summary>
    internal static bool DropForTest(IntPtr handle)
    {
        var dropped = false;
        if (s_implRoots.TryRemove(handle, out var implRoot))
        {
            if (implRoot.Root.IsAllocated)
                implRoot.Root.Free();
            dropped = true;
        }
        if (s_entries.TryRemove(handle, out var entry))
        {
            Interlocked.Exchange(ref entry.Released, 1);
            dropped = true;
        }
        return dropped;
    }

    /// <summary>
    /// The per-handle impl root: the <see cref="GCHandle"/> that keeps (or merely observes) the user's
    /// implementation, plus the lane it was published on. Carrying the lane alongside the handle — rather
    /// than inferring it from the handle's kind, which <see cref="GCHandle"/> does not expose — is what
    /// lets a reverse-dispatch receiver decide whether a null resolve is an invariant violation or the
    /// ordinary collected-delegate state.
    /// </summary>
    private readonly struct ImplRoot
    {
        public ImplRoot(GCHandle root, bool consumerOwned)
        {
            Root = root;
            ConsumerOwned = consumerOwned;
        }

        /// <summary>Strong for a Swift-rooted carrier, long-weak for a consumer-owned one.</summary>
        public GCHandle Root { get; }

        /// <summary>True when the carrier was published through <see cref="TrackConsumerOwned"/>.</summary>
        public bool ConsumerOwned { get; }
    }

    /// <summary>
    /// Per-handle R0 state. The atomic <see cref="Released"/> flag is claimed by whichever of the
    /// proxy's Dispose or finalizer path runs first, so <see cref="SwiftReleaseTrampoline.Release"/>
    /// fires exactly once per handle.
    /// </summary>
    private sealed class HandleEntry
    {
        public readonly IntPtr Handle;
        // 0 = live, 1 = released. Mutated via Interlocked.Exchange.
        public int Released;

        public HandleEntry(IntPtr handle)
        {
            Handle = handle;
        }
    }
}
