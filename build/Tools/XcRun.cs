// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Nuke.Common.IO;
using Nuke.Common.Tooling;

/// <summary>
/// Resolves Apple SDK paths and tool locations via xcrun.
/// Results are cached for the lifetime of the build process — SDK paths and tool locations
/// don't change mid-build.
/// </summary>
public static class XcRun
{
    private static readonly ConcurrentDictionary<string, AbsolutePath> SdkPathCache = new();
    private static readonly ConcurrentDictionary<string, string> ToolPathCache = new();

    /// <summary>
    /// Returns the SDK path for a given SDK name. Cached after first lookup.
    /// Example: GetSdkPath("iphonesimulator") -> "/Applications/Xcode.app/.../iPhoneSimulator.sdk"
    /// </summary>
    public static AbsolutePath GetSdkPath(string sdkName) =>
        SdkPathCache.GetOrAdd(sdkName, name =>
        {
            var output = ProcessTasks.StartProcess(
                    "xcrun", $"--sdk {name} --show-sdk-path",
                    logOutput: false)
                .AssertWaitForExit()
                .AssertZeroExitCode()
                .Output.StdToText().Trim();
            return (AbsolutePath)output;
        });

    /// <summary>
    /// Returns the full path to a developer tool. Cached after first lookup.
    /// Example: FindTool("swiftc") -> "/usr/bin/swiftc"
    /// </summary>
    public static string FindTool(string toolName) =>
        ToolPathCache.GetOrAdd(toolName, name =>
        {
            var output = ProcessTasks.StartProcess(
                    "xcrun", $"--find {name}",
                    logOutput: false)
                .AssertWaitForExit()
                .AssertZeroExitCode()
                .Output.StdToText().Trim();
            return output;
        });
}
