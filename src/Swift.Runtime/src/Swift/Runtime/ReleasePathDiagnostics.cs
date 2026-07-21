// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Threading;

namespace Swift.Runtime;

/// <summary>
/// Lightweight, process-global, aggregate counters over the value-witness RELEASE paths, so a
/// leak probe that never balances can say WHY a release did or did not run instead of only that a
/// live count stayed non-zero. Every counter is a plain interlocked <see cref="long"/> bumped only
/// on a release/finalize path that already crosses a native boundary (a value-witness Destroy, a
/// finalizer trampoline call), so the added cost is one interlocked add on an already-expensive
/// operation. There is deliberately no per-object state — these are totals a test resets before a
/// churn and reads after, not a per-instance ledger.
///
/// The disambiguation the counters buy: for a struct-with-ref wrapper whose embedded reference
/// leaked, the survivor identity proves the Swift deinit never ran but not why. The three
/// end-states are:
/// (a) the wrapper's SafeHandle was never finalized (a managed root) — no finalizer counter moves
///     for it, so the finalizer Destroy-invoked total falls short of the wrapper count and no
///     skip/catch counter fired;
/// (b) the SafeHandle was finalized but the Destroy was skipped — a metadata-zero skip or a
///     swallowed finalizer catch is non-zero;
/// (c) the Destroy ran yet the value stayed live — the finalizer Destroy-invoked total matches the
///     wrapper count, so the extra reference came from construction; if a wire-buffer Destroy was
///     skipped (its entered/completed totals disagree, or its metadata-unavailable / skipped-invalid
///     counter fired) that skip is the orphaned reference, otherwise the imbalance is upstream of
///     every path counted here.
/// The wire-buffer counters aggregate both wire-Destroy variants — the direct consuming Destroy and
/// the finalizer-safe (Cdecl trampoline) one — so a struct-with-ref churn (which uses only the
/// direct variant) and an existential-proxy probe (which uses the finalizer-safe one) both read out
/// through the same wireDestroy totals.
/// In a clean run every skip/catch counter is zero, so any non-zero value on a failing run names
/// the exact fail-open branch that dropped the release.
/// </summary>
internal static class ReleasePathDiagnostics
{
    private static long _wireDestroyEntered;
    private static long _wireDestroyCompleted;
    private static long _wireDestroyMetadataUnavailable;
    private static long _wireDestroySkippedInvalid;
    private static long _finalizerVwtDestroyInvoked;
    private static long _finalizerMetadataZeroSkip;
    private static long _finalizerReleaseCatch;
    private static long _disposeVwtDestroyInvoked;
    private static long _disposeMetadataInvalidSkip;
    private static long _disposeReleaseCatch;

    /// <summary>
    /// A wire-buffer value-witness Destroy was entered with a resolved, valid metadata. Covers both
    /// the direct-call consuming Destroy and the finalizer-safe (Cdecl trampoline) variant, since
    /// both release the same wire-buffer retains.
    /// </summary>
    internal static void OnWireDestroyEntered() => Interlocked.Increment(ref _wireDestroyEntered);

    /// <summary>A wire-buffer value-witness Destroy ran to completion.</summary>
    internal static void OnWireDestroyCompleted() => Interlocked.Increment(ref _wireDestroyCompleted);

    /// <summary>
    /// The generic consuming-Destroy overload could not resolve metadata and returned WITHOUT
    /// destroying (its fail-open catch). A source-buffer reference is orphaned when this fires.
    /// </summary>
    internal static void OnWireDestroyMetadataUnavailable() => Interlocked.Increment(ref _wireDestroyMetadataUnavailable);

    /// <summary>
    /// A wire-buffer Destroy returned early because metadata was invalid, skipping the value-witness
    /// Destroy and orphaning the buffer's retains. Covers both the direct-call and finalizer-safe
    /// variants. A null buffer is a genuine no-op (nothing to orphan) and is deliberately NOT counted.
    /// </summary>
    internal static void OnWireDestroySkippedInvalid() => Interlocked.Increment(ref _wireDestroySkippedInvalid);

    /// <summary>The GC-finalizer release reached and invoked the VWT Destroy trampoline.</summary>
    internal static void OnFinalizerVwtDestroyInvoked() => Interlocked.Increment(ref _finalizerVwtDestroyInvoked);

    /// <summary>
    /// The GC-finalizer release skipped the VWT Destroy because metadata was not cached at
    /// construction (<c>_metadataHandle == IntPtr.Zero</c>). The buffer is freed without releasing
    /// the embedded reference.
    /// </summary>
    internal static void OnFinalizerMetadataZeroSkip() => Interlocked.Increment(ref _finalizerMetadataZeroSkip);

    /// <summary>The GC-finalizer release swallowed an exception from the VWT Destroy trampoline.</summary>
    internal static void OnFinalizerReleaseCatch() => Interlocked.Increment(ref _finalizerReleaseCatch);

    /// <summary>The explicit-Dispose release invoked the VWT Destroy.</summary>
    internal static void OnDisposeVwtDestroyInvoked() => Interlocked.Increment(ref _disposeVwtDestroyInvoked);

    /// <summary>
    /// The explicit-Dispose release re-resolved metadata that came back invalid and freed the buffer
    /// WITHOUT a VWT Destroy, orphaning the embedded retains. The dispose-path counterpart to the
    /// finalizer's metadata-zero skip.
    /// </summary>
    internal static void OnDisposeMetadataInvalidSkip() => Interlocked.Increment(ref _disposeMetadataInvalidSkip);

    /// <summary>The explicit-Dispose release swallowed an exception from the VWT Destroy.</summary>
    internal static void OnDisposeReleaseCatch() => Interlocked.Increment(ref _disposeReleaseCatch);

    /// <summary>
    /// Zeroes every counter. A leak probe calls this at the start of a churn so the readings it
    /// surfaces on failure cover only that churn's release activity.
    /// </summary>
    internal static void Reset()
    {
        Interlocked.Exchange(ref _wireDestroyEntered, 0);
        Interlocked.Exchange(ref _wireDestroyCompleted, 0);
        Interlocked.Exchange(ref _wireDestroyMetadataUnavailable, 0);
        Interlocked.Exchange(ref _wireDestroySkippedInvalid, 0);
        Interlocked.Exchange(ref _finalizerVwtDestroyInvoked, 0);
        Interlocked.Exchange(ref _finalizerMetadataZeroSkip, 0);
        Interlocked.Exchange(ref _finalizerReleaseCatch, 0);
        Interlocked.Exchange(ref _disposeVwtDestroyInvoked, 0);
        Interlocked.Exchange(ref _disposeMetadataInvalidSkip, 0);
        Interlocked.Exchange(ref _disposeReleaseCatch, 0);
    }

    /// <summary>
    /// A single-line snapshot of the current counter totals for a leak-probe failure message.
    /// </summary>
    internal static string Snapshot()
    {
        return "release-path counters: "
            + $"wireDestroy(entered={Volatile.Read(ref _wireDestroyEntered)}, "
            + $"completed={Volatile.Read(ref _wireDestroyCompleted)}, "
            + $"metadataUnavailable={Volatile.Read(ref _wireDestroyMetadataUnavailable)}, "
            + $"skippedInvalid={Volatile.Read(ref _wireDestroySkippedInvalid)}); "
            + $"finalizer(vwtDestroyInvoked={Volatile.Read(ref _finalizerVwtDestroyInvoked)}, "
            + $"metadataZeroSkip={Volatile.Read(ref _finalizerMetadataZeroSkip)}, "
            + $"releaseCatch={Volatile.Read(ref _finalizerReleaseCatch)}); "
            + $"dispose(vwtDestroyInvoked={Volatile.Read(ref _disposeVwtDestroyInvoked)}, "
            + $"metadataInvalidSkip={Volatile.Read(ref _disposeMetadataInvalidSkip)}, "
            + $"releaseCatch={Volatile.Read(ref _disposeReleaseCatch)})";
    }
}
