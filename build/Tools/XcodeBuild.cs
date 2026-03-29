// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common.Tooling;
using Serilog;

/// <summary>
/// Wrapper for xcodebuild — creates xcframeworks and archives/builds Swift packages.
/// Uses Tier 2 approach: helper class with ProcessTasks and fluent settings classes.
/// Two modes: CreateXcframework (from compiled framework slices) and ArchiveBuild (from source).
/// </summary>
public static class XcodeBuild
{
    /// <summary>
    /// Starts xcodebuild -create-xcframework. Does not wait for completion.
    /// </summary>
    public static IProcess CreateXcframework(CreateXcframeworkSettings settings)
    {
        var args = settings.BuildArguments();
        Log.Debug("xcodebuild {Arguments}", args);

        return ProcessTasks.StartProcess(
            "xcodebuild", args,
            workingDirectory: settings.WorkingDirectory);
    }

    /// <summary>
    /// Creates an xcframework, waits for completion, and asserts a zero exit code.
    /// </summary>
    public static IProcess ExecuteCreateXcframework(CreateXcframeworkSettings settings)
    {
        var process = CreateXcframework(settings);
        process.AssertWaitForExit();
        process.AssertZeroExitCode();
        return process;
    }

    /// <summary>
    /// Starts xcodebuild archive. Does not wait for completion.
    /// </summary>
    public static IProcess ArchiveBuild(ArchiveBuildSettings settings)
    {
        var args = settings.BuildArguments();
        Log.Debug("xcodebuild {Arguments}", args);

        return ProcessTasks.StartProcess(
            "xcodebuild", args,
            workingDirectory: settings.WorkingDirectory);
    }

    /// <summary>
    /// Archives a build, waits for completion, and asserts a zero exit code.
    /// </summary>
    public static IProcess ExecuteArchiveBuild(ArchiveBuildSettings settings)
    {
        var process = ArchiveBuild(settings);
        process.AssertWaitForExit();
        process.AssertZeroExitCode();
        return process;
    }
}

/// <summary>
/// Settings for xcodebuild -create-xcframework.
/// Creates a multi-platform xcframework from one or more framework directory slices.
/// </summary>
public class CreateXcframeworkSettings
{
    public string? OutputPath { get; private set; }
    public string? WorkingDirectory { get; private set; }

    private readonly List<string> _frameworkPaths = new();
    public IReadOnlyList<string> FrameworkPaths => _frameworkPaths;

    public CreateXcframeworkSettings AddFrameworkPath(string path) { _frameworkPaths.Add(path); return this; }
    public CreateXcframeworkSettings AddFrameworkPaths(params string[] paths) { _frameworkPaths.AddRange(paths); return this; }
    public CreateXcframeworkSettings SetOutputPath(string value) { OutputPath = value; return this; }
    public CreateXcframeworkSettings SetWorkingDirectory(string value) { WorkingDirectory = value; return this; }

    public string BuildArguments()
    {
        if (_frameworkPaths.Count == 0)
            throw new ArgumentException("CreateXcframeworkSettings requires at least one framework path.");
        if (OutputPath == null)
            throw new ArgumentException("CreateXcframeworkSettings requires OutputPath.");

        var args = new List<string> { "-create-xcframework" };

        foreach (var path in _frameworkPaths) { args.Add("-framework"); args.Add(path); }
        args.Add("-output"); args.Add(OutputPath);

        return ArgumentEscaper.Join(args);
    }
}

/// <summary>
/// Settings for xcodebuild archive — builds a Swift package or Xcode project from source.
/// Mirrors the pattern from fetch-libraries.sh: xcodebuild archive -scheme X -destination Y
/// with build settings like BUILD_LIBRARY_FOR_DISTRIBUTION=YES.
/// </summary>
public class ArchiveBuildSettings
{
    public string? Scheme { get; private set; }
    public string? Project { get; private set; }
    public string? Destination { get; private set; }
    public string? ArchivePath { get; private set; }
    public string? DerivedDataPath { get; private set; }
    public string? Configuration { get; private set; }
    public bool Quiet { get; private set; }
    public string? WorkingDirectory { get; private set; }

    private readonly Dictionary<string, string> _buildSettings = new();
    public IReadOnlyDictionary<string, string> BuildSettings => _buildSettings;

    public ArchiveBuildSettings SetScheme(string value) { Scheme = value; return this; }
    public ArchiveBuildSettings SetProject(string value) { Project = value; return this; }
    public ArchiveBuildSettings SetDestination(string value) { Destination = value; return this; }
    public ArchiveBuildSettings SetArchivePath(string value) { ArchivePath = value; return this; }
    public ArchiveBuildSettings SetDerivedDataPath(string value) { DerivedDataPath = value; return this; }
    public ArchiveBuildSettings SetConfiguration(string value) { Configuration = value; return this; }
    public ArchiveBuildSettings SetQuiet(bool value = true) { Quiet = value; return this; }
    public ArchiveBuildSettings SetWorkingDirectory(string value) { WorkingDirectory = value; return this; }

    public ArchiveBuildSettings AddBuildSetting(string key, string value) { _buildSettings[key] = value; return this; }

    /// <summary>
    /// Applies the standard build settings for library distribution:
    /// BUILD_LIBRARY_FOR_DISTRIBUTION=YES, SKIP_INSTALL=NO, MACH_O_TYPE=mh_dylib.
    /// </summary>
    public ArchiveBuildSettings SetLibraryDistributionDefaults()
    {
        _buildSettings["BUILD_LIBRARY_FOR_DISTRIBUTION"] = "YES";
        _buildSettings["SKIP_INSTALL"] = "NO";
        _buildSettings["MACH_O_TYPE"] = "mh_dylib";
        return this;
    }

    /// <summary>
    /// Sets the iOS deployment target.
    /// </summary>
    public ArchiveBuildSettings SetIosDeploymentTarget(string version)
    {
        _buildSettings["IPHONEOS_DEPLOYMENT_TARGET"] = version;
        return this;
    }

    public string BuildArguments()
    {
        if (Scheme == null)
            throw new ArgumentException("ArchiveBuildSettings requires Scheme.");

        var args = new List<string> { "archive" };

        args.Add("-scheme"); args.Add(Scheme);
        if (Project != null) { args.Add("-project"); args.Add(Project); }
        if (Destination != null) { args.Add("-destination"); args.Add(Destination); }
        if (ArchivePath != null) { args.Add("-archivePath"); args.Add(ArchivePath); }
        if (DerivedDataPath != null) { args.Add("-derivedDataPath"); args.Add(DerivedDataPath); }
        if (Configuration != null) { args.Add("-configuration"); args.Add(Configuration); }

        foreach (var (key, value) in _buildSettings.OrderBy(kv => kv.Key))
        {
            args.Add($"{key}={value}");
        }

        if (Quiet) args.Add("-quiet");

        return ArgumentEscaper.Join(args);
    }
}
