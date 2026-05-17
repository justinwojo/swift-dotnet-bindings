// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

#nullable enable

namespace Swift.Runtime;

/// <summary>
/// Anchors the +1 ARC retain on an <c>EveryProtocol</c> handle to the lifetime of
/// the user's C# protocol implementation object (the "impl").
///
/// Auto-wrapped protocol proxies need to keep the Swift-side <c>EveryProtocol</c>
/// instance alive while the user's impl is reachable, but must release the +1
/// retain once the impl itself becomes collectible — otherwise the proxy and its
/// underlying Swift allocation leak until process exit.
///
/// The tracker stores a <see cref="ConditionalWeakTable{TKey, TValue}"/> keyed
/// weakly by the impl. When the impl is garbage-collected, the associated
/// <c>ProxyCleanup</c> becomes unreachable and its finalizer runs
/// <see cref="SwiftReleaseTrampoline.Release"/> on every tracked handle (the
/// finalizer-safe path that routes through the <c>SBW_SwiftRelease</c> Swift
/// wrapper). Combined with the Swift <c>EveryProtocol.deinit</c> callback routed
/// to <see cref="OnEveryProtocolDeinit"/>, this releases the
/// <see cref="SwiftObjectRegistry"/> strong root and allows the proxy to be
/// collected.
///
/// <para>
/// Deinit/finalizer race: each handle is represented by a <c>HandleEntry</c>
/// with an atomic <c>Released</c> flag. Whichever path (Swift deinit callback or
/// managed finalizer) observes the flag == 0 via <c>Interlocked.Exchange</c>
/// "wins" the release; the loser sees 1 and becomes a no-op. This guarantees
/// exactly-one native release per tracked handle and prevents the stale-pointer
/// <c>swift_isDeallocating</c> read that was possible in the earlier
/// WeakReference-keyed design.
/// </para>
///
/// <para>
/// Process-exit safety: both <c>ProxyCleanup</c>'s finalizer and
/// <see cref="OnEveryProtocolDeinit"/> short-circuit on
/// <see cref="SwiftExitGuard.IsProcessExiting"/>, mirroring
/// <see cref="SwiftClassHandle{T}"/>'s release path. Calls into the partially
/// torn-down Swift runtime during shutdown are the kind of thing that crashes
/// iOS processes on exit, so we deliberately leak in that case.
/// </para>
/// </summary>
public static class ProxyLifetimeTracker
{
    // Primary: weakly keyed by impl. Value becomes eligible for finalization when
    // impl is GC'd, at which point ProxyCleanup.~ProxyCleanup releases the +1(s).
    private static readonly ConditionalWeakTable<object, ProxyCleanup> s_tracker = new();

    // Secondary: handle -> HandleEntry. Holds a DIRECT reference to the per-handle
    // state (NOT a WeakReference<impl>) so that a Swift-driven deinit can detach the
    // handle even if the impl object is already unreachable. The entry's atomic
    // Released flag serializes Swift's deinit callback against the finalizer so
    // SwiftReleaseTrampoline.Release runs exactly once per handle.
    private static readonly ConcurrentDictionary<IntPtr, HandleEntry> s_entries = new();

    /// <summary>
    /// Associates an EveryProtocol handle with the lifetime of <paramref name="impl"/>.
    /// The tracker takes responsibility for calling
    /// <see cref="SwiftReleaseTrampoline.Release"/> on <paramref name="handle"/> when
    /// <paramref name="impl"/> becomes unreachable (unless Swift's deinit runs first,
    /// in which case <see cref="OnEveryProtocolDeinit"/> marks the entry released so
    /// the finalizer skips it).
    /// </summary>
    /// <remarks>
    /// Transactional publication: the secondary map is written FIRST with a
    /// placeholder-but-valid entry; if attaching the entry to the cleanup bundle
    /// throws, the secondary map write is rolled back so a subsequent
    /// <see cref="NotifyDeinit"/> sees no leftover state and the caller's
    /// compensating release is the only path that touches the handle. This
    /// guarantees Track is all-or-nothing with respect to the global handle index.
    /// </remarks>
    public static void Track(object impl, IntPtr handle)
    {
        if (impl is null)
            throw new ArgumentNullException(nameof(impl));
        if (handle == IntPtr.Zero)
            throw new ArgumentException("Handle cannot be zero", nameof(handle));

        var cleanup = s_tracker.GetValue(impl, static _ => new ProxyCleanup());
        // CRITICAL: HandleEntry must NOT hold a strong reference back to ProxyCleanup,
        // and s_entries must NOT have any path that reaches ProxyCleanup. Otherwise the
        // global s_entries dictionary roots the cleanup, the CWT impl-key weak collection
        // can never finalize it, and the +1 leak the tracker exists to fix is reintroduced.
        // The cleanup is only reachable from the CWT entry (keyed weakly by impl).
        var entry = new HandleEntry(handle);

        if (!s_entries.TryAdd(handle, entry))
            throw new InvalidOperationException($"Handle {handle.ToString($"X{IntPtr.Size * 2}")} is already tracked");

        try
        {
            cleanup.Add(entry);
        }
        catch
        {
            s_entries.TryRemove(handle, out _);
            throw;
        }
    }

    /// <summary>
    /// Called from <see cref="OnEveryProtocolDeinit"/> — marks <paramref name="handle"/>'s
    /// entry as released so the finalizer-driven release path is a no-op. Safe to call
    /// even if the owning impl has already been garbage-collected (the entry is held
    /// directly, not via the impl).
    /// </summary>
    internal static void NotifyDeinit(IntPtr handle)
    {
        if (!s_entries.TryRemove(handle, out var entry))
            return;

        // Claim the release — if the cleanup finalizer has already snapshotted the
        // _entries list, it will see this flag set and skip the trampoline release
        // (SwiftReleaseTrampoline.Release) for this entry.
        Interlocked.Exchange(ref entry.Released, 1);
    }

    /// <summary>
    /// Unmanaged callback invoked from the generated Swift <c>EveryProtocol.deinit</c>
    /// after Swift's last retain has dropped. Drops the <see cref="SwiftObjectRegistry"/>
    /// strong root and marks the handle's entry released so the finalizer-driven
    /// release path is skipped.
    ///
    /// <para>
    /// Called on an arbitrary Swift release thread. Must be idempotent and non-throwing
    /// across the ABI boundary. <c>[UnmanagedCallersOnly]</c> methods cannot be called
    /// from managed code directly; the body delegates to <see cref="OnEveryProtocolDeinitCore"/>
    /// so the managed unit tests can exercise the same logic.
    /// </para>
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static void OnEveryProtocolDeinit(IntPtr handle)
    {
        OnEveryProtocolDeinitCore(handle);
    }

    /// <summary>
    /// Shared implementation for the EveryProtocol deinit callback, callable from
    /// both the unmanaged trampoline and managed unit tests.
    /// </summary>
    internal static void OnEveryProtocolDeinitCore(IntPtr handle)
    {
        if (SwiftExitGuard.IsProcessExiting)
            return;

        try
        {
            SwiftObjectRegistry.Unregister(handle);
            NotifyDeinit(handle);
        }
        catch
        {
            // Cannot propagate exceptions across the Swift ABI boundary.
        }
    }

    /// <summary>
    /// Test helper: drops every tracked handle for <paramref name="impl"/> without
    /// calling the native release path (<see cref="SwiftReleaseTrampoline.Release"/>).
    /// Used by unit tests that register mock pointers which cannot be passed to
    /// <c>swift_release</c>. Each entry is marked released so any racing
    /// deinit/finalizer becomes a no-op, and the global secondary index is
    /// scrubbed of the handles.
    /// </summary>
    internal static bool TryDropAllForTest(object impl)
    {
        if (s_tracker.TryGetValue(impl, out var cleanup))
        {
            cleanup.DropAllForTest();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Test helper: reports whether a handle is still being tracked.
    /// </summary>
    internal static bool IsTrackedForTest(IntPtr handle)
        => s_entries.ContainsKey(handle);

    /// <summary>
    /// Per-handle entry. Held by both the global <see cref="s_entries"/> dictionary
    /// AND the per-impl <see cref="ProxyCleanup"/> list, with an atomic
    /// <see cref="Released"/> flag that the deinit callback and the finalizer race
    /// on so exactly one path releases the handle.
    ///
    /// <para>
    /// Deliberately does NOT carry a back-reference to its owning
    /// <see cref="ProxyCleanup"/>: such a back-reference would let the global
    /// <see cref="s_entries"/> dictionary transitively root the cleanup, defeating
    /// the impl-keyed <see cref="ConditionalWeakTable{TKey, TValue}"/> weak collection
    /// and reintroducing the original +1 leak. NotifyDeinit only flips Released —
    /// the cleanup's eventual finalizer is the path that runs the trampoline
    /// release (SwiftReleaseTrampoline.Release).
    /// </para>
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

    /// <summary>
    /// Per-impl cleanup bundle. One instance is associated with each tracked impl
    /// via <see cref="ConditionalWeakTable{TKey, TValue}"/>; its finalizer runs
    /// when the impl is garbage-collected.
    /// </summary>
    private sealed class ProxyCleanup
    {
        private readonly List<HandleEntry> _entries = new();
        private readonly object _lock = new();

        public void Add(HandleEntry entry)
        {
            lock (_lock)
            {
                _entries.Add(entry);
            }
        }

        public void Remove(HandleEntry entry)
        {
            lock (_lock)
            {
                _entries.Remove(entry);
            }
        }

        internal void DropAllForTest()
        {
            // Drop managed bookkeeping only — unit tests register mock pointers that
            // would crash swift_release. Each entry is marked released so a racing
            // deinit/finalizer is a no-op, and the secondary index is scrubbed.
            HandleEntry[] snapshot;
            lock (_lock)
            {
                snapshot = _entries.ToArray();
                _entries.Clear();
            }
            foreach (var entry in snapshot)
            {
                Interlocked.Exchange(ref entry.Released, 1);
                s_entries.TryRemove(entry.Handle, out _);
            }
        }

        private void ReleaseAll()
        {
            // Snapshot under lock to avoid mutating the list during iteration if
            // OnEveryProtocolDeinit races in on another thread.
            HandleEntry[] snapshot;
            lock (_lock)
            {
                if (_entries.Count == 0)
                    return;
                snapshot = _entries.ToArray();
                _entries.Clear();
            }

            foreach (var entry in snapshot)
            {
                // Atomically claim the release — if NotifyDeinit already marked it
                // (Swift deinit beat us to the finalizer), skip this entry so
                // SwiftReleaseTrampoline.Release is not called on a dead pointer.
                if (Interlocked.Exchange(ref entry.Released, 1) == 1)
                    continue;

                s_entries.TryRemove(entry.Handle, out _);
                try
                {
                    // Finalizer thread: must route through the SBW_SwiftRelease Swift
                    // wrapper, not Arc.Release directly. Arc.cs documents the direct
                    // path as crashing Mono with `jit-info.c:918 !ji->async` after
                    // CallConvSwift JIT state contamination — the same reason
                    // SwiftClassHandle<T>.ReleaseHandle uses the trampoline.
                    SwiftReleaseTrampoline.Release(entry.Handle);
                }
                catch
                {
                    // Already deallocating via a race — ignore.
                }
            }
        }

        ~ProxyCleanup()
        {
            // Mirror SwiftClassHandle's process-exit guard: skip native release
            // during shutdown because the Swift runtime may be partially torn down.
            if (SwiftExitGuard.IsProcessExiting)
                return;

            try
            {
                ReleaseAll();
            }
            catch
            {
                // Finalizers must not throw.
            }
        }
    }
}
