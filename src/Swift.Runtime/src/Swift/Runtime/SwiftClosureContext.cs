// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Closure-context owner-token bridge: pairs Swift's <c>_SBClosureCtx</c> reference-
/// counted box (defined in the SwiftBindingsRuntime native framework) with the C# free
/// callback that releases the wrapped <see cref="GCHandle"/> when the box deinits.
/// </summary>
/// <remarks>
/// <para>
/// Each escaping Swift closure that captures a managed delegate previously leaked the
/// <see cref="GCHandle"/> rooting the delegate — the C# wrapper had no way to know
/// when Swift released the closure. The owner-token model wraps the captured
/// <see cref="GCHandle"/> pointer in a Swift class whose <c>deinit</c> upcalls the
/// trampoline registered here, freeing the handle exactly once.
/// </para>
/// <para>
/// Restricted to <c>@escaping</c> closures: non-escaping closures still free in the
/// C# wrapper's <c>finally</c> block (the original Cat 1 path), where Swift cannot
/// retain the closure past the call.
/// </para>
/// </remarks>
internal static class SwiftClosureContext
{
    private static volatile bool s_registered;
    private static readonly object s_lock = new();

    // Dylib-presence latch. Initialized in <see cref="EnsureRegistered"/> before
    // any closure trampoline can fire (ModuleInitializer runs before user code),
    // so the legacy SwiftClosureData trampoline's <see cref="GetCtx"/> call sees
    // a definite state. 0 = unknown (default), 1 = present (unbox via P/Invoke),
    // -1 = absent (passthrough — context is the raw GCHandle pointer).
    private static volatile int s_dylibState;

    /// <summary>
    /// Registers the destroy trampoline with the runtime dylib. Idempotent — safe
    /// to call multiple times. Called once from
    /// <see cref="SwiftFrameworkResolver.InitializeRuntime"/>.
    /// </summary>
    internal static unsafe void EnsureRegistered()
    {
        if (s_registered) return;
        lock (s_lock)
        {
            if (s_registered) return;

            try
            {
                NativeMethods.SwiftBindings_SetClosureContextDestroyCallback(
                    &DestroyClosureContext);
                s_registered = true;
                s_dylibState = 1;
            }
            catch (DllNotFoundException)
            {
                // The SwiftBindingsRuntime native framework not packaged (e.g.
                // IncludeSwiftBindingsRuntimeNative=false). Falling back to
                // the prior leak behaviour is acceptable for those builds —
                // the closure-context owner token is opt-in via the runtime.
                s_dylibState = -1;
            }
            catch (EntryPointNotFoundException)
            {
                // Older runtime dylib without the closure-context callback
                // symbols. Same fallback as missing dylib.
                s_dylibState = -1;
            }
        }
    }

    /// <summary>
    /// Destroy trampoline fired by Swift's <c>_SBClosureCtx.deinit</c>. Receives the
    /// opaque pointer originally produced by <c>GCHandle.ToIntPtr</c> and frees
    /// the handle. The Swift box guarantees this fires exactly once per allocated
    /// context.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static void DestroyClosureContext(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero) return;
        var handle = GCHandle.FromIntPtr(ctx);
        if (handle.IsAllocated)
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Allocates a <c>_SBClosureCtx</c> box wrapping the supplied <see cref="GCHandle"/>
    /// pointer and returns a +1-retained opaque pointer. Returns
    /// <see cref="IntPtr.Zero"/> when the runtime dylib is absent
    /// (<c>IncludeSwiftBindingsRuntimeNative=false</c>), letting the caller fall back
    /// to the prior raw-handle leak behaviour.
    /// </summary>
    internal static IntPtr TryAllocateBox(IntPtr ctx)
    {
        if (ctx == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            return NativeMethods.SwiftBindings_NewClosureContext(ctx);
        }
        catch (DllNotFoundException)
        {
            return IntPtr.Zero;
        }
        catch (EntryPointNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Releases a box pointer returned by <see cref="TryAllocateBox"/>. The Swift
    /// <c>_SBClosureCtx.deinit</c> then fires <see cref="DestroyClosureContext"/>,
    /// freeing the wrapped <see cref="GCHandle"/> exactly once.
    /// </summary>
    internal static void ReleaseBox(IntPtr boxPtr)
    {
        if (boxPtr == IntPtr.Zero) return;
        SwiftReleaseTrampoline.ReleaseRaw(boxPtr);
    }

    /// <summary>
    /// Returns the original <see cref="GCHandle"/> pointer from a closure context
    /// slot that may hold either a raw <see cref="GCHandle"/> pointer (dylib
    /// absent) or an <c>_SBClosureCtx</c> box pointer (dylib present). The
    /// dylib-presence latch is set eagerly in <see cref="EnsureRegistered"/> so
    /// trampolines see a definite state by the time they fire.
    /// </summary>
    /// <remarks>
    /// Used by trampolines on the legacy <c>SwiftClosureData</c> escaping path —
    /// the cdecl path receives the raw <see cref="GCHandle"/> pointer directly
    /// (the Swift wrapper unboxes before invoking the C# callback) and must NOT
    /// call this helper.
    /// </remarks>
    internal static IntPtr GetCtx(IntPtr maybeBoxedCtx)
    {
        if (maybeBoxedCtx == IntPtr.Zero) return IntPtr.Zero;
        if (s_dylibState <= 0) return maybeBoxedCtx;
        return NativeMethods.SBW_UnboxClosureContext(maybeBoxedCtx);
    }

    private static class NativeMethods
    {
        private const string LibraryName = "SwiftBindingsRuntime";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void SwiftBindings_SetClosureContextDestroyCallback(
            delegate* unmanaged[Cdecl]<IntPtr, void> callback);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SwiftBindings_NewClosureContext(IntPtr ctx);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr SBW_UnboxClosureContext(IntPtr box);
    }
}
