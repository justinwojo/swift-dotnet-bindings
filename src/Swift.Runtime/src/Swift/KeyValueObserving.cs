// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Subset of Foundation's <c>NSKeyValueObservingOptions</c> supported by the
/// v1 KVO bridge. The raw bit values match Foundation's enum so the flags
/// can be passed unchanged to the generated Swift @_cdecl observe
/// trampoline, which reconstructs them via
/// <c>NSKeyValueObservingOptions(rawValue:)</c>.
///
/// <para>v1 only forwards <c>change.newValue</c> (or the current property
/// value as a fallback) to the C# change handler, so <c>Old</c> and
/// <c>Prior</c> from the underlying Foundation enum are deliberately not
/// exposed here — they would be silently ignored by the
/// <c>Action&lt;C, V&gt;</c> dispatch surface. Adding them is a follow-up
/// that changes both the emitted Swift shim and the C# handler shape.</para>
/// </summary>
[Flags]
public enum SbwKvoOptions : uint
{
    None = 0,
    /// <summary>Deliver the new value in the change handler.</summary>
    New = 1 << 0,
    /// <summary>Fire the change handler once at subscription time, with the
    /// current value treated as the "new" value.</summary>
    Initial = 1 << 2,
}

/// <summary>
/// Managed wrapper around a Swift-side <c>NSKeyValueObservation</c> token plus
/// the <see cref="GCHandle"/> rooting the C# change handler. Generated KVO
/// observe extension methods return one of these. On <see cref="Dispose"/>
/// the wrapper calls the per-class <c>SBW_KVO_&lt;Class&gt;_invalidate</c> shim
/// (which calls <c>invalidate()</c> and releases the token) and frees the
/// GCHandle. The wrapper is idempotent — repeated Dispose is a no-op, and
/// the GCHandle is freed inside a <c>finally</c> so it is released even if
/// the native invalidator throws.
///
/// <para>There is intentionally no finalizer: the Swift KVO observation keeps
/// the <see cref="GCHandle"/> slot live as the <c>ctx</c> pointer it forwards
/// into the dispatch trampoline on every mutation. Freeing the handle from a
/// finalizer while the observation is still subscribed would let a later KVO
/// notification dereference a recycled handle slot (use-after-free). Callers
/// MUST <see cref="Dispose"/>: an undisposed token leaks both the
/// <c>NSKeyValueObservation</c> +1 retain and the <see cref="GCHandle"/>
/// rooting the managed handler. Treat the returned token like any other
/// disposable resource (<c>using</c> / <c>using var</c>).</para>
/// </summary>
public sealed class KvoToken : IDisposable
{
    private IntPtr _token;
    private GCHandle _handle;
    private readonly Action<IntPtr> _invalidator;

    /// <param name="token">+1-retained NSKeyValueObservation token from Swift.</param>
    /// <param name="handle">GCHandle rooting the managed change handler.</param>
    /// <param name="invalidator">Per-class P/Invoke into the
    /// <c>SBW_KVO_&lt;Class&gt;_invalidate</c> shim.</param>
    public KvoToken(IntPtr token, GCHandle handle, Action<IntPtr> invalidator)
    {
        _token = token;
        _handle = handle;
        _invalidator = invalidator ?? throw new ArgumentNullException(nameof(invalidator));
    }

    public void Dispose()
    {
        try
        {
            if (_token != IntPtr.Zero)
            {
                var token = _token;
                _token = IntPtr.Zero;
                _invalidator(token);
            }
        }
        finally
        {
            if (_handle.IsAllocated)
                _handle.Free();
        }
    }
}
