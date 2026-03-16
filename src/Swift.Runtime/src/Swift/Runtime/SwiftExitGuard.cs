// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using System.Threading;

[assembly: InternalsVisibleTo("Swift.Runtime.Tests")]

namespace Swift.Runtime;

/// <summary>
/// Shared process-exit guard for Swift handle types.
/// During process exit, finalizer-triggered Swift runtime calls (swift_release, Destroy, etc.)
/// are skipped to avoid crashes from Swift deinitializers running against a partially torn-down
/// runtime. Explicit Dispose() calls on user threads still release — only the GC finalizer
/// path is suppressed, since the caller may no longer be on a live thread with a valid Swift runtime.
/// Used by both <see cref="SwiftClassHandle{T}"/> and <see cref="SwiftSafeHandle{T}"/>.
///
/// Two independent signals detect process exit:
/// 1. <see cref="AppDomain.ProcessExit"/> event — sets the flag explicitly.
/// 2. <see cref="Environment.HasShutdownStarted"/> — checked on every read; returns true
///    once the NativeAOT/CLR runtime has begun shutdown, which may precede ProcessExit
///    and is reliable even if ProcessExit never fires (e.g., signal-based termination).
///
/// The static constructor registers the ProcessExit handler on first type access.
/// Call <see cref="EnsureInitialized"/> from application startup or generated code
/// to guarantee registration before any exit sequence begins.
/// </summary>
internal static class SwiftExitGuard
{
    private static volatile bool s_processExiting;

    /// <summary>
    /// Returns true once the process has begun exiting.
    /// Checks both the explicit ProcessExit flag and the runtime's own shutdown flag,
    /// ensuring coverage even if ProcessExit fires after finalization starts (NativeAOT/iOS)
    /// or doesn't fire at all (signal-based termination).
    /// </summary>
    internal static bool IsProcessExiting => s_processExiting || Environment.HasShutdownStarted;

    /// <summary>
    /// Static constructor — registers the ProcessExit handler on first type access.
    /// Call <see cref="EnsureInitialized"/> from application startup to guarantee
    /// the handler is registered before any exit sequence begins.
    /// </summary>
    static SwiftExitGuard()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => s_processExiting = true;
    }

    /// <summary>
    /// Ensures the static constructor has run, registering the ProcessExit handler.
    /// Called explicitly from generated code or application startup to guarantee
    /// the handler is registered before any exit sequence begins.
    /// </summary>
    internal static void EnsureInitialized()
    {
        // Reading the volatile field forces the static constructor to execute.
        _ = s_processExiting;
    }

    /// <summary>
    /// For testing: allows setting the process-exiting flag directly.
    /// Note: <see cref="Environment.HasShutdownStarted"/> cannot be overridden,
    /// but it is always false during test execution.
    /// </summary>
    internal static void SetProcessExitingForTest(bool value)
    {
        s_processExiting = value;
    }
}
