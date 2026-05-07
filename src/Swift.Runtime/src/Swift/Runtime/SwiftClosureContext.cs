// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Closure-context owner-token bridge: pairs Swift's <c>_SBClosureCtx</c> reference-
/// counted box (defined in <c>libSwiftBindingsRuntime.dylib</c>) with the C# free
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
            }
            catch (DllNotFoundException)
            {
                // libSwiftBindingsRuntime.dylib not packaged (e.g.
                // IncludeSwiftBindingsRuntimeNative=false). Falling back to
                // the prior leak behaviour is acceptable for those builds —
                // the closure-context owner token is opt-in via the runtime.
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

    private static class NativeMethods
    {
        private const string LibraryName = "SwiftBindingsRuntime";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void SwiftBindings_SetClosureContextDestroyCallback(
            delegate* unmanaged[Cdecl]<IntPtr, void> callback);
    }
}
