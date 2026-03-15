// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.CompilerServices;
using System.Threading;

[assembly: InternalsVisibleTo("Swift.Runtime.Tests")]

namespace Swift.Runtime;

/// <summary>
/// Shared process-exit guard for Swift handle types.
/// Sets a flag when ProcessExit fires so that finalizer-triggered handle cleanup
/// can skip calling into the Swift runtime (which may be partially torn down).
/// Used by both <see cref="SwiftClassHandle{T}"/> and <see cref="SwiftSafeHandle{T}"/>.
/// </summary>
internal static class SwiftExitGuard
{
    private static volatile bool s_processExiting;

    /// <summary>
    /// Returns true once AppDomain.ProcessExit has fired.
    /// </summary>
    internal static bool IsProcessExiting => s_processExiting;

    /// <summary>
    /// Static constructor — registers the ProcessExit handler eagerly on first type access.
    /// This MUST NOT be lazy (e.g., on first IsProcessExiting read) because the first read
    /// may happen during finalization after ProcessExit has already fired, in which case
    /// the handler would never register and the flag would stay false.
    /// </summary>
    static SwiftExitGuard()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => s_processExiting = true;
    }

    /// <summary>
    /// For testing: allows setting the process-exiting flag directly.
    /// </summary>
    internal static void SetProcessExitingForTest(bool value)
    {
        s_processExiting = value;
    }
}
