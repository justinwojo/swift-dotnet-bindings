// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.ComponentModel;
using System.IO;

namespace BindingsGeneration.Tests;

/// <summary>
/// Paths inside the Apple SDK of the selected Xcode. Resolved through <c>xcrun</c>, which honours
/// <c>DEVELOPER_DIR</c> and <c>xcode-select</c>, so a test reads the SDK of the toolchain it runs
/// under rather than whatever Xcode happens to be installed at a fixed path.
/// </summary>
internal static class SelectedAppleSdk
{
    /// <summary>
    /// The macOS SDK's <c>Foundation.tbd</c>, or <c>null</c> when <c>xcrun</c> is unavailable or
    /// cannot resolve the macOS SDK (no Xcode selected).
    /// </summary>
    public static string? MacOSFoundationTbd { get; } = Resolve(
        "macosx", "System/Library/Frameworks/Foundation.framework/Foundation.tbd");

    private static string? Resolve(string sdk, string relativePath)
    {
        int exitCode;
        string stdout;
        try
        {
            (exitCode, stdout, _) = new SystemCommandRunner().Run("xcrun", $"--sdk {sdk} --show-sdk-path");
        }
        catch (Win32Exception)
        {
            return null; // No xcrun on this host.
        }
        var sdkPath = stdout.Trim();
        return exitCode == 0 && sdkPath.Length > 0 ? Path.Combine(sdkPath, relativePath) : null;
    }
}
