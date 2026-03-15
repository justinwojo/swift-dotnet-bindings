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
    /// Per-type destroy action registered by generated bindings.
    /// When set, ReleaseHandle calls this instead of ValueWitnessTable->Destroy,
    /// routing through a @_cdecl wrapper that avoids the CallConvSwift crash on NativeAOT.
    /// Static per generic instantiation (each T gets its own field).
    /// </summary>
    private static Action<IntPtr>? s_destroyAction;

    /// <summary>
    /// Registers a custom destroy action for this SafeHandle type parameter.
    /// Called from generated binding code to route Dispose() through a @_cdecl
    /// Swift wrapper instead of the ValueWitnessTable function pointer.
    /// </summary>
    /// <param name="action">The destroy action that calls the @_cdecl wrapper P/Invoke.</param>
    public static void RegisterDestroyAction(Action<IntPtr> action)
    {
        s_destroyAction = action;
    }

    /// <summary>
    /// Cached Mono runtime detection for finalizer safety decisions.
    /// On Mono, the VWT Destroy path can trigger jit-info.c:918 crashes from the finalizer
    /// thread. The @_cdecl destroy action uses CallingConvention.Cdecl which avoids this,
    /// but may still trigger other Mono finalizer issues with Swift runtime calls.
    /// On NativeAOT (production), both paths are safe from the finalizer thread.
    /// </summary>
    private static readonly bool s_isMonoRuntime = Type.GetType("Mono.Runtime") != null;

    /// <summary>
    /// Tracks whether Dispose() was explicitly called.
    /// If true, we're in explicit disposal and should call Destroy via any path.
    /// If false when ReleaseHandle runs, we're in finalization — the @_cdecl destroy
    /// action (Cdecl-safe) is called on NativeAOT; on Mono, only the buffer is freed.
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
    /// During explicit disposal, Swift's Destroy is called to decrement reference counts.
    /// Also suppresses finalization since cleanup is already handled.
    /// </summary>
    /// <remarks>
    /// Failing to call Dispose() will cause the finalizer to run. On NativeAOT (production),
    /// the @_cdecl destroy action is called from the finalizer via SafeHandle, providing the same
    /// cleanup as explicit Dispose. On Mono (simulator), the finalizer only frees the buffer
    /// without calling Destroy — use 'using' or call Dispose() explicitly in dev builds.
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
    /// During explicit disposal: calls the @_cdecl destroy action or VWT Destroy to
    /// properly decrement reference counts, then frees the buffer.
    /// </para>
    /// <para>
    /// During finalization on NativeAOT: if a @_cdecl destroy action is registered
    /// (CallingConvention.Cdecl — safe from the finalizer thread), calls it to provide
    /// the same safety net as explicit Dispose. This makes struct lifecycle match class
    /// lifecycle on NativeAOT: the finalizer is a reliable safety net.
    /// </para>
    /// <para>
    /// During finalization on Mono: skips Destroy (both @_cdecl and VWT) to avoid
    /// potential crashes from calling into the Swift runtime from Mono's finalizer thread.
    /// Only the .NET-allocated buffer is freed. This is a dev-only limitation (Mono is
    /// only used for simulator builds).
    /// </para>
    /// </remarks>
    protected override unsafe bool ReleaseHandle()
    {
        // Early exit for already-freed handles
        if (handle == IntPtr.Zero)
            return true;

        // During process exit, skip Destroy for finalizer-triggered cleanup.
        // Swift deinitializers can crash if the Swift runtime is partially torn down.
        // Explicit Dispose() always cleans up regardless of process state.
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
            var destroyAction = s_destroyAction;
            if (_explicitDispose)
            {
                // Explicit Dispose: always call Destroy (safe — we're on a user thread).
                if (destroyAction != null)
                {
                    // Use the @_cdecl wrapper (avoids CallConvSwift crash on NativeAOT).
                    // Generated bindings register this via RegisterDestroyAction.
                    destroyAction(handle);
                }
                else
                {
                    // Fallback to VWT Destroy (works on Mono/JIT, may crash on NativeAOT
                    // for types with non-trivial fields due to CallConvSwift indirect call).
                    TypeMetadata metadata = SwiftObjectHelper<T>.GetTypeMetadata();
                    if (metadata.IsValid)
                    {
                        metadata.ValueWitnessTable->Destroy((void*)handle, metadata);
                    }
                }
            }
            else if (destroyAction != null && !s_isMonoRuntime)
            {
                // Finalization on NativeAOT with @_cdecl destroy action registered:
                // The @_cdecl wrapper uses CallingConvention.Cdecl (NOT CallConvSwift),
                // which is safe from the GC finalizer thread. This provides the same
                // cleanup as explicit Dispose — struct lifecycle matches class lifecycle.
                destroyAction(handle);
            }
            // Finalization on Mono or no destroy action: skip Destroy.
            // On Mono, calling into Swift runtime from the finalizer thread is unsafe.
            // Without a registered destroy action, VWT Destroy may use CallConvSwift.
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
