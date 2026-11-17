// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

#nullable enable

namespace Swift.Runtime;

/// <summary>
/// Reads the native pointer of a Swift object for a call into Swift, refusing a disposed one.
/// </summary>
/// <remarks>
/// A disposed class wrapper's handle reads <see cref="IntPtr.Zero"/>. Handing that to Swift as
/// <c>self</c> or as a non-optional object argument dereferences null, and handing it as an
/// optional argument silently turns a disposed object into <c>nil</c>. Generated code reads the
/// pointer through these helpers wherever it does not already hold the handle open with
/// <see cref="SafeHandle.DangerousAddRef"/>, so a disposed object throws
/// <see cref="ObjectDisposedException"/> before any native code runs.
/// </remarks>
public static class SwiftHandleGuard
{
    /// <summary>
    /// Returns the pointer a live <paramref name="handle"/> holds.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The handle is closed or holds no pointer.</exception>
    public static IntPtr GetLiveHandle(SafeHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.IsClosed || handle.IsInvalid)
            throw new ObjectDisposedException(handle.GetType().FullName);
        return handle.DangerousGetHandle();
    }

    /// <summary>
    /// Returns the Swift pointer of a live <paramref name="value"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    public static IntPtr GetLiveHandle(ISwiftObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var pointer = value.SwiftHandle;
        if (pointer == IntPtr.Zero)
            throw new ObjectDisposedException(value.GetType().FullName);
        return pointer;
    }

    /// <summary>
    /// Returns the Swift pointer of a non-optional argument.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    public static IntPtr GetLiveArgumentHandle(ISwiftObject? value, string parameterName)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
        return GetLiveHandle(value);
    }

    /// <summary>
    /// Returns the Swift pointer of an optional argument: <see cref="IntPtr.Zero"/> (Swift
    /// <c>nil</c>) for null, the object's pointer when it is live.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    public static IntPtr GetLiveHandleOrZero(ISwiftObject? value)
        => value is null ? IntPtr.Zero : GetLiveHandle(value);

#if IOS || TVOS || MACCATALYST || MACOS
    /// <summary>
    /// Returns the Objective-C pointer of a live <paramref name="value"/>. Named apart from
    /// <see cref="GetLiveHandle(ISwiftObject)"/> because an NSObject-rooted Swift class is both.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    public static IntPtr GetLiveObjCHandle(ObjCRuntime.INativeObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        IntPtr pointer = value.Handle;
        if (pointer == IntPtr.Zero)
            throw new ObjectDisposedException(value.GetType().FullName);
        return pointer;
    }

    /// <summary>
    /// Returns the Objective-C pointer of a non-optional argument.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    public static IntPtr GetLiveObjCArgumentHandle(ObjCRuntime.INativeObject? value, string parameterName)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
        return GetLiveObjCHandle(value);
    }

    /// <summary>
    /// Returns the Objective-C pointer of an optional argument: <see cref="IntPtr.Zero"/>
    /// (Swift <c>nil</c>) for null, the object's pointer when it is live.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The object has been disposed.</exception>
    public static IntPtr GetLiveObjCHandleOrZero(ObjCRuntime.INativeObject? value)
        => value is null ? IntPtr.Zero : GetLiveObjCHandle(value);
#endif
}
