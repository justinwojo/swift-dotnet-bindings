// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime
{
    /// <summary>
    /// Provides centralized finalizer cleanup for generated Swift binding types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For structs (<see cref="SwiftSafeHandle{T}"/>): Calls <c>Close()</c> on the
    /// SafeHandle to trigger ReleaseHandle, which uses a Cdecl trampoline
    /// (<c>SBW_VWTDestroy</c>) for VWT Destroy. This is safe from the GC finalizer
    /// thread on both Mono and NativeAOT. Uses <c>Close()</c> rather than
    /// <c>Dispose()</c> to leave <c>_explicitDispose</c> false, preserving the process
    /// exit guard that skips VWT Destroy when the Swift runtime may be torn down.
    /// </para>
    /// <para>
    /// For classes (<see cref="SwiftClassHandle{T}"/>): Not needed — SwiftClassHandle's
    /// built-in SafeHandle finalizer calls Arc.Release (Cdecl-safe on all runtimes).
    /// </para>
    /// </remarks>
    public static class SwiftDispose
    {
        /// <summary>
        /// Called from generated struct finalizers to ensure Swift ARC cleanup.
        /// Triggers VWT Destroy via Cdecl trampoline — safe on both Mono and NativeAOT.
        /// </summary>
        public static void FinalizerCleanup<T>(SwiftSafeHandle<T>? payload) where T : ISwiftObject
        {
            if (payload != null && !payload.IsInvalid && !payload.IsClosed)
                payload.Close();
        }
    }
}
