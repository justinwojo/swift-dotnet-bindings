// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

#nullable enable

namespace Swift.Runtime;

/// <summary>
/// Represents an opaque raw handle to a Swift object.
/// Used internally in private constructors to prevent conflicts with public IntPtr constructors.
/// </summary>
public struct SwiftHandle
{
    /// <summary>
    /// The handle to the Swift native object
    /// </summary>
    public IntPtr Handle { get; }

    /// <summary>
    /// Constructs a SwiftHandle from the given IntPtr
    /// </summary>
    public SwiftHandle(IntPtr handle)
    {
        Handle = handle;
    }

    /// <summary>
    /// Implicit conversion from SwiftHandle to IntPtr
    /// </summary>
    public static implicit operator IntPtr(SwiftHandle value)
    {
        return value.Handle;
    }

    /// <summary>
    /// Explicit conversion from IntPtr to SwiftHandle
    /// </summary>
    public static implicit operator SwiftHandle(IntPtr value)
    {
        return new SwiftHandle(value);
    }
}

/// <summary>
/// Represents an opaque handle to a Swift object of type T.
/// Used to manage native memory associated with a Swift object of type T.
/// </summary>
[DebuggerDisplay("{DebugDisplay}")]
public sealed class SwiftSafeHandle<T> : SafeHandleZeroOrMinusOneIsInvalid where T : ISwiftObject
{
    /// <summary>
    /// Returns a SwiftSafeHandle with a zero value
    /// </summary>
    public readonly static SwiftSafeHandle<T> Zero = new SwiftSafeHandle<T>(IntPtr.Zero);

    /// <summary>
    /// Registers a custom destroy action for this SafeHandle type parameter.
    /// This method exists for backward compatibility with previously-generated bindings
    /// that emitted @_cdecl destroy wrappers. New bindings no longer emit these wrappers
    /// since VWT Destroy via CallConvSwift is proven safe on both Mono and NativeAOT.
    /// The registered action is ignored — VWT Destroy is always used directly.
    /// </summary>
    /// <param name="action">The destroy action (ignored).</param>
    [Obsolete("VWT Destroy via CallConvSwift works on both runtimes. @_cdecl destroy wrappers are no longer generated.")]
    public static void RegisterDestroyAction(Action<IntPtr>? action)
    {
        // No-op for backward compatibility. Previously-generated bindings may call this
        // during static initialization, but the action is not stored or used.
    }

    /// <summary>
    /// Cached Mono runtime detection for finalizer safety decisions.
    /// On Mono, VWT Destroy from the finalizer thread can trigger jit-info.c:918 crashes
    /// (the async assertion). On NativeAOT (production), VWT Destroy is safe from any thread.
    /// </summary>
    private static readonly bool s_isMonoRuntime = SwiftRuntimeInfo.IsMonoRuntime;

    /// <summary>
    /// Tracks whether Dispose() was explicitly called.
    /// If true, we're in explicit disposal — VWT Destroy always runs (safe from user thread).
    /// If false when ReleaseHandle runs, we're in finalization — VWT Destroy runs on
    /// NativeAOT (safe) but is skipped on Mono (jit-info.c:918 crash risk).
    /// </summary>
    private volatile bool _explicitDispose;

    /// <summary>
    /// Constructs a SwiftSafeHandle from the given IntPtr
    /// </summary>
    public SwiftSafeHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    /// <summary>
    /// Checks whether the process is exiting. Delegates to the shared flag on
    /// <see cref="SwiftExitGuard"/> to avoid duplicate ProcessExit handlers.
    /// </summary>
    internal static bool IsProcessExiting => SwiftExitGuard.IsProcessExiting;

    private string DebugDisplay => IsClosed || IsInvalid
        ? $"SwiftSafeHandle<{typeof(T).Name}> [DISPOSED]"
        : $"SwiftSafeHandle<{typeof(T).Name}> (0x{handle:X})";

    /// <summary>
    /// Disposes the handle. Call this to properly clean up Swift resources.
    /// During explicit disposal, VWT Destroy is called to decrement reference counts.
    /// Also suppresses finalization since cleanup is already handled.
    /// </summary>
    /// <remarks>
    /// Failing to call Dispose() will cause the finalizer to run. On NativeAOT (production),
    /// VWT Destroy runs from the finalizer, providing the same cleanup as explicit Dispose.
    /// On Mono (simulator), the finalizer only frees the buffer without calling Destroy —
    /// use 'using' or call Dispose() explicitly in dev builds.
    /// </remarks>
    public new void Dispose()
    {
        _explicitDispose = true;
        GC.SuppressFinalize(this);
        base.Dispose();
    }

    /// <summary>
    /// Releases the handle to the Swift object.
    /// This method must not throw exceptions per the SafeHandle contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// During explicit disposal: calls VWT Destroy to properly decrement reference counts,
    /// then frees the buffer. VWT Destroy via <c>delegate* unmanaged[Swift]</c> is proven
    /// safe on both Mono and NativeAOT from user threads.
    /// </para>
    /// <para>
    /// During finalization on NativeAOT: calls VWT Destroy — safe from the finalizer thread.
    /// This makes struct lifecycle match class lifecycle: the finalizer is a reliable safety net.
    /// </para>
    /// <para>
    /// During finalization on Mono: skips Destroy to avoid jit-info.c:918 crashes from
    /// the finalizer thread's async context. Only the .NET-allocated buffer is freed.
    /// This is a dev-only limitation (Mono is only used for simulator builds).
    /// </para>
    /// </remarks>
    protected override unsafe bool ReleaseHandle()
    {
        // Early exit for already-freed handles
        if (handle == IntPtr.Zero)
            return true;

        // During process exit, skip Destroy for finalizer-triggered cleanup only.
        // Explicit Dispose() still runs Destroy — Swift deinit may flush/close/persist.
        // On NativeAOT/iOS, GC finalization can start before ProcessExit fires,
        // and the Swift runtime may already be partially torn down.
        // We still free the .NET-allocated buffer since NativeMemory.Free is always safe.
        if (IsProcessExiting && !_explicitDispose)
        {
            NativeMemory.Free((void*)handle);
            handle = IntPtr.Zero;
            return true;
        }

        // Warn when handle is finalized without explicit Dispose on Mono,
        // where the finalizer cannot safely call Destroy.
        if (!_explicitDispose && s_isMonoRuntime && handle != IntPtr.Zero)
        {
            Debug.WriteLine($"[SwiftSafeHandle] WARNING: SwiftSafeHandle<{typeof(T).Name}> " +
                $"(0x{handle:X}) was finalized without Dispose() on Mono. " +
                "Swift ARC reference count was not decremented. " +
                "Use 'using' or call Dispose() explicitly.");
        }

        try
        {
            // Determine if Destroy should run:
            // - Explicit Dispose: always (safe from user thread on both runtimes)
            // - Finalization on NativeAOT: always (VWT Destroy safe from finalizer thread)
            // - Finalization on Mono: skip (jit-info.c:918 async assertion crash)
            bool shouldDestroy = _explicitDispose || !s_isMonoRuntime;

            if (shouldDestroy)
            {
                TypeMetadata metadata = SwiftObjectHelper<T>.GetTypeMetadata();
                if (metadata.IsValid)
                {
                    metadata.ValueWitnessTable->Destroy((void*)handle, metadata);
                }
            }
        }
        catch
        {
            // Swallow exceptions - ReleaseHandle must not throw per SafeHandle contract.
        }

        // Always free the .NET-allocated buffer
        NativeMemory.Free((void*)handle);
        handle = IntPtr.Zero;

        return true;
    }
}

/// <summary>
/// Represents a buffer for a Swift object used for lowering.
/// </summary>
public unsafe ref struct PayloadBuffer<T> : IDisposable where T : unmanaged
{
    private readonly SafeHandle _payload;

    private bool _shouldDispose;

    public T Buffer => *(T*)_payload.DangerousGetHandle();

    /// <summary>
    /// Returns a ref to the buffer value in native memory, allowing in-place modification
    /// via ref parameters (e.g., Swift inout on frozen-with-memory-management types).
    /// </summary>
    public ref T BufferRef => ref *(T*)_payload.DangerousGetHandle();

    public PayloadBuffer(SafeHandle payload)
    {
        _payload = payload;
        _payload.DangerousAddRef(ref _shouldDispose);
    }

    public void Dispose()
    {
        if (_shouldDispose)
        {
            _payload.DangerousRelease();
            _shouldDispose = false;
        }
    }
}

/// <summary>
/// Marker struct used as a sentinel parameter in protected constructors for class inheritance chaining.
/// Generated derived class constructors chain to base(default(SwiftInheritanceChain)) to invoke
/// the base class's protected constructor. This type cannot conflict with any Swift-generated
/// constructor parameters.
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public struct SwiftInheritanceChain { }
