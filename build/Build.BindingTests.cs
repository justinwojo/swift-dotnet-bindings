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

    [Parameter("Fail on non-zero generator exit")] readonly bool Strict;

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
        .After(Clean, Fetch, ValidateAppleTypesManifest)
        .Executes(() => RunBuildXcframework());

    void RunBuildXcframework(ApplePlatform? platformOverride = null, bool? includeDeviceOverride = null)
    {
        var platform = platformOverride ?? ResolvedPlatform;
        var sdkPath = XcRun.GetSdkPath(platform.SimulatorSdkName);
        var simBuildDir = BtBuildDir / platform.SimulatorSliceId;
        var frameworkDir = simBuildDir / $"{ModuleName}.framework";
        var depFrameworkDir = simBuildDir / $"{DepModuleName}.framework";

        // Derive the active smoke-flag Swift defines once here so every
        // CompileModuleSlice invocation below — main + dependency, simulator +
        // device, for whichever platform override we're running — sees the same
        // set. Passing this independently per call site is how `#if FOO_SMOKE`
        // fixtures stay consistent across the dylib compile and the ABI JSON
        // dump, which is a hard prerequisite for any smoke test to work.
        var swiftDefines = GetActiveSmokeFlags().Select(f => f.Define).ToList();

        Log.Information("=== Building {Module} ===", ModuleName);
        Log.Information("Platform: {Platform}, Target: {Target}", platform.Name, platform.SimulatorTarget);
        if (swiftDefines.Count > 0)
            Log.Information("Active smoke defines: {Defines}", string.Join(", ", swiftDefines));

        // macOS ignores --include-device
        var requestedIncludeDevice = includeDeviceOverride ?? IncludeDevice;
        var includeDevice = requestedIncludeDevice && platform.HasDeviceSlice;
        if (requestedIncludeDevice && !platform.HasDeviceSlice)
            Log.Information("Note: --include-device ignored for {Platform} (no device slice)", platform.Name);

        // Clean previous build
        BtBuildDir.DeleteDirectory();
        simBuildDir.CreateDirectory();

        // --- Build dependency module ---
        CompileModuleSlice(
            DepModuleName, platform.SimulatorTarget, sdkPath,
            platform.SimulatorModuleSuffix, platform.MinOsVersion, platform.SimulatorPlistPlatform,
            depFrameworkDir, GetDepSourceFiles(), frameworkSearchPaths: null,
            swiftDefines: swiftDefines);

        // --- Build main module ---
        var mainSourceFiles = GetMainSourceFiles();
        CompileModuleSlice(
            ModuleName, platform.SimulatorTarget, sdkPath,
            platform.SimulatorModuleSuffix, platform.MinOsVersion, platform.SimulatorPlistPlatform,
            frameworkDir, mainSourceFiles, frameworkSearchPaths: new[] { simBuildDir.ToString() },
            swiftDefines: swiftDefines);

        // --- Extract symbol graph ---
        ExtractSymbolGraph(platform, sdkPath, simBuildDir);

        // --- Device slice (optional, iOS/tvOS only) ---
        if (includeDevice)
            BuildDeviceSlices(platform, mainSourceFiles, swiftDefines);

        // --- Create xcframeworks ---
        CreateXcframeworks(platform, includeDevice, simBuildDir);

        Log.Information("=== Build Complete ===");
        Log.Information("xcframeworks: {Main}, {Dep}", BtXcframeworkDir, BtDepXcframeworkDir);
    }

    /// <summary>
    /// Compiles a single framework slice (simulator or device) for a given module.
    /// Produces dylib, swiftmodule, swiftinterface, TBD, ABI JSON, and Info.plist.
    /// </summary>
    /// <param name="swiftDefines">
    /// Conditional-compilation defines (-D) to pass to BOTH the dylib compile
    /// (swiftc) AND the ABI JSON dump (swift-frontend). Must be applied to both
    /// invocations: if the two views disagree, `#if FOO_SMOKE` fixtures either
    /// land in the dylib but aren't visible to the binding generator (missing
    /// wrappers) or show up in the generator output but aren't present in the
    /// dylib at runtime (undefined symbols at load time). The caller is
    /// responsible for deriving this list from the set of enabled smoke flags.
    /// </param>
    void CompileModuleSlice(
        string moduleName, string target, string sdkPath,
        string moduleSuffix, string minOs, string plistPlatform,
        string frameworkDir, IReadOnlyList<string> sourceFiles, string[]? frameworkSearchPaths,
        IReadOnlyList<string>? swiftDefines = null)
    {
        Log.Information("--- Building {Module} ({Target}) ---", moduleName, target);

        var moduleDir = Path.Combine(frameworkDir, "Modules", $"{moduleName}.swiftmodule");
        Directory.CreateDirectory(moduleDir);

        // Compile
        var settings = new SwiftCompilerSettings()
            .SetTarget(target)
            .SetSdk(sdkPath)
            .SetEmitModule()
            .SetEmitLibrary()
            .SetEnableLibraryEvolution()
            .SetEmitModuleInterface()
            .SetModuleName(moduleName)
            .SetInstallName($"@rpath/{moduleName}.framework/{moduleName}")
            .SetOutputPath(Path.Combine(frameworkDir, moduleName))
            .SetModulePath(Path.Combine(moduleDir, $"{moduleSuffix}.swiftmodule"))
            .SetModuleInterfacePath(Path.Combine(moduleDir, $"{moduleSuffix}.swiftinterface"))
            .AddSourceFiles(sourceFiles);

        if (frameworkSearchPaths != null)
            foreach (var path in frameworkSearchPaths)
                settings.AddFrameworkSearchPath(path);

        if (swiftDefines != null)
            foreach (var define in swiftDefines)
            {
                settings.AddExtraArgument("-D");
                settings.AddExtraArgument(define);
            }

        SwiftCompiler.Execute(settings);

        // Copy private swiftinterface (same as public for our purposes)
        File.Copy(
            Path.Combine(moduleDir, $"{moduleSuffix}.swiftinterface"),
            Path.Combine(moduleDir, $"{moduleSuffix}.private.swiftinterface"),
            overwrite: true);

        // Generate TBD
        TapiStubify(
            Path.Combine(frameworkDir, moduleName),
            Path.Combine(moduleDir, $"{moduleName}.tbd"));

        // Generate ABI JSON
        var frontendSettings = new SwiftFrontendSettings()
            .SetSwiftInterfacePath(Path.Combine(moduleDir, $"{moduleSuffix}.swiftinterface"))
            .SetTarget(target)
            .SetModuleName(moduleName)
            .SetSdk(sdkPath)
            .SetAbiDescriptorPath(Path.Combine(moduleDir, $"{moduleSuffix}.abi.json"));

        if (frameworkSearchPaths != null)
            foreach (var path in frameworkSearchPaths)
                frontendSettings.AddFrameworkSearchPath(path);

        if (swiftDefines != null)
            foreach (var define in swiftDefines)
            {
                frontendSettings.AddExtraArgument("-D");
                frontendSettings.AddExtraArgument(define);
            }

        SwiftFrontend.Execute(frontendSettings);

        // Info.plist
        PlistGenerator.WriteFrameworkPlist(
            Path.Combine(frameworkDir, "Info.plist"),
            $"com.test.{moduleName}", moduleName, moduleName,
            minOs, plistPlatform);

        Log.Information("{Module} built: {Dir}", moduleName, frameworkDir);
    }

    void BuildDeviceSlices(
        ApplePlatform platform,
        IReadOnlyList<string> mainSourceFiles,
        IReadOnlyList<string> swiftDefines)
    {
        var deviceSdkPath = XcRun.GetSdkPath(platform.DeviceSdkName!);
        var deviceBuildDir = BtBuildDir / platform.DeviceSliceId!;
        var depDeviceFrameworkDir = deviceBuildDir / $"{DepModuleName}.framework";
        var deviceFrameworkDir = deviceBuildDir / $"{ModuleName}.framework";

        Log.Information("--- Compiling device slice ---");
        Log.Information("Device target: {Target}, SDK: {Sdk}", platform.DeviceTarget, deviceSdkPath);

        deviceBuildDir.CreateDirectory();

        // Build dependency device slice
        CompileModuleSlice(
            DepModuleName, platform.DeviceTarget!, deviceSdkPath,
            platform.DeviceModuleSuffix!, platform.MinOsVersion, platform.DevicePlistPlatform!,
            depDeviceFrameworkDir, GetDepSourceFiles(), frameworkSearchPaths: null,
            swiftDefines: swiftDefines);

        // Build main module device slice
        CompileModuleSlice(
            ModuleName, platform.DeviceTarget!, deviceSdkPath,
            platform.DeviceModuleSuffix!, platform.MinOsVersion, platform.DevicePlistPlatform!,
            deviceFrameworkDir, mainSourceFiles, frameworkSearchPaths: new[] { deviceBuildDir.ToString() },
            swiftDefines: swiftDefines);
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
        .Executes(() => RunRegenerateBindings(Strict));

    void RunRegenerateBindings(bool strict, ApplePlatform? platformOverride = null)
    {
        Log.Information("=== Regenerating bindings for {Module} ===", ModuleName);

        // Ensure generator is built
        EnsureGeneratorBuilt();

        // Clean output
        if (Directory.Exists(BtOutputDir))
            ((AbsolutePath)BtOutputDir).DeleteDirectory();
        BtOutputDir.CreateDirectory();

        // Build generator arguments. --platform threads through the generator so
        // tvOS and Catalyst emit their own TPV-aware csprojs (and skip bindings
        // that don't have availability coverage for the target platform).
        var genArgs = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{BtXcframeworkDir}\"",
            $"-o \"{BtOutputDir}\"",
            $"--async-library {WrapperModule}",
        };
        if (platformOverride != null && platformOverride.Name != "ios")
            genArgs.Add($"--platform {platformOverride.Name}");

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

            // Dependency invocation must mirror the main module's --platform or
            // a tvOS-flavoured main regen will stamp the shared output as tvOS
            // while the dependency bindings are still emitted for the generator
            // default (iOS), producing mixed-platform output under one sidecar.
            var depArgs = new List<string>
            {
                $"\"{GeneratorDll}\"",
                $"--xcframework \"{BtDepXcframeworkDir}\"",
                $"-o \"{depOutputDir}\"",
            };
            if (platformOverride != null && platformOverride.Name != "ios")
                depArgs.Add($"--platform {platformOverride.Name}");

            var depProcess = ProcessTasks.StartProcess(
                "dotnet", string.Join(" ", depArgs),
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

            // Preserve dependency wrapper Swift sources for device build (RunBuildDeviceWrappers).
            // The generator compiles these for simulator; device needs a separate compilation.
            // Also preserve any .arm64.s thunk-assembly files so the device-side dep wrapper
            // build can compile and link them — without these, P/Invokes that point at
            // `thunk_<DepModule>_<hash>` symbols (cross-module inherited methods, etc.) fail
            // with EntryPointNotFoundException at runtime on device.
            var depSwiftFiles = Directory.GetFiles(depOutputDir, "*.swift");
            var depAsmFiles = Directory.GetFiles(depOutputDir, "*.arm64.s");
            if (depSwiftFiles.Length > 0)
            {
                var depSwiftDir = BtOutputDir / "dep-swift";
                if (Directory.Exists(depSwiftDir))
                    ((AbsolutePath)depSwiftDir).DeleteDirectory();
                depSwiftDir.CreateDirectory();
                foreach (var sf in depSwiftFiles)
                    File.Copy(sf, depSwiftDir / Path.GetFileName(sf));
                foreach (var af in depAsmFiles)
                    File.Copy(af, depSwiftDir / Path.GetFileName(af));
                Log.Information("Preserved {Count} dependency wrapper Swift source file(s) and {AsmCount} thunk-assembly file(s) for device build.",
                    depSwiftFiles.Length, depAsmFiles.Length);
            }

            // Clean up dep temp directory
            ((AbsolutePath)depOutputDir).DeleteDirectory();
        }

        // Report output
        var csCount = Directory.GetFiles(BtOutputDir, "*.cs", SearchOption.AllDirectories).Length;
        var swiftCount = Directory.GetFiles(BtOutputDir, "*.swift", SearchOption.AllDirectories).Length;
        Log.Information("Generated: {CsCount} C# files, {SwiftCount} Swift wrapper files", csCount, swiftCount);

        // Stamp the active smoke-flag set so AssertBindingsNotStale can
        // detect flag-set drift under a later --skip-regen run.
        StampSmokeFlagsSidecar(BtOutputDir);

        // Same idea on a different axis: stamp the platform so a later
        // --skip-regen run across platform boundaries (iOS regen then tvOS
        // --skip-regen) is rejected instead of silently reusing mismatched
        // bindings.
        StampTargetPlatformSidecar(BtOutputDir, platformOverride ?? ApplePlatform.IOS);
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
        .After(CompileCheckBindings)
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
        .After(BuildAsyncWrapper)
        .Executes(() => RunBuildBridge());

    void RunBuildBridge(string target = "simulator", ApplePlatform? platformOverride = null)
    {
        var platform = platformOverride ?? ResolvedPlatform;
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

    // Single entry point for the BindingTests pipeline. Flags pick between the compile
    // gate and one-or-more runtime gates; platforms compose. The .After() list on the
    // pipeline Targets orders them under Nuke --strict's global topo-sort; we call them
    // imperatively, so without these edges they'd be orphan sinks and --strict would
    // reject the plan.
    Target BindingTests => _ => _
        .After(Clean, Test, BuildBridge, BuildAsyncWrapper, CompileCheckBindings)
        .Executes(() =>
        {
            RejectSkipBuildWithActiveSmokeFlags();

            // --compile-only: run the binding pipeline + compile-check only (no app build,
            // no test execution). This is the CI gate — "does the generator emit valid C#?"
            if (CompileOnly)
            {
                if (Sim || Device || Macos || Catalyst || Tvos)
                    Log.Warning("Platform flags are ignored when --compile-only is set");

                RunBuildXcframework();
                RunRegenerateBindings(strict: Strict);
                RunCompileCheck();
                RunBuildAsyncWrapper();
                RunBuildBridge();
                ReportBindingTestResults();
                return;
            }

            // Runtime gate: default to --sim when no platform flag is set.
            var anyPlatform = Sim || Device || Macos || Catalyst || Tvos;
            var runSim = Sim || !anyPlatform;

            if (runSim)   RunSimulatorPlatform();
            if (Device)   RunDevicePlatform();
            if (Macos)    RunMacOSPlatform();
            if (Catalyst) RunCatalystPlatform();
            if (Tvos)     RunTvOSSimulatorPlatform();
        });

    // ValidateBlastRadius runs the blast-radius smoke script and fails the build if any of
    // the three committed golden diffs (otool-L, nm, strings-swift) diverges from HEAD.
    // This is the automated gate behind the invariant that pulling SwiftBindings.Apple into
    // a consumer adds zero new `-framework` link lines and zero new Swift ABI symbols
    // compared to a Swift.Runtime-only baseline. The raw script exits 0 regardless, so the
    // pass/fail lives here. Diff header lines are normalized out so filename/timestamp noise
    // does not trigger spurious failures. On a clean pass the working tree is restored to
    // HEAD so the gate has no side effects; on failure the freshly-generated measurement
    // files are left in place for inspection.
    Target ValidateBlastRadius => _ => _
        .After(BindingTests)
        .Executes(() =>
        {
            var measurementsDir = BindingTestsDir / "BlastRadius.Baseline" / "measurements";
            var script = BindingTestsDir / "BlastRadius.Baseline" / "measure-blast-radius.sh";
            var gates = new[] { "otool-L.diff", "nm.diff", "strings-swift.diff" };

            if (!File.Exists(script))
                throw new Exception($"Blast-radius script not found at {script}");

            // Snapshot every measurement artifact BEFORE the script overwrites them so we
            // can restore the working tree on a clean pass. Also captures the gate goldens
            // for the regression check.
            var measurementFiles = Directory.Exists(measurementsDir)
                ? Directory.GetFiles(measurementsDir).Select(Path.GetFileName).ToArray()
                : Array.Empty<string>();
            var snapshots = measurementFiles.ToDictionary(
                name => name!,
                name => File.ReadAllBytes(measurementsDir / name!));

            foreach (var gate in gates)
            {
                if (!snapshots.ContainsKey(gate))
                    throw new Exception($"Expected golden diff missing: {measurementsDir / gate}. Commit the baseline output before gating.");
            }

            Log.Information("Running blast-radius measurement script...");
            var proc = ProcessTasks.StartProcess(
                "bash", $"\"{script}\"",
                workingDirectory: RootDirectory,
                logOutput: true);
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new Exception($"measure-blast-radius.sh exited with code {proc.ExitCode}");

            var regressions = new List<string>();
            foreach (var name in gates)
            {
                var snapshot = NormalizeDiffOutput(System.Text.Encoding.UTF8.GetString(snapshots[name]));
                var fresh = NormalizeDiffOutput(File.ReadAllText(measurementsDir / name));
                if (!string.Equals(snapshot, fresh, StringComparison.Ordinal))
                    regressions.Add(name);
            }

            if (regressions.Count > 0)
            {
                foreach (var name in regressions)
                {
                    var snapshot = NormalizeDiffOutput(System.Text.Encoding.UTF8.GetString(snapshots[name]));
                    var fresh = NormalizeDiffOutput(File.ReadAllText(measurementsDir / name));
                    Log.Error("=== {Name}: committed (HEAD) ===", name);
                    Log.Error("{Content}", snapshot);
                    Log.Error("=== {Name}: fresh (working tree) ===", name);
                    Log.Error("{Content}", fresh);
                }
                var message = "Blast-radius regression detected. The following committed goldens diverged:\n  - "
                    + string.Join("\n  - ", regressions)
                    + "\nInspect the working-tree copies under BindingTests/BlastRadius.Baseline/measurements/."
                    + "\nIf the change is intentional, review the added linkage and update the committed diffs.";
                throw new Exception(message);
            }

            // Clean pass — restore the measurement directory so the working tree matches HEAD.
            // (The script rewrites timestamped diff headers and binary-path headers even on a
            // zero-regression run.)
            foreach (var (name, content) in snapshots)
                File.WriteAllBytes(measurementsDir / name, content);
            Log.Information("Blast-radius gate passed: otool-L.diff, nm.diff, strings-swift.diff match HEAD.");
        });

    static string NormalizeDiffOutput(string diff)
    {
        // `diff -u` emits `--- path\ttimestamp` and `+++ path\ttimestamp` as the first two
        // header lines. The absolute path and timestamp shift between machines/runs even
        // when the semantic diff is identical, so we strip them before comparing.
        var lines = diff.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal) ||
                line.StartsWith("+++ ", StringComparison.Ordinal))
                continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

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
