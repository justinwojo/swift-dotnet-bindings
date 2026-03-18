// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime
{
    /// <summary>
    /// Provides centralized finalizer cleanup for generated Swift binding types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For structs (<see cref="SwiftSafeHandle{T}"/>): On NativeAOT (non-Mono), calls full
    /// Dispose which triggers the @_cdecl destroy action. On Mono (JIT or AOT), no-op —
    /// SafeHandle's own finalizer handles buffer deallocation without Destroy, avoiding
    /// jit-info.c assertion crashes. Note: SwiftSafeHandle.ReleaseHandle now also calls
    /// the @_cdecl destroy action on NativeAOT during finalization, so the generated
    /// finalizer is a belt-and-suspenders safety net.
    /// </para>
    /// <para>
    /// For classes (<see cref="SwiftClassHandle{T}"/>): Not needed — SwiftClassHandle's
    /// built-in SafeHandle finalizer calls Arc.Release (Cdecl-safe on all runtimes).
    /// </para>
    /// </remarks>
    public static class SwiftDispose
    {
        private static readonly bool s_isMonoRuntime = SwiftRuntimeInfo.IsMonoRuntime;

        /// <summary>
        /// Called from generated struct finalizers to ensure Swift ARC cleanup.
        /// On NativeAOT: calls full Dispose (triggers @_cdecl destroy action).
        /// On Mono: no-op (SafeHandle's own finalizer handles buffer-only cleanup).
        /// </summary>
        public static void FinalizerCleanup<T>(SwiftSafeHandle<T>? payload) where T : ISwiftObject
        {
            if (!s_isMonoRuntime && payload != null && !payload.IsInvalid)
                payload.Dispose();
        }
    }
}
