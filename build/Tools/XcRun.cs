// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Nuke.Common.IO;
using Nuke.Common.Tooling;

/// <summary>
/// Resolves Apple SDK paths and tool locations via xcrun.
/// </summary>
public static class XcRun
{
    /// <summary>
    /// Returns the SDK path for a given SDK name.
    /// Example: GetSdkPath("iphonesimulator") -> "/Applications/Xcode.app/.../iPhoneSimulator.sdk"
    /// </summary>
    public static AbsolutePath GetSdkPath(string sdkName)
    {
        var output = ProcessTasks.StartProcess(
                "xcrun", $"--sdk {sdkName} --show-sdk-path",
                logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode()
            .Output.StdToText().Trim();
        return (AbsolutePath)output;
    }

    /// <summary>
    /// Returns the full path to a developer tool.
    /// Example: FindTool("swiftc") -> "/usr/bin/swiftc"
    /// </summary>
    public static string FindTool(string toolName)
    {
        var output = ProcessTasks.StartProcess(
                "xcrun", $"--find {toolName}",
                logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode()
            .Output.StdToText().Trim();
        return output;
    }
}
