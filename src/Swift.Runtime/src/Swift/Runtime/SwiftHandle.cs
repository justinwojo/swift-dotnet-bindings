// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.ComponentModel;
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
[EditorBrowsable(EditorBrowsableState.Never)]
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
    /// Tracks whether Dispose() was explicitly called.
    /// Used to distinguish explicit disposal from finalizer-triggered cleanup during
    /// process exit: explicit Dispose still runs VWT Destroy (Swift deinit may have
    /// side effects), while finalizer-triggered cleanup skips it (runtime may be torn down).
    /// </summary>
    private volatile bool _explicitDispose;

    /// <summary>
    /// Set by <see cref="MarkConsumed"/> when the underlying value has been moved out by a Swift
    /// <c>consuming</c> parameter. When true, <see cref="ReleaseHandle"/> frees the .NET buffer but
    /// skips the value-witness Destroy — Swift already ran the value's deinit exactly once, so a
    /// second Destroy would double-free.
    /// </summary>
    private volatile bool _consumed;

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
    /// Marks the underlying value as having been consumed (moved out) by a Swift <c>consuming</c>
    /// parameter of a non-copyable type. Ownership transferred into Swift, which runs the value's
    /// deinit exactly once, so the value-witness Destroy must NOT run again; the .NET-allocated
    /// buffer is still freed by <see cref="ReleaseHandle"/> (or eagerly here is avoided — the
    /// SafeHandle owns the free so it happens exactly once on Dispose/finalize). Idempotent.
    /// </summary>
    /// <remarks>
    /// Generated bindings call this immediately after a P/Invoke that passes this handle to a Swift
    /// <c>consuming</c> non-copyable parameter (see CdeclParamMapper's <c>.move()</c> path). Without
    /// it, Swift's consume plus the SafeHandle's Destroy would double-free the value.
    /// </remarks>
    public void MarkConsumed()
    {
        _consumed = true;
    }

    /// <summary>
    /// True once <see cref="MarkConsumed"/> has run — i.e. the underlying value was moved out by a
    /// Swift <c>consuming</c> self/parameter and no longer exists in the buffer. Generated bindings
    /// read this to fail fast (<see cref="System.ObjectDisposedException"/>) when a caller reuses a
    /// receiver whose value was already consumed, instead of passing the moved-out buffer back into
    /// Swift (use-after-move). Swift forbids post-consume use at compile time; the .NET class
    /// projection has no move checker, so the guard is the equivalent runtime contract.
    /// </summary>
    public bool IsConsumed => _consumed;

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
    /// function in the SwiftBindingsRuntime native framework called with
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

        // Value moved out by a Swift `consuming` parameter: Swift already ran the value's
        // deinit exactly once, so skip the value-witness Destroy and free the buffer only. Must
        // precede the Destroy paths below. Checked on both Dispose and finalizer.
        if (_consumed)
            return FreeBufferOnly();

        // Process exit finalizer → free buffer only (Swift runtime may be torn down)
        if (IsProcessExiting && !_explicitDispose)
            return FreeBufferOnly();

        // Explicit Dispose → direct VWT Destroy (safe from user thread)
        if (_explicitDispose)
            return HandleNormalRelease();

        // GC Finalizer → Cdecl trampoline (safe on both Mono and NativeAOT)
        return HandleFinalizerRelease();
    }

    /// <summary>
    /// Frees the .NET-allocated buffer WITHOUT running the value-witness Destroy. Shared by two
    /// paths: process-exit finalizer cleanup (Swift deinit may reference torn-down runtime state)
    /// and consumed-value cleanup (Swift's <c>consuming</c> parameter already ran deinit exactly
    /// once — see <see cref="MarkConsumed"/>). NativeMemory.Free is always safe.
    /// </summary>
    private unsafe bool FreeBufferOnly()
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
    /// (<c>SBW_VWTDestroy</c> in the SwiftBindingsRuntime native framework) using metadata cached
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
            // DllNotFoundException if the SwiftBindingsRuntime native framework is not loaded (e.g., unit tests).
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
    /// the SwiftBindingsRuntime native framework. The function reads VWT Destroy from
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
/// Pins a SafeHandle's reference count for the lifetime of the struct.
/// Generated bindings use this to hold a SafeHandle open across a P/Invoke when only the
/// raw pointer (DangerousGetHandle) is needed — without it, GC finalization between the
/// handle access and the native call can free the underlying Swift heap payload.
///
/// Use the `using` statement so DangerousRelease runs on every exit (including exception
/// unwinds). The constructor calls <see cref="SafeHandle.DangerousAddRef(ref bool)"/>;
/// if the handle is closed, that throws <see cref="ObjectDisposedException"/>, which
/// surfaces to the caller as a faulted invocation (correct: a disposed receiver cannot
/// back the in-flight call).
/// </summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public ref struct SafeHandlePin : IDisposable
{
    private readonly SafeHandle _handle;
    private bool _addedRef;

    /// <summary>The pinned SafeHandle's raw pointer.</summary>
    public IntPtr Handle => _handle.DangerousGetHandle();

    public SafeHandlePin(SafeHandle handle)
    {
        _handle = handle;
        handle.DangerousAddRef(ref _addedRef);
    }

    public void Dispose()
    {
        if (_addedRef)
        {
            _handle.DangerousRelease();
            _addedRef = false;
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
