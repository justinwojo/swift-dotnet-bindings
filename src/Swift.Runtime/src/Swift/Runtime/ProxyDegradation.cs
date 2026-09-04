// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Threading;

#nullable enable

namespace Swift.Runtime;

/// <summary>
/// Describes one reverse-dispatch callback that arrived at a <b>consumer-owned</b> protocol carrier
/// after the consumer's implementation had been collected. Raised on
/// <see cref="ProxyDegradation.ImplCollected"/> — at most once per carrier — so an application can
/// discover why its delegate stopped receiving callbacks.
/// </summary>
public sealed class SwiftProxyImplCollectedEventArgs : EventArgs
{
    /// <summary>Creates the event payload.</summary>
    /// <param name="handle">The Swift conformer-box handle whose implementation was collected.</param>
    /// <param name="member">The protocol member Swift called (e.g. <c>MyDelegate.DidUpdate()</c>).</param>
    public SwiftProxyImplCollectedEventArgs(IntPtr handle, string member)
    {
        Handle = handle;
        Member = member ?? string.Empty;
    }

    /// <summary>The Swift conformer-box handle whose implementation was collected.</summary>
    public IntPtr Handle { get; }

    /// <summary>The protocol member Swift called when the implementation could not be resolved.</summary>
    public string Member { get; }
}

/// <summary>
/// Thrown across the Swift error channel when a <c>throws</c> reverse-dispatch requirement is called
/// on a consumer-owned carrier whose implementation has already been collected. Swift sees an ordinary
/// thrown error; a consumer who wants the callbacks to keep arriving must hold their implementation for
/// as long as the Swift side may call it.
/// </summary>
public sealed class SwiftProxyImplCollectedException : InvalidOperationException
{
    /// <summary>Creates the exception with a fully-formed diagnostic.</summary>
    public SwiftProxyImplCollectedException(string message) : base(message)
    {
    }
}

/// <summary>
/// The degradation channel for a <b>consumer-owned</b> protocol carrier — the lane a C# implementation
/// takes when it is assigned into a non-retaining Swift slot (<c>weak</c>/<c>unowned</c>/<c>unowned(unsafe)</c>).
///
/// <para>On the Swift-rooted lane a reverse-dispatch callback that cannot resolve its implementation is
/// an invariant violation: the tracker holds a strong root for exactly as long as Swift holds the box, so
/// a null resolve means something corrupted that pairing, and the receiver
/// <see cref="Environment.FailFast(string)"/>s. On the consumer-owned lane the same null resolve is a
/// <i>legal</i> state reachable from ordinary application code — Swift may hold the conformer box through
/// some other strong reference (an internal array, a captured closure, an in-flight operation) after the
/// consumer has dropped the implementation, and a callback can also race a drop on another thread. Killing
/// the process for that would punish a legitimate Swift lifecycle pattern, so the receiver degrades the way
/// Swift itself treats a <c>nil</c> weak delegate: the call becomes a no-op, or returns the identity value
/// of its return type, or throws through the Swift error channel where the requirement has one.</para>
///
/// <para>Because a degraded callback is silent by construction, every degraded carrier reports itself here
/// exactly once — through <see cref="ImplCollected"/> and a <see cref="System.Diagnostics.Trace"/> line — so
/// "my delegate stopped firing" is diagnosable without a debugger. Subsequent callbacks on the same carrier
/// stay silent so a per-frame delegate cannot flood the log.</para>
/// </summary>
public static class ProxyDegradation
{
    // Carriers that have already reported. Keyed by conformer-box handle so the "once" is per proxy,
    // not per member or per call. Entries are dropped when the box deinitializes (Forget), so a handle
    // the allocator later reuses starts clean.
    private static readonly ConcurrentDictionary<IntPtr, byte> s_reported = new();
    private static long s_reportCount;

    /// <summary>
    /// Raised the first time a consumer-owned carrier degrades a reverse-dispatch callback because its
    /// implementation had been collected. Handlers run on whichever thread Swift called in on — including
    /// a Swift-owned queue — and must not throw; an exception from a handler is caught and dropped (the
    /// callback is mid-way across a native boundary where a managed throw would abort the process). Each
    /// subscriber is invoked under its own guard, so one that throws does not stop the ones behind it.
    /// </summary>
    public static event EventHandler<SwiftProxyImplCollectedEventArgs>? ImplCollected;

    /// <summary>
    /// Total number of carriers that have reported a degraded callback in this process. One per carrier,
    /// not one per call — a probe reads this to assert the diagnostic fired, and fired only once.
    /// </summary>
    public static long ReportCount => Volatile.Read(ref s_reportCount);

    /// <summary>
    /// Reports that <paramref name="member"/> was reverse-dispatched onto a consumer-owned carrier whose
    /// implementation is gone. Fires <see cref="ImplCollected"/> and writes a trace line the FIRST time a
    /// given <paramref name="handle"/> degrades; later calls on that handle are silent. Never throws — it
    /// is called from an <c>[UnmanagedCallersOnly]</c> receiver.
    /// </summary>
    /// <param name="handle">The conformer-box handle Swift dispatched through.</param>
    /// <param name="member">The protocol member Swift called.</param>
    /// <returns>
    /// <c>true</c> when this call was the one that claimed the carrier's report latch — that answer is
    /// decided by the latch alone, so it stays <c>true</c> even if a trace listener or an
    /// <see cref="ImplCollected"/> subscriber throws while the diagnostic is being delivered.
    /// </returns>
    public static bool ReportCollectedImpl(IntPtr handle, string member)
    {
        if (!s_reported.TryAdd(handle, 0))
            return false;

        Interlocked.Increment(ref s_reportCount);

        // Everything below is best-effort delivery of a diagnostic. The latch above already decided
        // this call is the reporting one, so no failure past this point may change that answer or
        // stop a later step: a subscriber that throws must not silence the subscriber behind it, and
        // a managed exception must not unwind across the native receiver boundary that called us.
        try
        {
            System.Diagnostics.Trace.WriteLine(BuildMessage(handle, member));
        }
        catch
        {
            // A trace listener threw. The event below is the other half of the diagnostic.
        }

        var handlers = ImplCollected;
        if (handlers is not null)
        {
            var args = new SwiftProxyImplCollectedEventArgs(handle, member);
            // Walked one subscriber at a time rather than through the combined delegate: a multicast
            // invoke stops at the first exception, so one badly-written handler would hide every
            // handler registered after it.
            foreach (var entry in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<SwiftProxyImplCollectedEventArgs>)entry)(null, args);
                }
                catch
                {
                    // Documented contract: a handler must not throw. One that does is isolated here.
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Drops the once-per-carrier report latch for <paramref name="handle"/>. Called when the Swift
    /// conformer box deinitializes, so the handle value can be recycled by a later allocation without
    /// inheriting an already-reported state.
    /// </summary>
    internal static void Forget(IntPtr handle) => s_reported.TryRemove(handle, out _);

    /// <summary>
    /// The error a <c>throws</c> reverse-dispatch requirement surfaces to Swift when its consumer-owned
    /// implementation has been collected. Reports the degradation first, so the throw is discoverable the
    /// same way a silent no-op is.
    /// </summary>
    public static Exception CollectedImplError(IntPtr handle, string member)
    {
        ReportCollectedImpl(handle, member);
        return new SwiftProxyImplCollectedException(BuildMessage(handle, member));
    }

    /// <summary>
    /// Terminal for a consumer-owned carrier whose implementation was collected and whose requirement
    /// returns a type with no synthesizable Swift value — a non-optional class or existential, an
    /// enumeration with no defined zero case, an aggregate whose zeroed bytes are not a valid instance.
    /// There is nothing valid to hand back to Swift and fabricating bytes would corrupt the boundary,
    /// so this is a controlled <see cref="Environment.FailFast(string)"/> — but the message names the
    /// consumer-owned lane and the fix (hold the implementation for as long as the Swift side may call
    /// it), and says plainly that it is a lifetime mistake in application code, not a binding defect.
    /// <para>Shaped as a throw-helper (returns an <see cref="Exception"/> the caller <c>throw</c>s) for the
    /// same reason as the Swift-rooted backstop: C#'s definite-return analysis is syntactic, so a
    /// value-returning receiver needs the <c>throw</c> token even though the process is already gone.</para>
    /// </summary>
    public static Exception FailFastUnsatisfiableReturn(IntPtr handle, string member, string returnDescription)
    {
        ReportCollectedImpl(handle, member);
        var message =
            $"Swift called '{member}' on a C# implementation that was already collected, and the requirement returns "
            + $"'{returnDescription}' — a type with no value this binding can synthesize on the caller's behalf. "
            + $"(Conformer box 0x{handle.ToString("X")}.) The implementation was assigned into a non-retaining Swift "
            + "slot (weak/unowned), so Swift never kept it alive; the Swift object still holds the conformer through "
            + "some other reference and is still calling back. Keep a reference to the implementation for as long as "
            + "the Swift side may call it. This is a lifetime mistake in application code, not a binding defect.";
        Environment.FailFast(message);
        return new SwiftProxyImplCollectedException(message); // unreachable: FailFast terminated the process
    }

    private static string BuildMessage(IntPtr handle, string member)
        => $"[SwiftBindings] Swift called '{member}' on a C# implementation that was already collected "
           + $"(conformer box 0x{handle.ToString("X")}). The implementation was assigned into a non-retaining Swift "
           + "slot (weak/unowned), so nothing on the Swift side kept it alive; the call was degraded to its "
           + "default result instead of dispatching. Keep a reference to the implementation for as long as the "
           + "Swift side may call it.";
}
