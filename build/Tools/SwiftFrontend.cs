// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Nuke.Common.Tooling;
using Serilog;

/// <summary>
/// Generates ABI JSON descriptors from .swiftinterface files via swift-frontend.
/// Resolves the swift-frontend path once via xcrun --find and invokes it directly.
/// Uses Tier 2 approach: helper class with ProcessTasks and a fluent settings class.
/// </summary>
public static class SwiftFrontend
{
    /// <summary>
    /// Starts swift-frontend with the given settings. Does not wait for completion.
    /// </summary>
    public static IProcess Run(SwiftFrontendSettings settings)
    {
        var args = settings.BuildArguments();
        Log.Debug("swift-frontend {Arguments}", args);

        return ProcessTasks.StartProcess(
            XcRun.FindTool("swift-frontend"), args,
            workingDirectory: settings.WorkingDirectory);
    }

    /// <summary>
    /// Invokes swift-frontend, waits for completion, and asserts a zero exit code.
    /// </summary>
    public static IProcess Execute(SwiftFrontendSettings settings)
    {
        var process = Run(settings);
        process.AssertWaitForExit();
        process.AssertZeroExitCode();
        return process;
    }
}

/// <summary>
/// Fluent settings builder for xcrun swift-frontend invocations.
/// Primary use case: generating ABI JSON from .swiftinterface files.
/// </summary>
public class SwiftFrontendSettings
{
    public string? SwiftInterfacePath { get; private set; }
    public string? Target { get; private set; }
    public string? ModuleName { get; private set; }
    public string? Sdk { get; private set; }
    public string? AbiDescriptorPath { get; private set; }
    public string? WorkingDirectory { get; private set; }

    private readonly List<string> _frameworkSearchPaths = new();
    private readonly List<string> _includeSearchPaths = new();

    public IReadOnlyList<string> FrameworkSearchPaths => _frameworkSearchPaths;
    public IReadOnlyList<string> IncludeSearchPaths => _includeSearchPaths;

    public SwiftFrontendSettings SetSwiftInterfacePath(string value) { SwiftInterfacePath = value; return this; }
    public SwiftFrontendSettings SetTarget(string value) { Target = value; return this; }
    public SwiftFrontendSettings SetModuleName(string value) { ModuleName = value; return this; }
    public SwiftFrontendSettings SetSdk(string value) { Sdk = value; return this; }
    public SwiftFrontendSettings SetAbiDescriptorPath(string value) { AbiDescriptorPath = value; return this; }
    public SwiftFrontendSettings SetWorkingDirectory(string value) { WorkingDirectory = value; return this; }

    public SwiftFrontendSettings AddFrameworkSearchPath(string path) { _frameworkSearchPaths.Add(path); return this; }
    public SwiftFrontendSettings AddFrameworkSearchPaths(params string[] paths) { _frameworkSearchPaths.AddRange(paths); return this; }
    public SwiftFrontendSettings AddIncludeSearchPath(string path) { _includeSearchPaths.Add(path); return this; }

    /// <summary>
    /// Builds the argument string for xcrun swift-frontend.
    /// Always includes -compile-module-from-interface as the command mode.
    /// </summary>
    public string BuildArguments()
    {
        if (SwiftInterfacePath == null)
            throw new ArgumentException("SwiftFrontendSettings requires SwiftInterfacePath.");

        var args = new List<string>();

        args.Add("-compile-module-from-interface");
        args.Add(SwiftInterfacePath);

        if (Target != null) { args.Add("-target"); args.Add(Target); }
        if (ModuleName != null) { args.Add("-module-name"); args.Add(ModuleName); }
        if (Sdk != null) { args.Add("-sdk"); args.Add(Sdk); }

        foreach (var path in _frameworkSearchPaths) { args.Add("-F"); args.Add(path); }
        foreach (var path in _includeSearchPaths) { args.Add("-I"); args.Add(path); }

        if (AbiDescriptorPath != null) { args.Add("-emit-abi-descriptor-path"); args.Add(AbiDescriptorPath); }

        return ArgumentEscaper.Join(args);
    }
}
