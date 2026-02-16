// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace Swift.Runtime
{
    /// <summary>
    /// Provides centralized finalizer cleanup for generated Swift binding types.
    /// On NativeAOT (non-Mono), calls full Dispose which triggers VWT Destroy.
    /// On Mono (JIT or AOT), no-op — SafeHandle's own finalizer handles buffer
    /// deallocation without VWT Destroy, avoiding jit-info.c assertion crashes.
    /// </summary>
    public static class SwiftDispose
    {
        private static readonly bool s_isMonoRuntime = Type.GetType("Mono.Runtime") != null;

        /// <summary>
        /// Called from generated type finalizers to ensure Swift ARC cleanup.
        /// </summary>
        public static void FinalizerCleanup<T>(SwiftSafeHandle<T>? payload) where T : ISwiftObject
        {
            if (!s_isMonoRuntime && payload != null && !payload.IsInvalid)
                payload.Dispose();
        }
    }
}
