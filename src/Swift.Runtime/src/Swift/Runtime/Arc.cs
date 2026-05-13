// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Arc is a class containing p/invokes for Swift Automatic Reference Counting and memory management.
/// Retain, read, and query P/Invokes use [SuppressGCTransition] because they are leaf calls
/// (atomic increment or flag read) that never call back into managed code.
/// Release P/Invokes do NOT use [SuppressGCTransition] because swift_release can trigger
/// deinit on final release, and deinit code may invoke managed callbacks via closures/@_cdecl.
/// </summary>
public static class Arc
{
    /// <summary>
    /// Retain a heap-allocated Swift object
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    [SuppressGCTransition]
    static extern void swift_retain(IntPtr p);

    /// <summary>
    /// Retain a heap-allocated Swift object
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    /// <returns>The pointer passed in.</returns>
    /// <exception cref="ArgumentNullException">Throws if p is null</exception>
    public static IntPtr Retain(IntPtr p)
    {
        if (p == IntPtr.Zero)
            throw new ArgumentNullException(nameof(p));
        swift_retain(p);
        return p;
    }

    /// <summary>
    /// Check to see if a pointer is in the process of being deallocated.
    /// </summary>
    /// <param name="p">A non-null pointer to an unmanaged Swift object</param>
    /// <returns>True if and only if the pointer in the process of being deallocated.</returns>
    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    [SuppressGCTransition]
    [return: MarshalAs(UnmanagedType.I1)]
    static extern bool swift_isDeallocating(IntPtr p);

    /// <summary>
    /// Check to see if a pointer is in the process of being deallocated.
    /// </summary>
    /// <param name="p">A non-null pointer to an unmanaged Swift object</param>
    /// <returns>True if and only if the pointer in the process of being deallocated.</returns>
    /// <exception cref="ArgumentNullException">Throws if p is null</exception>
    public static bool IsDeallocating(IntPtr p)
    {
        if (p == IntPtr.Zero)
            throw new ArgumentNullException(nameof(p));
        return swift_isDeallocating(p);
    }

    /// <summary>
    /// Releases a heap-allocated Swift object
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    static extern void swift_release(IntPtr p);

    /// <summary>
    /// Releases a heap-allocated Swift object
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    /// <returns>The pointer passed in</returns>
    /// <exception cref="ArgumentNullException">Throws if p is null</exception>
    /// <exception cref="Exception">Throws if p points to an object that has been deinitialized</exception>
    public static IntPtr Release(IntPtr p)
    {
        if (p == IntPtr.Zero)
            throw new ArgumentNullException(nameof(p));
        if (swift_isDeallocating(p))
        {
            throw new Exception($"Attempt to release a Swift object that has been deinitialized {p.ToString($"X{IntPtr.Size * 2}")}");
        }
        swift_release(p);
        return p;
    }

    /// <summary>
    /// Retains an 'unowned' heap-allocated Swift object.
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    [SuppressGCTransition]
    static extern void swift_unownedRetain(IntPtr p);

    /// <summary>
    /// Retains an 'unowned' heap-allocated Swift object.
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    /// <returns>The pointer passed in</returns>
    /// <exception cref="ArgumentNullException">Throws if p is null</exception>
    public static IntPtr UnownedRetain(IntPtr p)
    {
        if (p == IntPtr.Zero)
            throw new ArgumentNullException(nameof(p));
        swift_unownedRetain(p);
        return p;
    }

    /// <summary>
    /// Releases an 'unowned' heap-allocated Swift object.
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    static extern void swift_unownedRelease(IntPtr p);

    /// <summary>
    /// Releases an 'unowned' heap-allocated Swift object.
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    /// <returns>The pointer passed in</returns>
    /// <exception cref="ArgumentNullException">Throws if p is null</exception>
    public static IntPtr UnownedRelease(IntPtr p)
    {
        if (p == IntPtr.Zero)
            throw new ArgumentNullException(nameof(p));
        swift_unownedRelease(p);
        return p;
    }

    /// <summary>
    /// Retains every pointer in <paramref name="pointers"/> in a single managed→native
    /// transition. The buffer is pinned and forwarded to the Swift companion's
    /// <c>SBW_BulkRetain</c> helper, which walks it in-process. Cuts ARC overhead on
    /// large collection transfers from O(N) P/Invokes to one.
    /// </summary>
    /// <param name="pointers">Span of non-null pointers to unmanaged Swift objects.</param>
    /// <exception cref="ArgumentException">Throws if any pointer is null.</exception>
    public static unsafe void RetainMultiple(ReadOnlySpan<IntPtr> pointers)
    {
        if (pointers.IsEmpty) return;
        // Pre-validate all pointers before calling into native code so a null entry
        // surfaces as a managed exception, not a Swift-side crash.
        for (int i = 0; i < pointers.Length; i++)
        {
            if (pointers[i] == IntPtr.Zero)
                throw new ArgumentException($"Pointer at index {i} is null.", nameof(pointers));
        }
        fixed (IntPtr* p = pointers)
        {
            BulkArc.SBW_BulkRetain((IntPtr)p, pointers.Length);
        }
    }

    /// <summary>
    /// Releases every pointer in <paramref name="pointers"/> in a single managed→native
    /// transition via the Swift companion's <c>SBW_BulkRelease</c>. Deinit on final
    /// release can re-enter managed code through closures or <c>@_cdecl</c> callbacks,
    /// so the call is not <c>[SuppressGCTransition]</c>.
    /// </summary>
    /// <param name="pointers">Span of non-null pointers to unmanaged Swift objects.</param>
    /// <exception cref="ArgumentException">Throws if any pointer is null.</exception>
    /// <exception cref="Exception">Throws if any pointer points to an object being deinitialized.</exception>
    public static unsafe void ReleaseMultiple(ReadOnlySpan<IntPtr> pointers)
    {
        if (pointers.IsEmpty) return;
        for (int i = 0; i < pointers.Length; i++)
        {
            if (pointers[i] == IntPtr.Zero)
                throw new ArgumentException($"Pointer at index {i} is null.", nameof(pointers));
            if (swift_isDeallocating(pointers[i]))
                throw new Exception($"Attempt to release a Swift object at index {i} that has been deinitialized {pointers[i].ToString($"X{IntPtr.Size * 2}")}");
        }
        fixed (IntPtr* p = pointers)
        {
            BulkArc.SBW_BulkRelease((IntPtr)p, pointers.Length);
        }
    }

    /// <summary>
    /// Returns the retain count for the heap-allocated Swift object.
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    /// <returns>The retain count</returns>
    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    [SuppressGCTransition]
    static extern nint swift_retainCount(IntPtr p);

    /// <summary>
    /// Returns the retain count for the heap-allocated Swift object
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    /// <returns>The retain count</returns>
    /// <exception cref="ArgumentNullException">Throws if p is null</exception>
    public static nint RetainCount(IntPtr p)
    {
        if (p == IntPtr.Zero)
            throw new ArgumentNullException(nameof(p));
        return swift_retainCount(p);
    }

    /// <summary>
    /// Returns the 'unowned' retain count for the heap-allocated Swift object.
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    /// <returns>The unowned retain count</returns>
    [DllImport(KnownLibraries.SwiftCore, CallingConvention = CallingConvention.Cdecl)]
    [SuppressGCTransition]
    static extern nint swift_unownedRetainCount(IntPtr p);

    /// <summary>
    /// Returns the 'unowned' retain count for the heap-allocated Swift object.
    /// </summary>
    /// <param name="p">Pointer to an unmanaged Swift object, must be non-null.</param>
    /// <returns>The unowned retain count</returns>
    /// <exception cref="ArgumentNullException">Throws if p is null</exception>
    public static nint UnownedRetainCount(IntPtr p)
    {
        if (p == IntPtr.Zero)
            throw new ArgumentNullException(nameof(p));
        return swift_unownedRetainCount(p);
    }
}

/// <summary>
/// Non-generic finalizer-safe trampoline for releasing Swift class references.
/// Mirrors the <c>VwtDestroyTrampoline</c> pattern used by <c>SwiftSafeHandle</c>.
///
/// <para>Why this exists:</para>
/// <para>
/// <c>SwiftClassHandle&lt;T&gt;.ReleaseHandle</c> runs on the GC finalizer thread.
/// Calling <c>swift_release</c> directly from C# (whether via <c>Arc.Release</c>
/// or via a direct <c>[DllImport(libswiftCore)]</c> in a non-generic helper)
/// crashes Mono with the <c>jit-info.c:918 `!ji->async'</c> assertion after
/// CallConvSwift JIT state contamination — observed empirically on the iOS
/// Simulator in <c>EnumMarshallingTests</c> and <c>ExistentialBoxingTests</c>,
/// and reproducible from any test class that uses CallConvSwift heavily.
/// The crash fires inside the P/Invoke marshalling stub itself, even when the
/// C# side has no managed body.
/// </para>
/// <para>
/// The fix is to route the call through a Swift <c>@_cdecl</c> wrapper
/// (<c>SBW_SwiftRelease</c>) we control: the C# side only crosses one Cdecl
/// boundary into our own loaded <c>SwiftBindingsRuntime.dylib</c>, and the
/// Swift wrapper performs the actual <c>swift_release</c> call from inside
/// Swift where Mono's JIT contamination has no effect. This is the exact same
/// trick <c>SBW_VWTDestroy</c> uses for Swift struct VWT destruction on the
/// finalizer thread, and the only path empirically known to be finalizer-safe
/// on Mono.
/// </para>
/// <para>
/// The <c>swift_isDeallocating</c> defensive check from <c>Arc.Release</c> is
/// intentionally skipped here: SafeHandle guarantees ReleaseHandle runs at most
/// once per handle, so a double-release cannot happen on this path.
/// </para>
/// </summary>
internal static class SwiftReleaseTrampoline
{
    [DllImport("SwiftBindingsRuntime", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_SwiftRelease")]
    internal static extern void Release(IntPtr p);

    /// <summary>
    /// Finalizer-safe release for arbitrary Swift heap objects that are NOT
    /// <c>AnyObject</c>-castable — escaping closure contexts in particular.
    /// Routes through the <c>SBW_SwiftReleaseRaw</c> companion wrapper, which
    /// calls <c>swift_release</c> directly without going through
    /// <c>Unmanaged&lt;AnyObject&gt;</c>. The <see cref="Release"/> entry point's
    /// <c>AnyObject</c> cast segfaults inside <c>_objc_msgSend_uncached</c> for
    /// closure contexts because they have no ObjC <c>isa</c> metadata.
    /// </summary>
    [DllImport("SwiftBindingsRuntime", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_SwiftReleaseRaw")]
    internal static extern void ReleaseRaw(IntPtr p);

    /// <summary>
    /// Best-effort release for use from a finalizer thread, swallowing every
    /// exception path (DllNotFoundException for older bundled dylibs, native
    /// faults). Lives on a non-generic static class so the try/catch IL is not
    /// instantiated per closing-over-T finalizer, which trips Mono AOT shutdown
    /// loading new generic instantiations from inside the GC finalizer thread.
    /// </summary>
    internal static void SafeReleaseRawForFinalizer(IntPtr p)
    {
        try
        {
            ReleaseRaw(p);
        }
        catch
        {
        }
    }
}

/// <summary>
/// Bulk Swift ARC helpers implemented in the Swift companion. These accept a pinned
/// buffer of class-instance pointers plus a count and walk the buffer in Swift, so
/// the C# caller crosses one managed→native boundary instead of N. Used by
/// <see cref="Arc.RetainMultiple"/> and <see cref="Arc.ReleaseMultiple"/>; collection
/// runtime helpers should reach these via Arc, not directly.
/// </summary>
internal static class BulkArc
{
    // count is `nint` to match Swift's `Int` (64-bit on arm64). Declaring this as
    // C# `int` leaves the upper 32 bits of the count register undefined, which
    // causes the Swift loop to iterate billions of times and SIGSEGV.
    [DllImport("SwiftBindingsRuntime", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_BulkRetain")]
    internal static extern void SBW_BulkRetain(IntPtr pointers, nint count);

    [DllImport("SwiftBindingsRuntime", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SBW_BulkRelease")]
    internal static extern void SBW_BulkRelease(IntPtr pointers, nint count);
}
