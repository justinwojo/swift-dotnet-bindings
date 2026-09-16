// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Runtime.InteropServices;
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
#if ACTIVITYKIT_PUSH_TOKEN
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint CfStringEncodingUtf8 = 0x08000100;

    [DllImport(SecurityFramework)]
    private static extern IntPtr SecTaskCreateFromSelf(IntPtr allocator);

    [DllImport(SecurityFramework)]
    private static extern IntPtr SecTaskCopyValueForEntitlement(
        IntPtr task,
        IntPtr entitlement,
        IntPtr error);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr value);

    [DllImport(CoreFoundationFramework)]
    private static extern nuint CFGetTypeID(IntPtr value);

    [DllImport(CoreFoundationFramework)]
    private static extern nuint CFStringGetTypeID();

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFStringGetLength(IntPtr value);

    [DllImport(CoreFoundationFramework)]
    private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [DllImport(CoreFoundationFramework)]
    private static extern byte CFStringGetCString(
        IntPtr value,
        IntPtr buffer,
        nint bufferSize,
        uint encoding);

    /// <summary>
    /// Reads the entitlement from the running process's code signature and
    /// fails closed before ActivityKit.request when it is absent. The Nuke
    /// harness separately verifies the final app plus embedded profile before
    /// installation; this live check prevents a manually launched or re-signed
    /// bundle from bypassing the same contract.
    /// </summary>
    public static string RequirePushTokenCapability()
    {
        var task = SecTaskCreateFromSelf(IntPtr.Zero);
        if (task == IntPtr.Zero)
            return ActivityKitEntitlementGate.RequireRuntimeEnvironment(null);

        try
        {
            using var entitlement = new Foundation.NSString(ActivityKitEntitlementGate.RequiredEntitlement);
            var value = SecTaskCopyValueForEntitlement(task, entitlement.Handle, IntPtr.Zero);
            if (value == IntPtr.Zero)
                return ActivityKitEntitlementGate.RequireRuntimeEnvironment(null);
            try
            {
                if (CFGetTypeID(value) != CFStringGetTypeID())
                    return ActivityKitEntitlementGate.RequireRuntimeEnvironment(null);
                var length = CFStringGetLength(value);
                var capacity = CFStringGetMaximumSizeForEncoding(length, CfStringEncodingUtf8) + 1;
                if (capacity <= 1)
                    return ActivityKitEntitlementGate.RequireRuntimeEnvironment(null);
                var buffer = Marshal.AllocHGlobal(checked((int)capacity));
                try
                {
                    var environment = CFStringGetCString(value, buffer, capacity, CfStringEncodingUtf8) != 0
                        ? Marshal.PtrToStringUTF8(buffer)
                        : null;
                    return ActivityKitEntitlementGate.RequireRuntimeEnvironment(environment);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CFRelease(value);
            }
        }
        finally
        {
            CFRelease(task);
        }
    }
#endif

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
