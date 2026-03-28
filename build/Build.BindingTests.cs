// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    const string ModuleName = "SwiftBindingsTestLib";
    const string DepModuleName = "SwiftBindingsTestLibDependency";
    const string WrapperModule = "SwiftBindings";
    const string BridgeModule = "SwiftBindingsTestLibBridge";

    [Parameter("Fail on non-zero generator exit (standalone use)")] readonly bool Strict;
    bool ForceStrict; // Set by BindingTestsStrict target

    // --- Computed BindingTests paths ---
    AbsolutePath BtBuildDir => BindingTestsDir / ".build";
    AbsolutePath BtOutputDir => BindingTestsDir / "output";
    AbsolutePath BtSymbolgraphDir => BtBuildDir / "symbolgraph";
    AbsolutePath BtXcframeworkDir => BtBuildDir / $"{ModuleName}.xcframework";
    AbsolutePath BtDepXcframeworkDir => BtBuildDir / $"{DepModuleName}.xcframework";

    // ============================================================
    // BuildXcframework target — ports build-xcframework.sh
    // ============================================================

    Target BuildXcframework => _ => _
        .Executes(() => RunBuildXcframework());

    void RunBuildXcframework(ApplePlatform? platformOverride = null, bool? includeDeviceOverride = null)
    {
        var platform = platformOverride ?? ResolvedPlatform;
        var sdkPath = XcRun.GetSdkPath(platform.SimulatorSdkName);
        var simBuildDir = BtBuildDir / platform.SimulatorSliceId;
        var frameworkDir = simBuildDir / $"{ModuleName}.framework";
        var depFrameworkDir = simBuildDir / $"{DepModuleName}.framework";

        Log.Information("=== Building {Module} ===", ModuleName);
        Log.Information("Platform: {Platform}, Target: {Target}", platform.Name, platform.SimulatorTarget);

        // macOS ignores --include-device
        var requestedIncludeDevice = includeDeviceOverride ?? IncludeDevice;
        var includeDevice = requestedIncludeDevice && platform.HasDeviceSlice;
        if (requestedIncludeDevice && !platform.HasDeviceSlice)
            Log.Information("Note: --include-device ignored for {Platform} (no device slice)", platform.Name);

        // Clean previous build
        BtBuildDir.DeleteDirectory();
        simBuildDir.CreateDirectory();

        // --- Build dependency module ---
        BuildModuleSlice(
            DepModuleName, platform, sdkPath, simBuildDir, depFrameworkDir,
            GetDepSourceFiles(), frameworkSearchPaths: null);

        // --- Build main module ---
        var mainSourceFiles = GetMainSourceFiles();
        BuildModuleSlice(
            ModuleName, platform, sdkPath, simBuildDir, frameworkDir,
            mainSourceFiles, frameworkSearchPaths: new[] { simBuildDir.ToString() });

        // --- Extract symbol graph ---
        ExtractSymbolGraph(platform, sdkPath, simBuildDir);

        // --- Device slice (optional, iOS/tvOS only) ---
        if (includeDevice)
            BuildDeviceSlices(platform, mainSourceFiles);

        // --- Create xcframeworks ---
        CreateXcframeworks(platform, includeDevice, simBuildDir);

        Log.Information("=== Build Complete ===");
        Log.Information("xcframeworks: {Main}, {Dep}", BtXcframeworkDir, BtDepXcframeworkDir);
    }

    void BuildModuleSlice(
        string moduleName, ApplePlatform platform, string sdkPath,
        string buildDir, string frameworkDir,
        IReadOnlyList<string> sourceFiles, string[]? frameworkSearchPaths)
    {
        Log.Information("--- Building {Module} ({Platform}) ---", moduleName, platform.Name);

        var moduleDir = Path.Combine(frameworkDir, "Modules", $"{moduleName}.swiftmodule");
        Directory.CreateDirectory(moduleDir);

        var settings = new SwiftCompilerSettings()
            .SetTarget(platform.SimulatorTarget)
            .SetSdk(sdkPath)
            .SetEmitModule()
            .SetEmitLibrary()
            .SetEnableLibraryEvolution()
            .SetEmitModuleInterface()
            .SetModuleName(moduleName)
            .SetInstallName($"@rpath/{moduleName}.framework/{moduleName}")
            .SetOutputPath(Path.Combine(frameworkDir, moduleName))
            .SetModulePath(Path.Combine(moduleDir, $"{platform.SimulatorModuleSuffix}.swiftmodule"))
            .SetModuleInterfacePath(Path.Combine(moduleDir, $"{platform.SimulatorModuleSuffix}.swiftinterface"))
            .AddSourceFiles(sourceFiles);

        if (frameworkSearchPaths != null)
        {
            foreach (var path in frameworkSearchPaths)
                settings.AddFrameworkSearchPath(path);
        }

        SwiftCompiler.Execute(settings);

        // Copy private swiftinterface (same as public for our purposes)
        File.Copy(
            Path.Combine(moduleDir, $"{platform.SimulatorModuleSuffix}.swiftinterface"),
            Path.Combine(moduleDir, $"{platform.SimulatorModuleSuffix}.private.swiftinterface"),
            overwrite: true);

        // Generate TBD
        TapiStubify(
            Path.Combine(frameworkDir, moduleName),
            Path.Combine(moduleDir, $"{moduleName}.tbd"));

        // Generate ABI JSON
        var frontendSettings = new SwiftFrontendSettings()
            .SetSwiftInterfacePath(Path.Combine(moduleDir, $"{platform.SimulatorModuleSuffix}.swiftinterface"))
            .SetTarget(platform.SimulatorTarget)
            .SetModuleName(moduleName)
            .SetSdk(sdkPath)
            .SetAbiDescriptorPath(Path.Combine(moduleDir, $"{platform.SimulatorModuleSuffix}.abi.json"));

        if (frameworkSearchPaths != null)
        {
            foreach (var path in frameworkSearchPaths)
                frontendSettings.AddFrameworkSearchPath(path);
        }

        SwiftFrontend.Execute(frontendSettings);

        // Info.plist
        PlistGenerator.WriteFrameworkPlist(
            Path.Combine(frameworkDir, "Info.plist"),
            $"com.test.{moduleName}", moduleName, moduleName,
            platform.MinOsVersion, platform.SimulatorPlistPlatform);

        Log.Information("{Module} built: {Dir}", moduleName, frameworkDir);
    }

    void BuildDeviceSlices(ApplePlatform platform, IReadOnlyList<string> mainSourceFiles)
    {
        var deviceSdkPath = XcRun.GetSdkPath(platform.DeviceSdkName!);
        var deviceBuildDir = BtBuildDir / platform.DeviceSliceId!;
        var depDeviceFrameworkDir = deviceBuildDir / $"{DepModuleName}.framework";
        var deviceFrameworkDir = deviceBuildDir / $"{ModuleName}.framework";

        Log.Information("--- Compiling device slice ---");
        Log.Information("Device target: {Target}, SDK: {Sdk}", platform.DeviceTarget, deviceSdkPath);

        deviceBuildDir.CreateDirectory();

        // Build dependency device slice
        BuildDeviceModuleSlice(
            DepModuleName, platform, deviceSdkPath, deviceBuildDir, depDeviceFrameworkDir,
            GetDepSourceFiles(), frameworkSearchPaths: null);

        // Build main module device slice
        BuildDeviceModuleSlice(
            ModuleName, platform, deviceSdkPath, deviceBuildDir, deviceFrameworkDir,
            mainSourceFiles, frameworkSearchPaths: new[] { deviceBuildDir.ToString() });

        // TBD and ABI JSON for main device slice
        Log.Information("=== Generating TBD (device) ===");
        TapiStubify(
            deviceFrameworkDir / ModuleName,
            deviceFrameworkDir / "Modules" / $"{ModuleName}.swiftmodule" / $"{ModuleName}.tbd");

        Log.Information("=== Generating ABI JSON (device) ===");
        SwiftFrontend.Execute(new SwiftFrontendSettings()
            .SetSwiftInterfacePath(deviceFrameworkDir / "Modules" / $"{ModuleName}.swiftmodule" / $"{platform.DeviceModuleSuffix}.swiftinterface")
            .SetTarget(platform.DeviceTarget!)
            .SetModuleName(ModuleName)
            .SetSdk(deviceSdkPath)
            .AddFrameworkSearchPath(deviceBuildDir)
            .SetAbiDescriptorPath(deviceFrameworkDir / "Modules" / $"{ModuleName}.swiftmodule" / $"{platform.DeviceModuleSuffix}.abi.json"));
    }

    void BuildDeviceModuleSlice(
        string moduleName, ApplePlatform platform, string sdkPath,
        string buildDir, string frameworkDir,
        IReadOnlyList<string> sourceFiles, string[]? frameworkSearchPaths)
    {
        var moduleDir = Path.Combine(frameworkDir, "Modules", $"{moduleName}.swiftmodule");
        Directory.CreateDirectory(moduleDir);

        var settings = new SwiftCompilerSettings()
            .SetTarget(platform.DeviceTarget!)
            .SetSdk(sdkPath)
            .SetEmitModule()
            .SetEmitLibrary()
            .SetEnableLibraryEvolution()
            .SetEmitModuleInterface()
            .SetModuleName(moduleName)
            .SetInstallName($"@rpath/{moduleName}.framework/{moduleName}")
            .SetOutputPath(Path.Combine(frameworkDir, moduleName))
            .SetModulePath(Path.Combine(moduleDir, $"{platform.DeviceModuleSuffix}.swiftmodule"))
            .SetModuleInterfacePath(Path.Combine(moduleDir, $"{platform.DeviceModuleSuffix}.swiftinterface"))
            .AddSourceFiles(sourceFiles);

        if (frameworkSearchPaths != null)
        {
            foreach (var path in frameworkSearchPaths)
                settings.AddFrameworkSearchPath(path);
        }

        SwiftCompiler.Execute(settings);

        // Copy private swiftinterface
        File.Copy(
            Path.Combine(moduleDir, $"{platform.DeviceModuleSuffix}.swiftinterface"),
            Path.Combine(moduleDir, $"{platform.DeviceModuleSuffix}.private.swiftinterface"),
            overwrite: true);

        // Generate TBD
        TapiStubify(
            Path.Combine(frameworkDir, moduleName),
            Path.Combine(moduleDir, $"{moduleName}.tbd"));

        // Generate ABI JSON
        SwiftFrontend.Execute(new SwiftFrontendSettings()
            .SetSwiftInterfacePath(Path.Combine(moduleDir, $"{platform.DeviceModuleSuffix}.swiftinterface"))
            .SetTarget(platform.DeviceTarget!)
            .SetModuleName(moduleName)
            .SetSdk(sdkPath)
            .SetAbiDescriptorPath(Path.Combine(moduleDir, $"{platform.DeviceModuleSuffix}.abi.json")));

        // Info.plist
        PlistGenerator.WriteFrameworkPlist(
            Path.Combine(frameworkDir, "Info.plist"),
            $"com.test.{moduleName}", moduleName, moduleName,
            platform.MinOsVersion, platform.DevicePlistPlatform!);
    }

    void ExtractSymbolGraph(ApplePlatform platform, string sdkPath, string simBuildDir)
    {
        Log.Information("=== Extracting Symbol Graph ===");
        BtSymbolgraphDir.CreateDirectory();

        try
        {
            SymbolGraphExtract.Execute(new SymbolGraphExtractSettings()
                .SetModuleName(ModuleName)
                .SetTarget(platform.SimulatorTarget)
                .SetSdk(sdkPath)
                .AddIncludeSearchPath(simBuildDir)
                .AddFrameworkSearchPath(simBuildDir)
                .SetOutputDir(BtSymbolgraphDir)
                .SetPrettyPrint());

            var sgCount = Directory.GetFiles(BtSymbolgraphDir, "*.symbols.json").Length;
            Log.Information("Extracted {Count} symbol graph files", sgCount);
        }
        catch (Exception ex)
        {
            Log.Warning("swift-symbolgraph-extract failed: {Message}. Doc comments will not be available.", ex.Message);
        }
    }

    void TapiStubify(string inputDylib, string outputTbd)
    {
        XcRunTool($"tapi stubify --filetype=tbd-v4 {inputDylib} -o {outputTbd}");
    }

    void CreateXcframeworks(ApplePlatform platform, bool includeDevice, AbsolutePath simBuildDir)
    {
        Log.Information("=== Creating xcframeworks ===");

        var depSimFw = simBuildDir / $"{DepModuleName}.framework";
        var mainSimFw = simBuildDir / $"{ModuleName}.framework";

        // Dependency xcframework
        if (Directory.Exists(BtDepXcframeworkDir))
            BtDepXcframeworkDir.DeleteDirectory();

        var depSettings = new CreateXcframeworkSettings()
            .AddFrameworkPath(depSimFw)
            .SetOutputPath(BtDepXcframeworkDir);
        if (includeDevice)
        {
            var depDeviceFw = BtBuildDir / platform.DeviceSliceId! / $"{DepModuleName}.framework";
            depSettings.AddFrameworkPath(depDeviceFw);
        }
        XcodeBuild.ExecuteCreateXcframework(depSettings);

        // Main xcframework
        if (Directory.Exists(BtXcframeworkDir))
            BtXcframeworkDir.DeleteDirectory();

        var mainSettings = new CreateXcframeworkSettings()
            .AddFrameworkPath(mainSimFw)
            .SetOutputPath(BtXcframeworkDir);
        if (includeDevice)
        {
            var mainDeviceFw = BtBuildDir / platform.DeviceSliceId! / $"{ModuleName}.framework";
            mainSettings.AddFrameworkPath(mainDeviceFw);
        }
        XcodeBuild.ExecuteCreateXcframework(mainSettings);
    }

    IReadOnlyList<string> GetDepSourceFiles()
    {
        var depSourceDir = BindingTestsDir / "Sources" / "SwiftBindingsTestLibDependency";
        return Directory.GetFiles(depSourceDir, "*.swift", SearchOption.AllDirectories).ToList();
    }

    IReadOnlyList<string> GetMainSourceFiles()
    {
        // Matches the exclusions in Package.swift and build-xcframework.sh
        var sourceDir = BindingTestsDir / "Sources" / "SwiftBindingsTestLib";
        var files = Directory.GetFiles(sourceDir, "*.swift", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".disabled"))
            .Where(f => !f.EndsWith("Closures/Autoclosures.swift"))
            .Where(f => !f.EndsWith("UnsafeTypes/Span.swift"))
            .Where(f => !f.EndsWith("UnsafeTypes/PointerGenerics.swift"))
            .Where(f => !f.EndsWith("Foundation/Date.swift"))
            .ToList();
        return files;
    }

    // ============================================================
    // RegenerateBindings target — ports regenerate-bindings.sh
    // ============================================================

    Target RegenerateBindings => _ => _
        .DependsOn(BuildXcframework)
        .Executes(() => RunRegenerateBindings(Strict || ForceStrict));

    void RunRegenerateBindings(bool strict)
    {
        Log.Information("=== Regenerating bindings for {Module} ===", ModuleName);

        // Ensure generator is built
        EnsureGeneratorBuilt();

        // Clean output
        if (Directory.Exists(BtOutputDir))
            ((AbsolutePath)BtOutputDir).DeleteDirectory();
        BtOutputDir.CreateDirectory();

        // Build generator arguments
        var genArgs = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{BtXcframeworkDir}\"",
            $"-o \"{BtOutputDir}\"",
            $"--async-library {WrapperModule}",
        };

        if (Directory.Exists(BtSymbolgraphDir))
        {
            var sgCount = Directory.GetFiles(BtSymbolgraphDir, "*.symbols.json").Length;
            if (sgCount > 0)
            {
                genArgs.Add($"--symbolgraph \"{BtSymbolgraphDir}\"");
                Log.Information("Symbol graph: {Dir} ({Count} files)", BtSymbolgraphDir, sgCount);
            }
        }

        if (Directory.Exists(BtDepXcframeworkDir))
        {
            genArgs.Add($"--framework-dependency \"{BtDepXcframeworkDir}\"");
            Log.Information("Dependency xcframework: {Dir}", BtDepXcframeworkDir);
        }

        // Run generator (may exit non-zero for unsupported features)
        var genProcess = ProcessTasks.StartProcess(
            "dotnet", string.Join(" ", genArgs),
            workingDirectory: BindingTestsDir,
            logOutput: false);
        genProcess.WaitForExit();
        var exitCode = genProcess.ExitCode;

        // Save exit code for downstream scripts
        File.WriteAllText(BtOutputDir / "generator-exit-code", exitCode.ToString());

        if (exitCode != 0)
        {
            Log.Warning("Generator exited with code {ExitCode}", exitCode);
            if (strict)
                throw new Exception($"Generator exited with code {exitCode} (strict mode)");
            Log.Information("This is expected if the test library includes features beyond current generator support.");
        }

        // Generate dependency module bindings
        if (Directory.Exists(BtDepXcframeworkDir))
        {
            Log.Information("=== Generating dependency bindings for {Module} ===", DepModuleName);
            var depOutputDir = BtOutputDir / "dep";
            depOutputDir.CreateDirectory();

            var depProcess = ProcessTasks.StartProcess(
                "dotnet", $"\"{GeneratorDll}\" --xcframework \"{BtDepXcframeworkDir}\" -o \"{depOutputDir}\"",
                workingDirectory: BindingTestsDir,
                logOutput: false);
            depProcess.WaitForExit();

            if (depProcess.ExitCode != 0)
                Log.Warning("Dependency bindings generation exited with code {Code} (non-fatal)", depProcess.ExitCode);

            // Move the dependency .cs file alongside the main bindings
            var depCsFile = depOutputDir / $"{DepModuleName}.cs";
            if (File.Exists(depCsFile))
            {
                File.Move(depCsFile, BtOutputDir / $"{DepModuleName}.cs", overwrite: true);
                Log.Information("Dependency bindings: {File}", BtOutputDir / $"{DepModuleName}.cs");
            }

            // Preserve the dependency wrapper xcframework for runtime linking
            var depWrapperXcf = depOutputDir / $"{DepModuleName}SwiftBindings.xcframework";
            if (Directory.Exists(depWrapperXcf))
            {
                var destWrapperXcf = BtOutputDir / $"{DepModuleName}SwiftBindings.xcframework";
                if (Directory.Exists(destWrapperXcf))
                    ((AbsolutePath)destWrapperXcf).DeleteDirectory();
                Directory.Move(depWrapperXcf, destWrapperXcf);
                Log.Information("Dependency wrapper: {Dir}", destWrapperXcf);
            }

            // Clean up dep temp directory
            ((AbsolutePath)depOutputDir).DeleteDirectory();
        }

        // Report output
        var csCount = Directory.GetFiles(BtOutputDir, "*.cs", SearchOption.AllDirectories).Length;
        var swiftCount = Directory.GetFiles(BtOutputDir, "*.swift", SearchOption.AllDirectories).Length;
        Log.Information("Generated: {CsCount} C# files, {SwiftCount} Swift wrapper files", csCount, swiftCount);
    }

    void EnsureGeneratorBuilt()
    {
        if (!File.Exists(GeneratorDll))
        {
            Log.Information("Building generator...");
            DotNetBuild(s => s
                .SetProjectFile(GeneratorProject)
                .SetConfiguration("Debug")
                .SetVerbosity(DotNetVerbosity.quiet));
        }
    }

    // ============================================================
    // CompileCheckBindings target
    // ============================================================

    Target CompileCheckBindings => _ => _
        .DependsOn(RegenerateBindings)
        .Executes(() => RunCompileCheck());

    void RunCompileCheck()
    {
        Log.Information("--- Compile-check generated bindings ---");
        DotNetBuild(s => s
            .SetProjectFile(BindingTestsDir / "CompileCheck" / "CompileCheck.csproj")
            .SetConfiguration("Debug")
            .SetVerbosity(DotNetVerbosity.quiet));
        Log.Information("Compile-check passed (0 errors).");
    }

    // ============================================================
    // BuildAsyncWrapper target — ports build-async-wrapper.sh
    // ============================================================

    Target BuildAsyncWrapper => _ => _
        .DependsOn(RegenerateBindings)
        .Executes(() => RunBuildAsyncWrapper());

    void RunBuildAsyncWrapper(ApplePlatform? platformOverride = null, AbsolutePath? outputDirOverride = null)
    {
        var platform = platformOverride ?? ResolvedPlatform;
        var outputDir = outputDirOverride ?? BtOutputDir;
        var sliceId = platform.SimulatorSliceId;
        var xcfwSliceDir = BtXcframeworkDir / sliceId;
        var depXcfwSliceDir = BtDepXcframeworkDir / sliceId;

        // Collect generated Swift wrapper files (exclude SwiftUI bridge)
        var swiftFiles = Directory.GetFiles(outputDir, "*.swift")
            .Where(f => !f.EndsWith(".SwiftUIBridge.swift"))
            .ToList();

        if (swiftFiles.Count == 0)
        {
            Log.Information("No Swift wrapper files found — skipping async wrapper build.");
            return;
        }

        Log.Information("=== Building {Module} async wrapper ===", WrapperModule);
        Log.Information("Platform: {Platform}, Swift wrapper files: {Count}", platform.Name, swiftFiles.Count);

        // Post-process: strip known-broken sections
        Log.Information("Post-processing Swift wrappers...");
        var cleanedDir = outputDir / ".wrapper-build";
        if (Directory.Exists(cleanedDir))
            ((AbsolutePath)cleanedDir).DeleteDirectory();
        cleanedDir.CreateDirectory();

        int totalStripped = 0;
        foreach (var swiftFile in swiftFiles)
        {
            var basename = Path.GetFileName(swiftFile);
            var result = SwiftSourceStripper.StripFile(swiftFile, cleanedDir / basename);
            totalStripped += result.StrippedCount;
            if (result.StrippedCount > 0)
                Log.Debug("Stripped {Count} broken wrapper(s) from {File}", result.StrippedCount, basename);
        }
        File.WriteAllText(outputDir / "wrapper-stripped-count", totalStripped.ToString());

        var cleanedFiles = Directory.GetFiles(cleanedDir, "*.swift").ToList();
        if (cleanedFiles.Count == 0)
        {
            Log.Information("No cleaned Swift files to compile.");
            return;
        }

        // Compile native ARM64 thunk assembly files (if any)
        var thunkObjects = new List<string>();
        foreach (var asmFile in Directory.GetFiles(outputDir, "*.arm64.s"))
        {
            var objFile = Path.ChangeExtension(asmFile, ".o");
            XcRunTool($"clang -c {asmFile} -o {objFile} -target {platform.SimulatorTarget}");
            thunkObjects.Add(objFile);
        }

        // Create output framework structure
        var wrapperXcfDir = outputDir / $"{WrapperModule}.xcframework";
        if (Directory.Exists(wrapperXcfDir))
            ((AbsolutePath)wrapperXcfDir).DeleteDirectory();
        var outputFwDir = wrapperXcfDir / sliceId / $"{WrapperModule}.framework";
        outputFwDir.CreateDirectory();

        var sdkPath = XcRun.GetSdkPath(platform.SimulatorSdkName);

        // Compile with error-based retry
        const int maxRetries = 3;
        int attempt = 0;
        string? compileLog = null;

        while (attempt < maxRetries)
        {
            attempt++;
            var allSourceFiles = cleanedFiles.Concat(thunkObjects).ToList();

            var settings = new SwiftCompilerSettings()
                .SetEmitLibrary()
                .SetTarget(platform.SimulatorTarget)
                .SetSdk(sdkPath)
                .AddFrameworkSearchPath(xcfwSliceDir + "/")
                .SetModuleName(WrapperModule)
                .SetStrictConcurrency("minimal")
                .SetInstallName($"@rpath/{WrapperModule}.framework/{WrapperModule}")
                .SetOutputPath(outputFwDir / WrapperModule)
                .AddSourceFiles(allSourceFiles);

            if (Directory.Exists(depXcfwSliceDir))
                settings.AddFrameworkSearchPath(depXcfwSliceDir + "/");

            var process = SwiftCompiler.Run(settings);
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                Log.Information("Compilation succeeded (after {Attempt} attempt(s), {Stripped} total stripped).",
                    attempt, totalStripped);
                break;
            }

            // Compilation failed — extract errors and strip enclosing functions
            compileLog = string.Join("\n", process.Output.Select(o => o.Text));

            if (attempt == maxRetries)
            {
                var errorLines = compileLog.Split('\n').Where(l => l.Contains("error:")).Take(20);
                Log.Warning("Wrapper compilation failed after {Retries} attempts:", maxRetries);
                foreach (var line in errorLines)
                    Log.Warning("  {Error}", line);
                Log.Information("Continuing without wrapper library (Tier 3 tests will fail).");
                CleanupWrapperBuild(cleanedDir);
                return;
            }

            Log.Information("Compilation attempt {Attempt} failed — stripping broken functions...", attempt);
            var errors = string.Join("\n", compileLog.Split('\n').Where(l => l.Contains("error:")).Take(80));
            int strippedN = SwiftSourceStripper.StripErrorFunctions(cleanedDir, errors);

            if (strippedN == 0)
            {
                Log.Warning("No strippable functions found. Build error may be structural.");
                CleanupWrapperBuild(cleanedDir);
                return;
            }

            totalStripped += strippedN;
            // Refresh cleaned files list (some may have been modified)
            cleanedFiles = Directory.GetFiles(cleanedDir, "*.swift").ToList();
            Log.Information("Retrying compilation...");
        }

        // Clean up temporary build directory
        CleanupWrapperBuild(cleanedDir);

        // Create framework Info.plist
        PlistGenerator.WriteFrameworkPlist(
            outputFwDir / "Info.plist",
            $"com.swiftbindings.{WrapperModule}", WrapperModule, WrapperModule,
            platform.MinOsVersion, platform.SimulatorPlistPlatform);

        // Create xcframework Info.plist
        WriteXcframeworkPlist(wrapperXcfDir / "Info.plist", WrapperModule, sliceId, platform);

        Log.Information("{Module} async wrapper framework built successfully", WrapperModule);
    }

    // ============================================================
    // BuildBridge target — ports build-bridge.sh
    // ============================================================

    Target BuildBridge => _ => _
        .DependsOn(RegenerateBindings)
        .Executes(() => RunBuildBridge());

    void RunBuildBridge(string target = "simulator")
    {
        var platform = ResolvedPlatform;
        string sliceId, sdkName, targetTriple, plistPlatform;

        if (target == "device" && platform.HasDeviceSlice)
        {
            sliceId = platform.DeviceSliceId!;
            sdkName = platform.DeviceSdkName!;
            targetTriple = platform.DeviceTarget!;
            plistPlatform = platform.DevicePlistPlatform!;
        }
        else
        {
            sliceId = platform.SimulatorSliceId;
            sdkName = platform.SimulatorSdkName;
            targetTriple = platform.SimulatorTarget;
            plistPlatform = platform.SimulatorPlistPlatform;
        }

        var generatedBridge = BtOutputDir / $"{ModuleName}.SwiftUIBridge.swift";
        var testHelpers = BindingTestsDir / "SwiftBridge" / "SwiftUIBridgeTestHelpers.swift";
        var xcfwSliceDir = BtXcframeworkDir / sliceId;
        var depXcfwSliceDir = BtDepXcframeworkDir / sliceId;

        if (!File.Exists(generatedBridge))
        {
            Log.Information("No generated bridge file found — skipping bridge build.");
            return;
        }

        // Smoke check: verify expected @_cdecl entrypoints
        Log.Information("Verifying generated bridge shape...");
        var bridgeContent = File.ReadAllText(generatedBridge);
        var expectedSymbols = new[]
        {
            $"SBW_{ModuleName}_EnumParamView_Create",
            $"SBW_{ModuleName}_EnumParamView_GetViewController",
            $"SBW_{ModuleName}_EnumParamView_Free",
        };

        foreach (var sym in expectedSymbols)
        {
            if (!bridgeContent.Contains(sym))
                throw new Exception($"Expected @_cdecl entrypoint not found: {sym}. The generated bridge shape has changed.");
        }
        Log.Information("Bridge shape verified.");

        // Determine output directory
        string outputDir;
        if (target == "device")
            outputDir = BindingTestsDir / "SwiftBridge" / "device" / $"{BridgeModule}.framework";
        else
            outputDir = BindingTestsDir / "SwiftBridge" / $"{BridgeModule}.framework";

        Directory.CreateDirectory(outputDir);

        var sdkPath = XcRun.GetSdkPath(sdkName);

        // Build source list
        var sources = new List<string> { generatedBridge };
        if (File.Exists(testHelpers))
        {
            sources.Add(testHelpers);
            Log.Information("Compiling generated bridge + test helpers...");
        }
        else
        {
            Log.Information("Compiling generated bridge (no test helpers)...");
        }

        var settings = new SwiftCompilerSettings()
            .SetEmitLibrary()
            .SetTarget(targetTriple)
            .SetSdk(sdkPath)
            .AddFrameworkSearchPath(xcfwSliceDir + "/")
            .SetModuleName(BridgeModule)
            .SetInstallName($"@rpath/{BridgeModule}.framework/{BridgeModule}")
            .SetOutputPath(Path.Combine(outputDir, BridgeModule))
            .AddSourceFiles(sources);

        if (Directory.Exists(depXcfwSliceDir))
            settings.AddFrameworkSearchPath(depXcfwSliceDir + "/");

        SwiftCompiler.Execute(settings);

        // Create Info.plist
        PlistGenerator.WriteFrameworkPlist(
            Path.Combine(outputDir, "Info.plist"),
            $"com.swiftbindings.{BridgeModule}", BridgeModule, BridgeModule,
            platform.MinOsVersion, plistPlatform);

        Log.Information("{Module} framework built successfully", BridgeModule);
    }

    // ============================================================
    // Aggregate targets
    // ============================================================

    Target BindingTests => _ => _
        .DependsOn(CompileCheckBindings, BuildAsyncWrapper, BuildBridge)
        .Executes(() =>
        {
            ReportBindingTestResults();
        });

    Target BindingTestsStrict => _ => _
        .Executes(() =>
        {
            ForceStrict = true;
            RunBuildXcframework();
            RunRegenerateBindings(strict: true);
            RunCompileCheck();
            RunBuildAsyncWrapper();
            RunBuildBridge();
            ReportBindingTestResults();
        });

    void ReportBindingTestResults()
    {
        Log.Information("=========================================");
        Log.Information(" Results");
        Log.Information("=========================================");

        // Count generated files
        if (Directory.Exists(BtOutputDir))
        {
            var csCount = Directory.GetFiles(BtOutputDir, "*.cs", SearchOption.AllDirectories).Length;
            var swiftCount = Directory.GetFiles(BtOutputDir, "*.swift", SearchOption.AllDirectories).Length;
            Log.Information("Generated files: {CsCount} C# files, {SwiftCount} Swift wrapper files", csCount, swiftCount);
        }

        // Show binding report if it exists
        var reportPath = BtOutputDir / "binding-report.json";
        if (File.Exists(reportPath))
            Log.Information("Binding report: {Path}", reportPath);

        Log.Information("=========================================");
        Log.Information(" Done");
        Log.Information("=========================================");
    }

    // ============================================================
    // Helpers
    // ============================================================

    void WriteXcframeworkPlist(string outputPath, string moduleName, string sliceId, ApplePlatform platform)
    {
        var variantXml = platform.SimulatorPlistVariant != null
            ? $@"
            <key>SupportedPlatformVariant</key>
            <string>{platform.SimulatorPlistVariant}</string>"
            : "";

        var content = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
                    <dict>
                        <key>LibraryIdentifier</key>
                        <string>{sliceId}</string>
                        <key>LibraryPath</key>
                        <string>{moduleName}.framework</string>
                        <key>SupportedArchitectures</key>
                        <array>
                            <string>arm64</string>
                        </array>
                        <key>SupportedPlatform</key>
                        <string>{platform.SupportedPlatform}</string>{variantXml}
                    </dict>
                </array>
                <key>CFBundlePackageType</key>
                <string>XFWK</string>
                <key>XCFrameworkFormatVersion</key>
                <string>1.0</string>
            </dict>
            </plist>
            """;
        File.WriteAllText(outputPath, content);
    }

    static void CleanupWrapperBuild(string cleanedDir)
    {
        if (Directory.Exists(cleanedDir))
            Directory.Delete(cleanedDir, recursive: true);
    }
}
