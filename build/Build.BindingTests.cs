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

    // --compile-only is the CI gate. By default, every catastrophic generator/wrapper
    // failure mode in that path is fatal — generator exit, dependency-gen exit, and
    // wrapper compilation give-up (single-shot now; the wrapper-strip gate fails closed
    // when the generator's post-processor strips MORE than the committed baseline).
    // --permissive opts out for local exploration where the intent is "what survives"
    // rather than "did anything regress?". Has no effect outside the --compile-only
    // branch (other paths already throw on their own failures). Implies --strict in
    // compile-only mode.
    [Parameter("Allow non-fatal failures in --compile-only (generator exit, dep-gen, wrapper give-up)")]
    readonly bool Permissive;

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

        // Ensure generator is built (also stages the SwiftInterfaceParser host binary it needs).
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

        // Finding 50: in strict mode (explicit --strict or --compile-only's fail-closed
        // default), make the generator fail-closed on a degraded input edge — a device→sim
        // slice fallback, a missing swiftinterface, an ABI-JSON fallback, an ambiguous TBD,
        // or a degraded auto-detected dependency. Mirrors how `strict` already escalates a
        // non-zero generator exit to a thrown build error below.
        if (strict)
            genArgs.Add("--strict-inputs");

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
            // The generator runs with logOutput:false so a passing run stays quiet, but that also
            // hides WHY it failed (a --strict-inputs SWIFTBIND027 degradation, an exception, a
            // wrapper-compile abort). StartProcess still captures the stream, so replay it here on
            // failure — without this, a CI failure at this gate is opaque (the reason never reaches
            // the Actions log).
            foreach (var line in genProcess.Output)
                Log.Information("[generator] {Text}", line.Text);
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
            if (strict)
                depArgs.Add("--strict-inputs");

            var depProcess = ProcessTasks.StartProcess(
                "dotnet", string.Join(" ", depArgs),
                workingDirectory: BindingTestsDir,
                logOutput: false);
            depProcess.WaitForExit();

            if (depProcess.ExitCode != 0)
            {
                Log.Warning("Dependency bindings generation exited with code {Code}", depProcess.ExitCode);
                foreach (var line in depProcess.Output)
                    Log.Information("[dep-generator] {Text}", line.Text);
                if (strict)
                    throw new Exception($"Dependency bindings generator exited with code {depProcess.ExitCode} (strict mode)");
            }

            // Move the dependency .cs file alongside the main bindings
            var depCsFile = depOutputDir / $"{DepModuleName}.cs";
            if (File.Exists(depCsFile))
            {
                File.Move(depCsFile, BtOutputDir / $"{DepModuleName}.cs", overwrite: true);
                Log.Information("Dependency bindings: {File}", BtOutputDir / $"{DepModuleName}.cs");
            }

            // Move the dependency API manifest alongside the main one so the api-manifest gate
            // ratchets the dependency module's ABI too. Without this, the manifest is deleted with
            // depOutputDir below and a symbol retarget in the dependency binding goes unnoticed
            // (the gate scans only BtOutputDir's top-level *.api-manifest.json).
            var depManifest = depOutputDir / $"{DepModuleName}.api-manifest.json";
            if (File.Exists(depManifest))
            {
                File.Move(depManifest, BtOutputDir / $"{DepModuleName}.api-manifest.json", overwrite: true);
                Log.Information("Dependency API manifest: {File}", BtOutputDir / $"{DepModuleName}.api-manifest.json");
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
                // Preserve the dep's wrapper-context.json so the device-dep wrapper build can scrub
                // with the dependency module's own internalTypeNames (depOutputDir is deleted below).
                var depContext = depOutputDir / "wrapper-context.json";
                if (File.Exists(depContext))
                    File.Copy(depContext, depSwiftDir / "wrapper-context.json");
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

    void EnsureGeneratorBuilt(bool ensureSwiftInterfaceParser = true)
    {
        // The generator hard-fails ("Could not locate the SwiftInterfaceParser host binary") when
        // the parser is absent AND the input carries a `.swiftinterface`. The binding-tests and
        // runtime regen paths that feed a `.swiftinterface`/`--xcframework` all funnel through this
        // method, so staging the parser here is the single chokepoint that covers them (binding-tests
        // sim/device/tvos via RunRegenerateBindings, macOS/Catalyst via RunRegenerateMacOSBindings,
        // the manual `-s` runtime path). Done before the generator-dll freshness early-return below
        // so a fresh dll paired with a missing parser still rebuilds the parser. Generator modes
        // that consume no `.swiftinterface` (the stdlib-conformances ABI-dump pass, the
        // apple-types-manifest probe) pass ensureSwiftInterfaceParser: false to skip a build they
        // don't need.
        if (ensureSwiftInterfaceParser)
            EnsureSwiftInterfaceParserBuilt();

        // Freshness check, not existence check. A stale generator dll — source edited but
        // the dll not rebuilt — would otherwise be trusted unconditionally, and every
        // binding-tests gate would silently run the OLD generator (the recurring
        // "stale generator binary masks your edit" footgun). Reuse the same SHA-fingerprint
        // guard Validation already uses (ComputeSourceFingerprint + a .build-stamp) so that
        // editing generator source and running ANY nuke binding-tests variant rebuilds the
        // generator. Keep the generator-only build here — not Validation's heavier
        // generator+runtime+supplement rebuild — so a BindingTests inner-loop run does not
        // pay for a runtime/supplement rebuild it doesn't need. The stamp lives next to the
        // dll so a clean that wipes the dll also wipes the stamp (fail-safe: rebuild).
        var buildStamp = GeneratorDll.Parent / ".bindingtests-generator-stamp";
        var fingerprint = ComputeSourceFingerprint();
        if (File.Exists(GeneratorDll) &&
            File.Exists(buildStamp) &&
            File.ReadAllText(buildStamp).Trim() == fingerprint)
        {
            return;
        }

        Log.Information("Building generator (source changed or dll missing)...");
        DotNetBuild(s => s
            .SetProjectFile(GeneratorProject)
            .SetConfiguration("Debug")
            .SetVerbosity(DotNetVerbosity.quiet));

        Directory.CreateDirectory(buildStamp.Parent);
        File.WriteAllText(buildStamp, fingerprint);
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

    // Returns true on success or no-op (no Swift wrapper files). Returns false when the
    // single-shot compile of the post-processed wrapper fails — the generator's own
    // SwiftWrapperPostProcessor already scrubbed it, so there is no strip-and-retry fallback.
    // The --compile-only fail-closed gate reads this; existing callers ignore it
    // because their downstream Tier 3 tests will surface the failure anyway.
    bool RunBuildAsyncWrapper(ApplePlatform? platformOverride = null, AbsolutePath? outputDirOverride = null)
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
            return true;
        }

        Log.Information("=== Building {Module} async wrapper ===", WrapperModule);
        Log.Information("Platform: {Platform}, Swift wrapper files: {Count}", platform.Name, swiftFiles.Count);

        // Post-process with the generator's OWN scrub oracle (not a bespoke harness stripper):
        // SwiftWrapperPostProcessor.Process, reading the persisted internalTypeNames, exactly as
        // the generator-own wrapper compile does — so this wrapper matches it by construction.
        Log.Information("Post-processing Swift wrappers (shared generator oracle)...");
        var cleanedDir = outputDir / ".wrapper-build";
        if (Directory.Exists(cleanedDir))
            ((AbsolutePath)cleanedDir).DeleteDirectory();
        cleanedDir.CreateDirectory();

        var internalTypeNames = LoadInternalTypeNames(outputDir / "wrapper-context.json");
        var manifest = RunWrapperPostProcess(swiftFiles, cleanedDir, internalTypeNames, ModuleName, platform.Name);
        File.WriteAllText(outputDir / "wrapper-stripped-count", manifest.StrippedBlockTotal.ToString());
        manifest.Save(outputDir / "wrapper-strip-manifest.json");

        // Fail closed if the generator emitted MORE uncompilable wrappers than the committed
        // baseline allows — a NEW emitter defect, never a reason for the harness to strip more.
        EnforceWrapperStripTripwire(manifest, LoadWrapperStripBaseline(), platform.Name);

        var cleanedFiles = Directory.GetFiles(cleanedDir, "*.swift").ToList();
        if (cleanedFiles.Count == 0)
        {
            Log.Information("No cleaned Swift files to compile.");
            return true;
        }

        // Compile the native thunk assembly files for this platform's CPU arch.
        // The generator emits both {ns}.arm64.s and {ns}.x86_64.s; pick the set
        // matching SliceArchitecture so the x86_64 cells compile the SysV thunks
        // and the arm64 cells compile the AAPCS64 ones, each with its own triple.
        var thunkObjects = new List<string>();
        foreach (var asmFile in Directory.GetFiles(outputDir, $"*.{platform.SliceArchitecture}.s"))
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

        // Single-shot compile. The wrapper is the generator's output scrubbed by the SAME
        // SwiftWrapperPostProcessor the generator-own compile uses, so there is no strip-and-retry
        // fallback — a compile failure is a generator/emitter defect to fix at emission.
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

        if (process.ExitCode != 0)
        {
            var compileLog = string.Join("\n", process.Output.Select(o => o.Text));
            var errorLines = compileLog.Split('\n').Where(l => l.Contains("error:")).Take(20);
            Log.Warning("Wrapper compilation failed:");
            foreach (var line in errorLines)
                Log.Warning("  {Error}", line);
            Log.Information("Continuing without wrapper library (Tier 3 tests will fail).");
            CleanupWrapperBuild(cleanedDir);
            return false;
        }

        Log.Information("Compilation succeeded.");

        // Clean up temporary build directory
        CleanupWrapperBuild(cleanedDir);

        // Create framework Info.plist
        PlistGenerator.WriteFrameworkPlist(
            outputFwDir / "Info.plist",
            $"com.swiftbindings.{WrapperModule}", WrapperModule, WrapperModule,
            platform.MinOsVersion, platform.SimulatorPlistPlatform);

        // Create xcframework Info.plist
        WriteXcframeworkPlist(wrapperXcfDir / "Info.plist", WrapperModule, sliceId, platform);

        // Migration oracle: the harness wrapper we just built must export the identical
        // EveryProtocol witness-getter set as the generator's own strip-free wrapper.
        EnforceWrapperGetterParity(outputDir, WrapperModule, $"{ModuleName}{WrapperModule}", platform.Name);

        Log.Information("{Module} async wrapper framework built successfully", WrapperModule);
        return true;
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

            // The opt-in heavyweight legs (--mixed-pack, --mixed-direct, --appstore-hygiene) and
            // --compile-only are mutually exclusive: --compile-only is a no-app-build compile-check
            // gate, while each opt-in leg builds + consumes/publishes a real app and returns early.
            // The --compile-only early return below would otherwise silently swallow the requested
            // leg. Fail loud rather than skip it.
            if ((MixedPack || MixedDirect || AppstoreHygiene) && CompileOnly)
                throw new Exception(
                    "--mixed-pack/--mixed-direct/--appstore-hygiene and --compile-only cannot be combined: --compile-only is a "
                    + "compile-check gate with no app build or test run, while the opt-in legs build a real app and run/inspect "
                    + "it. Pass exactly one.");

            // The opt-in legs each exercise a DIFFERENT consumption/packaging mode (--mixed-pack: a
            // single packed PackageReference; --mixed-direct: SDK-direct, the app IS the binding;
            // --appstore-hygiene: a device IPA's TN2435 App Store hygiene) and each is a focused,
            // exclusive run that returns early. Combining them would silently run only the first.
            // Fail loud rather than skip one.
            if (new[] { MixedPack, MixedDirect, AppstoreHygiene }.Count(x => x) > 1)
                throw new Exception(
                    "--mixed-pack, --mixed-direct, and --appstore-hygiene cannot be combined: each is a focused, exclusive leg "
                    + "that builds its own app and returns. Pass exactly one.");

            // --compile-only: run the binding pipeline + compile-check only (no app build,
            // no test execution). This is the CI gate — "does the generator emit valid C#?"
            if (CompileOnly)
            {
                if (Sim || Device || Macos || Catalyst || Tvos)
                    Log.Warning("Platform flags are ignored when --compile-only is set");
                if (AbiGrid)
                    Log.Warning("--abi-grid is ignored when --compile-only is set (the grid is a runtime gate)");

                // Fail-closed by default: every generator/wrapper failure mode is fatal
                // unless --permissive opts out. --strict still works as the generator-only
                // knob outside compile-only; here it's implied by the default.
                bool failClosed = !Permissive;

                RunBuildXcframework();
                RunRegenerateBindings(strict: Strict || failClosed);
                RunCompileCheck();

                bool wrapperOk = RunBuildAsyncWrapper();
                if (!wrapperOk && failClosed)
                    throw new Exception(
                        "Wrapper compilation failed (single-shot compile of the post-processed wrapper; no strip-and-retry fallback). Fail-closed in --compile-only; pass --permissive to downgrade.");

                RunBuildBridge();
                ReportBindingTestResults();

                // Cross-artifact parity gate: diff the generated C# against the built
                // Swift libraries (symbol existence, struct-mirror arity, vtable parity)
                // and fail on any NEW divergence vs build/baselines/parity-baseline.json.
                // Runs by default in --compile-only (the host where fresh artifacts exist);
                // fail-closed unless --permissive, consistent with the wrapper-build gate.
                RunParityGate(failClosed);

                // API-manifest ABI-contract gate: diff each generated
                // `{Module}.api-manifest.json` (C# signature → native entry symbol) against
                // build/baselines/api-manifest-baseline.json and fail on any RETARGET (a stable
                // C# signature now binding a different symbol). Runs by default on the
                // compile-only host where fresh manifests exist; fail-closed unless --permissive,
                // consistent with the parity and wrapper-build gates.
                RunApiManifestGate(failClosed);

                // Layer B trend gate: parse skip markers from generated `.cs`
                // and diff against `build/baselines/skip-surface-baseline.json`. Gated on
                // --skip-surface so it runs only when explicitly requested
                // (CI integration-branch gate; bundle worktrees can opt in).
                // Compile-only path is the right host because that's where
                // freshly-generated bindings exist on disk.
                if (SkipSurface)
                    RunSkipSurfaceGate();

                return;
            }

            // --mixed-pack: the opt-in mixed-framework (ObjC + Swift) pack→consume→run
            // gate. Packs a mixed binding into a SINGLE nupkg and consumes it via one
            // PackageReference on iOS sim (Mono JIT) and/or device (NativeAOT) — the
            // platforms where duplicate-ObjC-class registration actually bites and that
            // the macOS-host PackGate cannot stand in for. Focused + exclusive: it does
            // NOT also run the normal RuntimeTestsApp suite. Composes with --sim/--device
            // and defaults to --sim when neither is given. Never part of the default inner
            // loop (needs a booted sim and/or a provisioned device).
            if (MixedPack)
            {
                // iOS-only leg: it composes only with --sim/--device. Warn (don't silently
                // ignore) if a non-iOS platform flag was also passed, mirroring --compile-only.
                if (Macos || MacosX64 || Catalyst || CatalystX64 || Tvos)
                    Log.Warning("--mixed-pack is an iOS-only leg; --macos/--macos-x64/--catalyst/--catalyst-x64/--tvos are ignored (it composes only with --sim/--device).");
                RunMixedPackLeg(sim: Sim || !Device, device: Device);
                return;
            }

            // --mixed-direct: the opt-in SDK-direct mixed-framework gate (consumption path b).
            // Builds a mixed (ObjC + Swift) binding where the app's OWN csproj imports
            // SwiftBindings.Sdk and declares <SwiftFramework> — so the app IS the binding project —
            // then builds + runs it on the iOS Simulator and asserts the ObjC type round-trips and
            // its class registers exactly once. This is the runtime gate for _ReferenceMixedObjCCompanion
            // (the companion managed assembly surfaced into the SDK-direct consumer's own compile),
            // the one mixed-consumption mode neither --mixed-pack (path a) nor the macOS PackGate
            // exercises. Sim-only by design: the native single-registration question is already
            // device-proven by --mixed-pack, and the new surface here (companion <Reference> injection)
            // is a compile/copy-local concern fully observed on the Mono-JIT simulator runtime.
            if (MixedDirect)
            {
                if (Device || Macos || MacosX64 || Catalyst || CatalystX64 || Tvos)
                    Log.Warning("--mixed-direct is a sim-only leg; --device/--macos/--macos-x64/--catalyst/--catalyst-x64/--tvos are ignored.");
                RunMixedDirectLeg();
                return;
            }

            // --appstore-hygiene: the opt-in App Store TN2435-hygiene gate (issue #42). Packs the
            // Runtime, publishes a device IPA through a single-PackageReference consumer, and asserts
            // the runtime embeds as a signed SwiftBindingsRuntime.framework (not a loose dylib), the
            // app embeds zero libswift*.dylib, and no SwiftSupport/ folder is present. Builds +
            // inspects on the host (a code-signing identity is required, but no connected device or
            // simulator), so it composes with no platform flag.
            if (AppstoreHygiene)
            {
                if (Sim || Device || Macos || MacosX64 || Catalyst || CatalystX64 || Tvos)
                    Log.Warning("--appstore-hygiene builds + inspects a device IPA on the host; platform flags are ignored.");
                RunAppStoreHygieneLeg();
                return;
            }

            // Runtime gate: default to --sim when no platform flag is set.
            var anyPlatform = Sim || Device || Macos || MacosX64 || Catalyst || CatalystX64 || Tvos;
            var runSim = Sim || !anyPlatform;

            // The ABI grid grades sim+device together: each platform run stashes its results
            // (StashAbiGridResults, inside ReportRuntimeTestResult), then one merged grid is
            // rendered + gated here, after the loop. The try/finally renders the grid even if a
            // platform's runtime tests throw (so a partial grid is still visible on failure); the
            // gate itself is enforced only on the success path — when a platform's tests fail the
            // build already fails with that verdict, and the grid throw must not mask it.
            try
            {
                if (runSim)     RunSimulatorPlatform();
                if (Device)     RunDevicePlatform();
                if (Macos)      RunMacOSPlatform();
                if (MacosX64)   RunMacOSPlatform(ApplePlatform.MacOSX64);
                if (Catalyst)   RunCatalystPlatform();
                if (CatalystX64) RunCatalystPlatform(ApplePlatform.MacCatalystX64);
                if (Tvos)       RunTvOSSimulatorPlatform();
            }
            finally
            {
                if (AbiGrid)
                    _abiGridReport = RunMergedAbiGridReport();
            }

            // Enforce the grid gate on the success path (the try above did not throw). Integrity
            // (rename-rot, malformed manifest) blocks on every run; coverage (an expect-green cell
            // not green on an exercised runtime) blocks only on a full run.
            if (AbiGrid && _abiGridReport != null && _abiGridReport.IsBlocking(_abiGridReport.Partial))
                throw new Exception(
                    $"ABI coverage grid gate failed: {_abiGridReport.BlockingFailureSummary(_abiGridReport.Partial)}");
        });

    /// <summary>Last merged ABI grid report from this BindingTests run; rendered in the finally,
    /// gated on the success path. Field (not local) so the finally and the post-loop gate share it.</summary>
    AbiGridReport? _abiGridReport;

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
                            <string>{platform.SliceArchitecture}</string>
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
