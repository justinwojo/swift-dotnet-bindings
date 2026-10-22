// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Nuke.Common.Tooling;
using Serilog;

/// <summary>
/// Compiles Swift source files into modules and libraries via swiftc.
/// Resolves the swiftc path once via xcrun --find and invokes it directly.
/// Uses Tier 2 approach: helper class with ProcessTasks and a fluent settings class.
/// </summary>
public static class SwiftCompiler
{
    /// <summary>
    /// Starts swiftc with the given settings. Does not wait for completion.
    /// </summary>
    public static IProcess Run(SwiftCompilerSettings settings)
    {
        var args = settings.BuildArguments();
        Log.Debug("swiftc {Arguments}", args);

        return ProcessTasks.StartProcess(
            XcRun.FindTool("swiftc"), args,
            workingDirectory: settings.WorkingDirectory);
    }

    /// <summary>
    /// Invokes swiftc, waits for completion, and asserts a zero exit code.
    /// </summary>
    public static IProcess Execute(SwiftCompilerSettings settings)
    {
        var process = Run(settings);
        process.AssertWaitForExit();
        process.AssertZeroExitCode();
        return process;
    }
}
