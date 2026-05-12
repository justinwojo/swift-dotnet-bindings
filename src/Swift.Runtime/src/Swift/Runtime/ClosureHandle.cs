// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Lifetime policy for a managed delegate pinned behind a Swift-side closure ABI.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public enum ClosureHandlePolicy
{
    /// <summary>
    /// Swift assumes ownership of the captured <see cref="GCHandle"/> through the
    /// <c>_SBClosureCtx</c> ARC box on a successful P/Invoke (the box's
    /// <c>deinit</c> upcalls <see cref="SwiftClosureContext"/> to free the handle
    /// exactly once). The wrapper still owns the handle until ownership transfers,
    /// so a throw between <see cref="ClosureHandle"/> construction and
    /// <see cref="ClosureHandle.MarkOwnershipTransferred"/> frees it locally.
    /// </summary>
    Escaping,

    /// <summary>
    /// Swift invokes the trampoline synchronously inside the call and never
    /// retains the closure. The wrapper owns the captured handle for the
    /// duration of the call and frees it in its <c>finally</c> regardless of
    /// success or failure.
    /// </summary>
    NonEscaping,
}

/// <summary>
/// Wraps a <see cref="GCHandle"/> rooting a managed delegate that backs a
/// Swift-side closure trampoline. Encapsulates the per-call lifetime contract
/// that previously lived inline at every <c>GCHandle.Alloc</c> site in the
/// generator and that produced the 0.10.0 leak retrospective's "we forgot the
/// matching release on one of N paths" failure mode.
/// </summary>
/// <remarks>
/// <para>
/// Usage from generated wrappers — both policies share the same try/finally
/// shape so the emitter can treat them uniformly:
/// </para>
/// <code>
/// var __gcHandle = new ClosureHandle(__inner, ClosureHandlePolicy.NonEscaping);
/// try {
///     NativeMethods.SomeEntryPoint(/* ... */, __gcHandle.Context);
///     __gcHandle.MarkOwnershipTransferred(); // no-op for NonEscaping
/// } finally {
///     __gcHandle.Dispose();
/// }
/// </code>
/// <para>
/// <see cref="Dispose"/> is idempotent and safe on a default-constructed
/// instance (no allocated handle). Optional-closure paths pre-declare
/// <c>ClosureHandle __gcHandle = default;</c> at method scope so the
/// <c>finally</c> can dispose unconditionally regardless of whether the
/// caller provided a delegate.
/// </para>
/// <para>
/// <strong>Do not copy this struct.</strong> It is a value type wrapping a
/// non-copyable owning resource (the <see cref="GCHandle"/> token). A copy
/// shares the same underlying token but has its own <c>_disposed</c> flag,
/// so disposing the original leaves the copy holding a stale token whose
/// state is incoherent — subsequent operations on the copy are invalid and
/// have runtime-dependent behavior (some .NET versions silently accept a
/// repeated free, others throw <see cref="InvalidOperationException"/>).
/// Generated wrappers keep the instance as a single local; do not pass it
/// by value, store it in another struct/field, or capture it in a lambda.
/// If you need shared ownership, share the <see cref="Context"/> pointer
/// and dispose the original instance exactly once.
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct ClosureHandle : IDisposable
{
    private GCHandle _handle;
    private readonly ClosureHandlePolicy _policy;
    private bool _transferred;
    private bool _disposed;

    /// <summary>
    /// Allocates a normal (movable, non-pinned) <see cref="GCHandle"/> rooting
    /// <paramref name="target"/> and records the lifetime policy that governs
    /// when <see cref="Dispose"/> frees the handle.
    /// </summary>
    /// <param name="target">The managed delegate (or other object) to root.</param>
    /// <param name="policy">Lifetime policy — see <see cref="ClosureHandlePolicy"/>.</param>
    public ClosureHandle(object target, ClosureHandlePolicy policy)
    {
        _handle = GCHandle.Alloc(target);
        _policy = policy;
        _transferred = false;
        _disposed = false;
    }

    /// <summary>
    /// Opaque context pointer to pass to Swift as the closure's context slot.
    /// Returns <see cref="IntPtr.Zero"/> for a default-constructed instance.
    /// </summary>
    public IntPtr Context => _handle.IsAllocated ? GCHandle.ToIntPtr(_handle) : IntPtr.Zero;

    /// <summary>
    /// Whether the underlying <see cref="GCHandle"/> is currently allocated.
    /// </summary>
    public bool IsAllocated => _handle.IsAllocated;

    /// <summary>
    /// Records that the Swift-side <c>_SBClosureCtx</c> box now owns the
    /// captured handle, so <see cref="Dispose"/> must not free it. No-op for
    /// <see cref="ClosureHandlePolicy.NonEscaping"/>.
    /// </summary>
    /// <remarks>
    /// Call immediately after the P/Invoke returns successfully and before
    /// any code that can throw. If construction succeeds but the call throws
    /// before this method runs, <see cref="Dispose"/> frees the handle so the
    /// pinned delegate isn't leaked.
    /// </remarks>
    public void MarkOwnershipTransferred()
    {
        if (_policy == ClosureHandlePolicy.Escaping)
            _transferred = true;
    }

    /// <summary>
    /// Frees the underlying <see cref="GCHandle"/> when the wrapper still
    /// owns it. Idempotent — subsequent calls are no-ops.
    /// </summary>
    /// <remarks>
    /// Behavior per policy:
    /// <list type="bullet">
    /// <item><see cref="ClosureHandlePolicy.NonEscaping"/>: always frees on
    /// first dispose.</item>
    /// <item><see cref="ClosureHandlePolicy.Escaping"/>: frees only if
    /// <see cref="MarkOwnershipTransferred"/> was not called (P/Invoke threw
    /// before Swift constructed the owner-token box).</item>
    /// </list>
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        bool shouldFree = _policy == ClosureHandlePolicy.NonEscaping || !_transferred;
        if (shouldFree && _handle.IsAllocated)
            _handle.Free();
    }
}
