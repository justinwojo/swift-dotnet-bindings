// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common.Tooling;
using Serilog;

/// <summary>
/// Compiles Swift source files into modules and libraries via swiftc.
/// Resolves the swiftc path once via xcrun --find and invokes it directly.
/// Uses Tier 2 approach: helper class with ProcessTasks and a fluent settings class.
/// </summary>
public static class SwiftCompiler
{
    static readonly Lazy<string> SwiftcPath = new(() => XcRun.FindTool("swiftc"));

    /// <summary>
    /// Starts swiftc with the given settings. Does not wait for completion.
    /// </summary>
    public static IProcess Run(SwiftCompilerSettings settings)
    {
        var args = settings.BuildArguments();
        Log.Debug("swiftc {Arguments}", args);

        return ProcessTasks.StartProcess(
            SwiftcPath.Value, args,
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

/// <summary>
/// Fluent settings builder for xcrun swiftc invocations.
/// Mirrors the flags used across build-xcframework.sh, build-async-wrapper.sh, and build-bridge.sh.
/// </summary>
public class SwiftCompilerSettings
{
    public string? Target { get; private set; }
    public string? Sdk { get; private set; }
    public string? ModuleName { get; private set; }
    public bool EmitModule { get; private set; }
    public bool EmitLibrary { get; private set; }
    public bool EnableLibraryEvolution { get; private set; }
    public bool EmitModuleInterface { get; private set; }
    public string? OutputPath { get; private set; }
    public string? ModulePath { get; private set; }
    public string? ModuleInterfacePath { get; private set; }
    public string? InstallName { get; private set; }
    public string? StrictConcurrency { get; private set; }
    public string? SwiftVersion { get; private set; }
    public string? WorkingDirectory { get; private set; }

    private readonly List<string> _frameworkSearchPaths = new();
    private readonly List<string> _includeSearchPaths = new();
    private readonly List<string> _librarySearchPaths = new();
    private readonly List<string> _linkedLibraries = new();
    private readonly List<string> _sourceFiles = new();
    private readonly List<string> _extraArguments = new();

    public IReadOnlyList<string> FrameworkSearchPaths => _frameworkSearchPaths;
    public IReadOnlyList<string> IncludeSearchPaths => _includeSearchPaths;
    public IReadOnlyList<string> LibrarySearchPaths => _librarySearchPaths;
    public IReadOnlyList<string> LinkedLibraries => _linkedLibraries;
    public IReadOnlyList<string> SourceFiles => _sourceFiles;
    public IReadOnlyList<string> ExtraArguments => _extraArguments;

    public SwiftCompilerSettings SetTarget(string value) { Target = value; return this; }
    public SwiftCompilerSettings SetSdk(string value) { Sdk = value; return this; }
    public SwiftCompilerSettings SetModuleName(string value) { ModuleName = value; return this; }
    public SwiftCompilerSettings SetEmitModule(bool value = true) { EmitModule = value; return this; }
    public SwiftCompilerSettings SetEmitLibrary(bool value = true) { EmitLibrary = value; return this; }
    public SwiftCompilerSettings SetEnableLibraryEvolution(bool value = true) { EnableLibraryEvolution = value; return this; }
    public SwiftCompilerSettings SetEmitModuleInterface(bool value = true) { EmitModuleInterface = value; return this; }
    public SwiftCompilerSettings SetOutputPath(string value) { OutputPath = value; return this; }
    public SwiftCompilerSettings SetModulePath(string value) { ModulePath = value; return this; }
    public SwiftCompilerSettings SetModuleInterfacePath(string value) { ModuleInterfacePath = value; return this; }
    public SwiftCompilerSettings SetInstallName(string value) { InstallName = value; return this; }
    public SwiftCompilerSettings SetStrictConcurrency(string value) { StrictConcurrency = value; return this; }
    public SwiftCompilerSettings SetSwiftVersion(string value) { SwiftVersion = value; return this; }
    public SwiftCompilerSettings SetWorkingDirectory(string value) { WorkingDirectory = value; return this; }

    public SwiftCompilerSettings AddFrameworkSearchPath(string path) { _frameworkSearchPaths.Add(path); return this; }
    public SwiftCompilerSettings AddFrameworkSearchPaths(params string[] paths) { _frameworkSearchPaths.AddRange(paths); return this; }
    public SwiftCompilerSettings AddIncludeSearchPath(string path) { _includeSearchPaths.Add(path); return this; }
    public SwiftCompilerSettings AddLibrarySearchPath(string path) { _librarySearchPaths.Add(path); return this; }
    public SwiftCompilerSettings AddLinkedLibrary(string lib) { _linkedLibraries.Add(lib); return this; }
    public SwiftCompilerSettings AddSourceFile(string path) { _sourceFiles.Add(path); return this; }
    public SwiftCompilerSettings AddSourceFiles(IEnumerable<string> paths) { _sourceFiles.AddRange(paths); return this; }
    public SwiftCompilerSettings AddExtraArgument(string arg) { _extraArguments.Add(arg); return this; }

    /// <summary>
    /// Builds the argument string for xcrun swiftc.
    /// </summary>
    public string BuildArguments()
    {
        if (_sourceFiles.Count == 0 && !EmitModule)
            throw new ArgumentException("SwiftCompilerSettings requires at least one source file or -emit-module mode.");

        var args = new List<string>();

        if (EmitLibrary) args.Add("-emit-library");
        if (EmitModule) args.Add("-emit-module");
        if (EnableLibraryEvolution) args.Add("-enable-library-evolution");
        if (EmitModuleInterface) args.Add("-emit-module-interface");

        if (Target != null) { args.Add("-target"); args.Add(Target); }
        if (Sdk != null) { args.Add("-sdk"); args.Add(Sdk); }
        if (ModuleName != null) { args.Add("-module-name"); args.Add(ModuleName); }
        if (OutputPath != null) { args.Add("-o"); args.Add(OutputPath); }
        if (ModulePath != null) { args.Add("-emit-module-path"); args.Add(ModulePath); }
        if (ModuleInterfacePath != null) { args.Add("-emit-module-interface-path"); args.Add(ModuleInterfacePath); }
        if (StrictConcurrency != null) { args.Add($"-strict-concurrency={StrictConcurrency}"); }
        if (SwiftVersion != null) { args.Add("-swift-version"); args.Add(SwiftVersion); }

        if (InstallName != null)
        {
            args.Add("-Xlinker"); args.Add("-install_name");
            args.Add("-Xlinker"); args.Add(InstallName);
        }

        foreach (var path in _frameworkSearchPaths) { args.Add("-F"); args.Add(path); }
        foreach (var path in _includeSearchPaths) { args.Add("-I"); args.Add(path); }
        foreach (var path in _librarySearchPaths) { args.Add("-L"); args.Add(path); }
        foreach (var lib in _linkedLibraries) { args.Add("-l"); args.Add(lib); }

        args.AddRange(_extraArguments);
        args.AddRange(_sourceFiles);

        return string.Join(" ", args.Select(EscapeArgument));
    }

    private static string EscapeArgument(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"'))
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        return arg;
    }
}
