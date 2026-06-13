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
/// Implements "Design B2" (see <c>src/docs/session1-reverse-dispatch-lifetime-vtable.md</c>),
/// which fixes the inverted-lifetime / silent-value-fabrication defect (Defect G).
///
/// <para>
/// Two independent roots, both keyed by the EveryProtocol <c>handle</c>:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>Strong impl root</b> (<see cref="s_implRoots"/>): a strong <see cref="GCHandle"/> on
///     the user's C# implementation object. Allocated in <see cref="Track"/> (from the proxy
///     ctor) and freed in <see cref="OnEveryProtocolDeinitCore"/> (Swift's last retain dropped).
///     This roots the impl by <i>Swift liveness</i> — the impl stays reachable for exactly as
///     long as Swift holds the EveryProtocol, so reverse dispatch can always resolve it via
///     <see cref="ResolveImpl{T}"/> and never has to fabricate a value.
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
    // handle -> strong GCHandle on the user's C# impl. Roots the impl for exactly as long as
    // Swift holds the EveryProtocol. Freed in OnEveryProtocolDeinitCore (Swift's last retain).
    private static readonly ConcurrentDictionary<IntPtr, GCHandle> s_implRoots = new();

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
    {
        if (impl is null)
            throw new ArgumentNullException(nameof(impl));
        if (handle == IntPtr.Zero)
            throw new ArgumentException("Handle cannot be zero", nameof(handle));

        var entry = new HandleEntry(handle);
        if (!s_entries.TryAdd(handle, entry))
            throw new InvalidOperationException($"Handle {handle.ToString($"X{IntPtr.Size * 2}")} is already tracked");

        var implRoot = GCHandle.Alloc(impl, GCHandleType.Normal);
        if (!s_implRoots.TryAdd(handle, implRoot))
        {
            // Roll back the entry write so a subsequent Track/NotifyDeinit sees no leftover state.
            implRoot.Free();
            s_entries.TryRemove(handle, out _);
            throw new InvalidOperationException($"Handle {handle.ToString($"X{IntPtr.Size * 2}")} is already tracked");
        }
    }

    /// <summary>
    /// Resolves the C# implementation rooted for <paramref name="handle"/>, viewed as
    /// <typeparamref name="T"/>. This is the reverse-dispatch entry point: receiver thunks call
    /// <c>ResolveImpl&lt;IFace&gt;(handle)</c> instead of locating a (possibly already-collected)
    /// proxy. Returns <c>null</c> only if the impl is no longer rooted — which, in the canonical
    /// pattern, cannot happen while Swift references the proxy (the receiver's loud backstop
    /// treats a null here as a hard invariant violation).
    /// </summary>
    public static T? ResolveImpl<T>(IntPtr handle) where T : class
    {
        if (handle != IntPtr.Zero && s_implRoots.TryGetValue(handle, out var implRoot) && implRoot.IsAllocated)
            return implRoot.Target as T;
        return null;
    }

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
            if (s_implRoots.TryRemove(handle, out var implRoot) && implRoot.IsAllocated)
                implRoot.Free();

            SwiftObjectRegistry.Unregister(handle);

            if (s_entries.TryRemove(handle, out var entry))
                Interlocked.Exchange(ref entry.Released, 1);
        }
        catch
        {
            // Cannot propagate exceptions across the Swift ABI boundary.
        }
    }

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
            if (implRoot.IsAllocated)
                implRoot.Free();
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
