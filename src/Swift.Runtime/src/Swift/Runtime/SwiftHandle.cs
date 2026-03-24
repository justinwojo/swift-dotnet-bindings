// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
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
    /// since VWT Destroy via the Cdecl trampoline is safe on both Mono and NativeAOT.
    /// The registered action is ignored — VWT Destroy is always used directly.
    /// </summary>
    /// <param name="action">The destroy action (ignored).</param>
    public static void RegisterDestroyAction(Action<IntPtr>? action)
    {
        // No-op for backward compatibility. Previously-generated bindings may call this
        // during static initialization, but the action is not stored or used.
    }

    /// <summary>
    /// Tracks whether Dispose() was explicitly called.
    /// Used to distinguish explicit disposal from finalizer-triggered cleanup during
    /// process exit: explicit Dispose still runs VWT Destroy (Swift deinit may have
    /// side effects), while finalizer-triggered cleanup skips it (runtime may be torn down).
    /// </summary>
    private volatile bool _explicitDispose;

    /// <summary>
    /// Cached type metadata handle for the Swift type T. Populated eagerly during
    /// construction on a user thread so that the finalizer path can call VWT Destroy
    /// via the Cdecl trampoline without any JIT compilation or generic resolution.
    /// IntPtr.Zero if metadata was not available (e.g., zero handle or mock types in tests).
    /// </summary>
    private readonly IntPtr _metadataHandle;

    /// <summary>
    /// Constructs a SwiftSafeHandle from the given IntPtr
    /// </summary>
    public SwiftSafeHandle(IntPtr handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);

        // Cache metadata eagerly on user thread so the finalizer can use it without JIT.
        // Skip for zero handles (e.g., SwiftSafeHandle<T>.Zero static field) to avoid
        // triggering type metadata resolution during static initialization.
        if (handle != IntPtr.Zero)
        {
            try
            {
                var metadata = SwiftObjectHelper<T>.GetTypeMetadata();
                _metadataHandle = metadata.IsValid ? metadata.Handle : IntPtr.Zero;
            }
            catch
            {
                // Metadata not available (e.g., mock types in unit tests).
                // Finalizer will skip VWT Destroy but still free the buffer.
                _metadataHandle = IntPtr.Zero;
            }
        }
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
    /// VWT Destroy is called directly to decrement reference counts, then the
    /// .NET buffer is freed. Suppresses finalization since cleanup is handled.
    /// </summary>
    /// <remarks>
    /// Failing to call Dispose() will cause the finalizer to run. The finalizer
    /// calls VWT Destroy via a Cdecl trampoline (<c>SBW_VWTDestroy</c>), providing
    /// identical cleanup on both Mono (simulator) and NativeAOT (device). Disposal
    /// is never required for correctness — use <c>using</c> for deterministic cleanup
    /// of scarce resources.
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
    /// During explicit disposal: calls VWT Destroy directly via the cached VWT function
    /// pointer. This runs on a user thread where JIT compilation is always safe.
    /// </para>
    /// <para>
    /// During finalization: calls VWT Destroy via <c>SBW_VWTDestroy</c>, a <c>@_cdecl</c>
    /// function in <c>SwiftBindingsRuntime.dylib</c> called with
    /// <c>CallingConvention.Cdecl</c>. DllImport stubs are resolved by the runtime loader
    /// (not JIT-compiled), making this safe from the GC finalizer thread on all runtimes
    /// including Mono (which crashes on JIT from the finalizer when CallConvSwift
    /// compilations have contaminated JIT state).
    /// </para>
    /// <para>
    /// During process exit (finalizer only): skips VWT Destroy because Swift deinit
    /// may reference torn-down runtime state. Explicit Dispose() during process exit
    /// still calls VWT Destroy — Swift deinit may flush/close/persist.
    /// </para>
    /// </remarks>
    protected override unsafe bool ReleaseHandle()
    {
        // Early exit for already-freed handles
        if (handle == IntPtr.Zero)
            return true;

        // Process exit finalizer → free buffer only (Swift runtime may be torn down)
        if (IsProcessExiting && !_explicitDispose)
            return HandleProcessExitCleanup();

        // Explicit Dispose → direct VWT Destroy (safe from user thread)
        if (_explicitDispose)
            return HandleNormalRelease();

        // GC Finalizer → Cdecl trampoline (safe on both Mono and NativeAOT)
        return HandleFinalizerRelease();
    }

    /// <summary>
    /// Handles cleanup during process exit for finalizer-triggered releases.
    /// Skips VWT Destroy because Swift deinit may reference torn-down runtime state,
    /// but still frees the .NET-allocated buffer since NativeMemory.Free is always safe.
    /// Explicit Dispose() bypasses this path — Swift deinit may flush/close/persist.
    /// </summary>
    private unsafe bool HandleProcessExitCleanup()
    {
        NativeMemory.Free((void*)handle);
        handle = IntPtr.Zero;
        return true;
    }

    /// <summary>
    /// Handles the normal release path for explicit Dispose: calls VWT Destroy directly
    /// to decrement Swift ARC reference counts, then frees the .NET-allocated buffer.
    /// Always runs on a user thread where JIT compilation is safe.
    /// </summary>
    private unsafe bool HandleNormalRelease()
    {
        try
        {
            // VWT Destroy is in a separate NoInlining method so that Mono's JIT
            // does not eagerly compile SwiftObjectHelper<T>.GetTypeMetadata() when
            // compiling ReleaseHandle(). On user threads, this JIT is always safe.
            PerformVwtDestroy(handle);
        }
        catch
        {
            // Swallow exceptions - ReleaseHandle must not throw per SafeHandle contract.
        }

        // Free the .NET-allocated buffer
        NativeMemory.Free((void*)handle);
        handle = IntPtr.Zero;

        return true;
    }

    /// <summary>
    /// Handles GC finalizer release: calls VWT Destroy via Cdecl trampoline
    /// (<c>SBW_VWTDestroy</c> in SwiftBindingsRuntime.dylib) using metadata cached
    /// at construction time. DllImport stubs are resolved by the runtime loader —
    /// no JIT compilation needed — making this safe from the finalizer thread on
    /// both Mono and NativeAOT.
    /// </summary>
    private unsafe bool HandleFinalizerRelease()
    {
        try
        {
            // Metadata was cached at construction time (on a user thread).
            // The Cdecl trampoline is a [DllImport] resolved by the runtime loader —
            // no JIT compilation, no generic resolution, safe from the finalizer thread.
            if (_metadataHandle != IntPtr.Zero)
                VwtDestroyTrampoline.Destroy(handle, _metadataHandle);
        }
        catch
        {
            // Swallow exceptions - ReleaseHandle must not throw per SafeHandle contract.
            // DllNotFoundException if SwiftBindingsRuntime.dylib is not loaded (e.g., unit tests).
        }

        // Free the .NET-allocated buffer
        NativeMemory.Free((void*)handle);
        handle = IntPtr.Zero;

        return true;
    }

    /// <summary>
    /// Calls VWT Destroy to properly decrement Swift ARC reference counts.
    /// Extracted into a separate NoInlining method so that Mono's JIT does not
    /// attempt to compile <see cref="SwiftObjectHelper{T}.GetTypeMetadata()"/> when
    /// compiling <see cref="ReleaseHandle"/> on the finalizer thread.
    /// Only called from the explicit dispose path (user thread).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private unsafe void PerformVwtDestroy(IntPtr handle)
    {
        TypeMetadata metadata = SwiftObjectHelper<T>.GetTypeMetadata();
        if (metadata.IsValid)
        {
            metadata.ValueWitnessTable->Destroy((void*)handle, metadata);
        }
    }
}

/// <summary>
/// Non-generic helper for the VWT Destroy Cdecl trampoline.
/// <c>[DllImport]</c> cannot be applied to methods in generic types (CS7042),
/// so the P/Invoke declaration lives here and is called from <see cref="SwiftSafeHandle{T}"/>.
/// </summary>
internal static class VwtDestroyTrampoline
{
    /// <summary>
    /// Calls VWT Destroy on the Swift side via a <c>@_cdecl</c> function in
    /// <c>SwiftBindingsRuntime.dylib</c>. The function reads VWT Destroy from
    /// <c>metadata[-1]</c> (the VWT pointer, ABI-stable since Swift 5.0) and
    /// calls it with the value pointer and metadata.
    /// </summary>
    /// <param name="ptr">Pointer to the Swift value buffer to destroy.</param>
    /// <param name="metadata">Swift type metadata pointer for the value's type.</param>
    [DllImport("SwiftBindingsRuntime", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_VWTDestroy")]
    internal static extern void Destroy(IntPtr ptr, IntPtr metadata);
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
