// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Threading.Tasks;
using UIKit;

namespace RuntimeTestsApp.AppleSupplement;

/// <summary>
/// ActivityKit precondition shared by <see cref="LiveActivityTests"/> and the
/// <c>--persist-activity</c> visual-proof path in Program.cs: request() throws
/// unless the host app is foreground-active, and launch transitions through
/// Inactive, so callers must wait for Active first. Lives here rather than in
/// Infrastructure/ because it needs UIKit, and Infrastructure compiles into the
/// UIKit-less macOS host too.
/// </summary>
internal static class ActivityKitReadiness
{
    /// <summary>
    /// Waits (bounded) for the app to reach foreground-active. Returns false when
    /// the state never arrived within <paramref name="timeout"/> — the caller
    /// decides whether that is a hard test failure or a user-visible message,
    /// rather than proceeding into an unattributable ActivityKit visibility error.
    /// Resumes on the main thread under the UIKit sync context, so UIApplication
    /// access here is safe.
    /// </summary>
    public static async Task<bool> WaitForForegroundActiveAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (UIApplication.SharedApplication.ApplicationState != UIApplicationState.Active)
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(50);
        }
        return true;
    }
}
