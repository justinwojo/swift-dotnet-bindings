// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common.Tooling;
using Serilog;

/// <summary>
/// Wrapper for xcrun swift-symbolgraph-extract — extracts symbol graphs for documentation.
/// Uses Tier 2 approach: helper class with ProcessTasks and a fluent settings class.
/// </summary>
public static class SymbolGraphExtract
{
    /// <summary>
    /// Starts xcrun swift-symbolgraph-extract. Does not wait for completion.
    /// </summary>
    public static IProcess Run(SymbolGraphExtractSettings settings)
    {
        var args = settings.BuildArguments();
        Log.Debug("swift-symbolgraph-extract {Arguments}", args);

        return ProcessTasks.StartProcess(
            "xcrun", $"swift-symbolgraph-extract {args}",
            workingDirectory: settings.WorkingDirectory);
    }

    /// <summary>
    /// Invokes xcrun swift-symbolgraph-extract, waits for completion, and asserts a zero exit code.
    /// </summary>
    public static IProcess Execute(SymbolGraphExtractSettings settings)
    {
        var process = Run(settings);
        process.AssertWaitForExit();
        process.AssertZeroExitCode();
        return process;
    }
}

/// <summary>
/// Fluent settings builder for xcrun swift-symbolgraph-extract invocations.
/// Extracts symbol graph JSON files for Swift module documentation.
/// </summary>
public class SymbolGraphExtractSettings
{
    public string? ModuleName { get; private set; }
    public string? Target { get; private set; }
    public string? Sdk { get; private set; }
    public string? OutputDir { get; private set; }
    public bool PrettyPrint { get; private set; }
    public string? WorkingDirectory { get; private set; }

    private readonly List<string> _frameworkSearchPaths = new();
    private readonly List<string> _includeSearchPaths = new();

    public IReadOnlyList<string> FrameworkSearchPaths => _frameworkSearchPaths;
    public IReadOnlyList<string> IncludeSearchPaths => _includeSearchPaths;

    public SymbolGraphExtractSettings SetModuleName(string value) { ModuleName = value; return this; }
    public SymbolGraphExtractSettings SetTarget(string value) { Target = value; return this; }
    public SymbolGraphExtractSettings SetSdk(string value) { Sdk = value; return this; }
    public SymbolGraphExtractSettings SetOutputDir(string value) { OutputDir = value; return this; }
    public SymbolGraphExtractSettings SetPrettyPrint(bool value = true) { PrettyPrint = value; return this; }
    public SymbolGraphExtractSettings SetWorkingDirectory(string value) { WorkingDirectory = value; return this; }

    public SymbolGraphExtractSettings AddFrameworkSearchPath(string path) { _frameworkSearchPaths.Add(path); return this; }
    public SymbolGraphExtractSettings AddFrameworkSearchPaths(params string[] paths) { _frameworkSearchPaths.AddRange(paths); return this; }
    public SymbolGraphExtractSettings AddIncludeSearchPath(string path) { _includeSearchPaths.Add(path); return this; }
    public SymbolGraphExtractSettings AddIncludeSearchPaths(params string[] paths) { _includeSearchPaths.AddRange(paths); return this; }

    /// <summary>
    /// Builds the argument string for xcrun swift-symbolgraph-extract.
    /// </summary>
    public string BuildArguments()
    {
        if (ModuleName == null)
            throw new ArgumentException("SymbolGraphExtractSettings requires ModuleName.");

        var args = new List<string>();

        args.Add("-module-name"); args.Add(ModuleName);
        if (Target != null) { args.Add("-target"); args.Add(Target); }
        if (Sdk != null) { args.Add("-sdk"); args.Add(Sdk); }

        foreach (var path in _includeSearchPaths) { args.Add("-I"); args.Add(path); }
        foreach (var path in _frameworkSearchPaths) { args.Add("-F"); args.Add(path); }

        if (OutputDir != null) { args.Add("-output-dir"); args.Add(OutputDir); }
        if (PrettyPrint) args.Add("-pretty-print");

        return string.Join(" ", args.Select(EscapeArgument));
    }

    private static string EscapeArgument(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        return arg;
    }
}
