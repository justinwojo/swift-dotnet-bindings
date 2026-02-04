// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Provides initialization for Swift concurrency interop with .NET.
/// </summary>
/// <remarks>
/// <para>
/// Swift's cooperative concurrency model uses a dedicated thread pool that .NET
/// threads don't participate in. When C# calls a Swift async method via P/Invoke,
/// the Swift task is enqueued but never executes. This class hooks Swift's global
/// task enqueue mechanism to redirect tasks to GCD, where they will run.
/// </para>
/// <para>
/// Call <see cref="Initialize"/> once at application startup before any async
/// Swift calls from C#.
/// </para>
/// <para>
/// <b>Known limitations:</b>
/// <list type="bullet">
/// <item><description>
/// <c>@MainActor</c> tasks are NOT intercepted. The main executor hook is buggy
/// in Swift 5.5–6.0 and often not invoked by the runtime.
/// </description></item>
/// <item><description>
/// Task cancellation does not propagate through GCD dispatch.
/// </description></item>
/// <item><description>
/// Custom actor executors are not intercepted — only plain <c>Task {}</c> and
/// <c>Task.detached {}</c> go through the global hook.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
public static class SwiftConcurrency
{
    private static volatile bool _isInitialized;
    private static readonly object _lock = new();

    /// <summary>
    /// Initialize Swift concurrency for interop with .NET.
    /// </summary>
    /// <remarks>
    /// Hooks <c>swift_task_enqueueGlobal_hook</c> to redirect Swift tasks to GCD
    /// instead of Swift's cooperative thread pool. Safe to call multiple times;
    /// subsequent calls are no-ops.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the SwiftBindingsRuntime native library cannot be found, or
    /// when the concurrency hook could not be installed (e.g. swift_task_enqueueGlobal_hook
    /// symbol not found in the Swift runtime).
    /// </exception>
    public static void Initialize()
    {
        if (_isInitialized) return;

        lock (_lock)
        {
            if (_isInitialized) return;

            try
            {
                NativeMethods.SwiftBindings_InitializeConcurrency();
                _isInitialized = NativeMethods.SwiftBindings_IsConcurrencyInitialized();

                if (!_isInitialized)
                {
                    throw new InvalidOperationException(
                        "Swift concurrency hook could not be installed. " +
                        "The swift_task_enqueueGlobal_hook symbol was not found in the Swift runtime.");
                }
            }
            catch (DllNotFoundException ex)
            {
                throw new InvalidOperationException(
                    "SwiftBindingsRuntime native library not found. Ensure libSwiftBindingsRuntime.dylib " +
                    "is included in your application bundle.", ex);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether Swift concurrency has been initialized.
    /// </summary>
    public static bool IsInitialized
    {
        get
        {
            if (_isInitialized) return true;

            try
            {
                return NativeMethods.SwiftBindings_IsConcurrencyInitialized();
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }
    }

    private static class NativeMethods
    {
        private const string LibraryName = "SwiftBindingsRuntime";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void SwiftBindings_InitializeConcurrency();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool SwiftBindings_IsConcurrencyInitialized();
    }
}
