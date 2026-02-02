// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
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
public sealed class SwiftSafeHandle<T> : SafeHandleZeroOrMinusOneIsInvalid where T : ISwiftObject
{
    /// <summary>
    /// Returns a SwiftSafeHandle with a zero value
    /// </summary>
    public readonly static SwiftSafeHandle<T> Zero = new SwiftSafeHandle<T>(IntPtr.Zero);

    /// <summary>
    /// Tracks whether Dispose() was explicitly called.
    /// If true, we're in explicit disposal and should call Destroy.
    /// If false when ReleaseHandle runs, we're in finalization and should skip Destroy.
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
    /// Disposes the handle. Call this to properly clean up Swift resources.
    /// During explicit disposal, Swift's Destroy is called to decrement reference counts.
    /// </summary>
    public new void Dispose()
    {
        _explicitDispose = true;
        base.Dispose();
    }

    /// <summary>
    /// Releases the handle to the Swift object.
    /// This method must not throw exceptions per the SafeHandle contract.
    /// </summary>
    /// <remarks>
    /// During finalization (when Dispose wasn't explicitly called), calling Swift's Destroy
    /// can crash if the Swift runtime is shutting down. In that case, we only free the buffer.
    /// During explicit disposal, we call Destroy to properly decrement reference counts.
    /// </remarks>
    protected override unsafe bool ReleaseHandle()
    {
        // Early exit for already-freed handles
        if (handle == IntPtr.Zero)
            return true;

        // Only call Destroy during explicit disposal, not during finalization.
        // During finalization/shutdown, the Swift runtime may be in an inconsistent state
        // causing native crashes that C# try-catch cannot handle.
        if (_explicitDispose)
        {
            try
            {
                TypeMetadata metadata = SwiftObjectHelper<T>.GetTypeMetadata();
                if (metadata.IsValid)
                {
                    metadata.ValueWitnessTable->Destroy((void*)handle, metadata);
                }
            }
            catch
            {
                // Swallow exceptions - ReleaseHandle must not throw per SafeHandle contract.
            }
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
