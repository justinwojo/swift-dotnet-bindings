// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

#nullable enable

namespace Swift.Runtime;

/// <summary>
/// Lightweight ARC-bridged SafeHandle for Swift class instances.
/// The handle IS the Swift object pointer (no buffer indirection).
///
/// Unlike <see cref="SwiftSafeHandle{T}"/> which owns an intermediate buffer,
/// <c>SwiftClassHandle&lt;T&gt;</c> directly holds the retained Swift object pointer.
/// ReleaseHandle calls <see cref="Arc.Release"/> to decrement the ARC reference count.
///
/// Finalizer-safe: <see cref="Arc.Release"/> uses <c>CallingConvention.Cdecl</c>
/// (direct P/Invoke to swift_release), not CallConvSwift. This avoids the
/// jit-info.c:918 assertion crash that affects CallConvSwift on Mono.
///
/// Process exit safety: During process exit, finalizer-triggered Arc.Release calls are
/// skipped to avoid crashes from Swift deinitializers running against a partially torn-down
/// runtime. Explicit Dispose() on a user thread still releases — Swift deinit may have
/// side effects (flushing, closing, persisting) that should run during graceful shutdown.
/// On NativeAOT/iOS, GC finalization can start before AppDomain.ProcessExit fires,
/// so the guard also checks Environment.HasShutdownStarted.
/// </summary>
[DebuggerDisplay("{DebugDisplay}")]
public sealed class SwiftClassHandle<T> : SafeHandleZeroOrMinusOneIsInvalid where T : ISwiftObject
{
    /// <summary>
    /// Returns a SwiftClassHandle with a zero (invalid) value.
    /// </summary>
    public static readonly SwiftClassHandle<T> Zero = new SwiftClassHandle<T>(IntPtr.Zero);

    /// <summary>
    /// Tracks whether Dispose() was explicitly called (vs finalizer).
    /// Explicit Dispose always releases, even during process exit — Swift deinit
    /// may have side effects that should run during graceful shutdown.
    /// </summary>
    private volatile bool _explicitDispose;

    /// <summary>
    /// Constructs a SwiftClassHandle from a retained Swift object pointer.
    /// The pointer must carry a +1 ARC retain that this handle takes ownership of.
    /// </summary>
    /// <param name="swiftObjectPointer">A retained Swift class object pointer.</param>
    public SwiftClassHandle(IntPtr swiftObjectPointer)
        : base(ownsHandle: true)
    {
        SetHandle(swiftObjectPointer);
    }

    private string DebugDisplay => IsClosed || IsInvalid
        ? $"SwiftClassHandle<{typeof(T).Name}> [DISPOSED]"
        : $"SwiftClassHandle<{typeof(T).Name}> (0x{handle:X})";

    /// <summary>
    /// Disposes the handle, deterministically releasing the Swift ARC reference.
    /// Suppresses finalization since cleanup is already handled.
    ///
    /// Unlike struct handles, explicit Dispose is NOT required for correctness —
    /// the finalizer also calls Arc.Release safely. Use Dispose for deterministic
    /// cleanup of scarce resources (same pattern as FileStream).
    ///
    /// During process exit, finalizer-triggered Arc.Release is skipped, but
    /// explicit Dispose still releases — Swift deinit may have side effects.
    /// </summary>
    public new void Dispose()
    {
        _explicitDispose = true;
        GC.SuppressFinalize(this);
        base.Dispose();
    }

    /// <summary>
    /// Releases the Swift ARC reference. Called by both explicit Dispose and finalizer.
    /// Thread-safe: swift_release is an atomic decrement. Swift deinit may run but
    /// has no thread affinity requirement (same as ObjC dealloc in Xamarin).
    ///
    /// During process exit, finalizer-triggered releases are skipped to avoid crashes
    /// from Swift deinitializers running against a partially torn-down Swift runtime.
    /// Explicit Dispose() still releases — the caller is on a user thread where Swift
    /// deinit side effects (flushing, closing) should still run during graceful shutdown.
    /// On NativeAOT/iOS, GC finalization can start before AppDomain.ProcessExit fires,
    /// so the guard also checks Environment.HasShutdownStarted.
    /// </summary>
    protected override bool ReleaseHandle()
    {
        if (handle == IntPtr.Zero)
            return true;

        // During process exit, skip Arc.Release for finalizer-triggered cleanup only.
        // Explicit Dispose() still releases — Swift deinit may flush/close/persist.
        // On NativeAOT/iOS, GC finalization can start before ProcessExit fires,
        // and the Swift runtime may already be partially torn down.
        if (SwiftExitGuard.IsProcessExiting && !_explicitDispose)
        {
            handle = IntPtr.Zero;
            return true;
        }

        try
        {
            // Arc.Release uses CallingConvention.Cdecl (NOT CallConvSwift).
            // This is safe from the GC finalizer thread on both Mono and NativeAOT.
            // Deinit runs on the releasing thread — no thread affinity issue.
            Arc.Release(handle);
        }
        catch
        {
            // Swallow — ReleaseHandle must not throw per SafeHandle contract.
            // Arc.Release can throw if the object is already deallocating,
            // which shouldn't happen in normal usage but we guard defensively.
        }

        handle = IntPtr.Zero;
        return true;
    }
}
