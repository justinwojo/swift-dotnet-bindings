// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Swift.Runtime;

/// <summary>
/// Debug-only guard asserting that the caller is running on the platform main thread.
/// Emitted at the top of every generated wrapper for a Swift <c>@MainActor</c>-isolated
/// member: the <see cref="SwiftMainActorAttribute"/> documents the requirement, this
/// guard catches a violation of it during development.
/// </summary>
/// <remarks>
/// The check is compiled in only for Debug consumers (it is annotated
/// <see cref="ConditionalAttribute"/> for <c>DEBUG</c>), so Release output is unchanged.
/// On Apple platforms it uses <c>pthread_main_np()</c> from libSystem — no
/// UIKit/AppKit/Foundation dependency, and it works under Mono-JIT, CoreCLR, and
/// NativeAOT alike. On non-Apple platforms (where there is no Swift main-thread concept)
/// it is a no-op.
/// </remarks>
public static class MainActorGuard
{
    [DllImport("libSystem.dylib", EntryPoint = "pthread_main_np")]
    private static extern int PthreadMainNp();

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when the current thread is not the
    /// platform main thread. Compiled out entirely in Release builds.
    /// </summary>
    /// <param name="memberName">
    /// Auto-populated with the calling member's name for a precise diagnostic; callers do
    /// not pass this explicitly.
    /// </param>
    [Conditional("DEBUG")]
    public static void AssertMainThread([CallerMemberName] string? memberName = null)
    {
        if (!IsApplePlatform())
            return;

        if (PthreadMainNp() == 0)
        {
            throw new InvalidOperationException(
                $"[SwiftBindings] '{memberName ?? "member"}' maps to a Swift @MainActor-isolated " +
                "declaration and must be called on the main thread. Marshal the call onto the main " +
                "thread (e.g. via the platform main dispatch queue) before invoking it.");
        }
    }

    private static bool IsApplePlatform()
        => OperatingSystem.IsIOS() || OperatingSystem.IsMacOS()
        || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS()
        || OperatingSystem.IsWatchOS();
}
