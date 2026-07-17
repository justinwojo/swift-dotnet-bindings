// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Result of compiling the Swift wrapper library.
    /// </summary>
    public sealed class SwiftWrapperCompilationResult
    {
        /// <summary>
        /// Path to the generated xcframework directory.
        /// </summary>
        public required string XCFrameworkPath { get; init; }

        /// <summary>
        /// Number of Swift files that were compiled (after post-processing).
        /// </summary>
        public required int CompiledFileCount { get; init; }

        /// <summary>
        /// Total number of blocks stripped by the post-processor across all files.
        /// </summary>
        public required int StrippedBlockCount { get; init; }

        /// <summary>
        /// Per-sub-cause aggregate counts for the blocks summed in <see cref="StrippedBlockCount"/>.
        /// Lets validation reporting distinguish "the new emission-time gate caught the dominant
        /// case" from "the gate missed everything but happened to reduce total strips."
        /// </summary>
        public IReadOnlyDictionary<StripSubCause, int> StrippedBlocksBySubCause { get; init; }
            = new Dictionary<StripSubCause, int>();

        /// <summary>
        /// Number of architecture slices in the wrapper xcframework (1 = simulator only, 2 = both).
        /// </summary>
        public int SliceCount { get; init; } = 1;

        /// <summary>
        /// Set of @_cdecl / @_silgen_name symbols that were stripped from the wrapper.
        /// Used by the C# stripped-symbol reconciler to suppress P/Invokes targeting these symbols.
        /// </summary>
        public IReadOnlySet<string> StrippedSymbols { get; init; } = new HashSet<string>();
    }

    /// <summary>
    /// Outcome of evaluating a wrapper compilation attempt.
    /// </summary>
    public enum WrapperCompilationOutcome
    {
        /// <summary>Compilation succeeded or was not needed.</summary>
        Success,
        /// <summary>Compilation had issues but asyncLibrary was explicit — non-fatal warning.</summary>
        Warning,
        /// <summary>Compilation failed and asyncLibrary was auto-wired — fatal, must abort.</summary>
        Fatal
    }

    /// <summary>
    /// Compiles generated Swift wrapper files into a {Module}SwiftBindings.xcframework.
    /// Supports single-slice (simulator) and multi-slice (simulator + device) compilation.
    /// </summary>
    public static class SwiftWrapperCompiler
    {
        /// <summary>
        /// Evaluates a compilation result to determine whether the outcome is fatal.
        /// </summary>
        /// <param name="result">The compilation result, or null if no Swift files existed.</param>
        /// <param name="asyncLibraryAutoWired">True if --async-library was auto-set by the generator.</param>
        /// <param name="compilationException">Non-null if Compile() threw an exception.</param>
        public static WrapperCompilationOutcome EvaluateResult(
            SwiftWrapperCompilationResult? result,
            bool asyncLibraryAutoWired,
            Exception? compilationException = null)
        {
            // Exception path: Compile() threw
            if (compilationException != null)
                return asyncLibraryAutoWired ? WrapperCompilationOutcome.Fatal : WrapperCompilationOutcome.Warning;

            // No Swift files — always fine
            if (result == null)
                return WrapperCompilationOutcome.Success;

            // All code stripped
            if (result.CompiledFileCount == 0 && result.StrippedBlockCount > 0)
                return asyncLibraryAutoWired ? WrapperCompilationOutcome.Fatal : WrapperCompilationOutcome.Warning;

            return WrapperCompilationOutcome.Success;
        }

        /// <summary>
        /// Applies SDK-mode outcome adjustment. In SDK mode, Fatal is downgraded to Warning
        /// so that wrapper compilation failures don't kill the entire build — the C# bindings
        /// are still correct, methods referencing the wrapper get DllNotFoundException at runtime.
        /// </summary>
        /// <param name="rawOutcome">The outcome from EvaluateResult().</param>
        /// <param name="sdkMode">True if --sdk-mode was passed.</param>
        public static WrapperCompilationOutcome EffectiveOutcome(
            WrapperCompilationOutcome rawOutcome, bool sdkMode)
        {
            if (rawOutcome == WrapperCompilationOutcome.Fatal && sdkMode)
                return WrapperCompilationOutcome.Warning;
            return rawOutcome;
        }

        /// <summary>
        /// Compiles generated Swift wrapper files into an xcframework (simulator slice only).
        /// Returns null if no Swift files exist in the output directory.
        /// </summary>
        /// <param name="outputDirectory">Directory containing generated Swift wrapper files.</param>
        /// <param name="moduleName">The Swift module name (e.g., "MyModule").</param>
        /// <param name="frameworkSearchPath">The -F flag target (e.g., xcframework slice directory).</param>
        /// <param name="dylibPath">Path to the source framework's dylib (used to locate Info.plist for min OS).</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        /// <param name="platformInfo">Optional platform info; defaults to iOS.</param>
        public static SwiftWrapperCompilationResult? Compile(
            string outputDirectory,
            string moduleName,
            string frameworkSearchPath,
            string dylibPath,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            HashSet<string>? internalTypeNames = null,
            IReadOnlyList<string>? additionalFrameworkSearchPaths = null,
            PlatformInfo? platformInfo = null,
            string? moduleNameForCollision = null,
            HashSet<string>? nestedTypesInCollidingClass = null,
            string? swiftInterfacePath = null,
            bool skipThunkCompilation = false,
            string? resolvedArchitecture = null,
            IReadOnlyList<string>? depModuleNamesForCollision = null,
            IReadOnlyList<string>? linkFrameworks = null,
            IReadOnlyList<string>? linkLibraries = null)
        {
            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            var simSlice = pi.GetSlice(true);
            if (!string.IsNullOrEmpty(resolvedArchitecture))
                simSlice = simSlice.WithArchitecture(resolvedArchitecture);
            return CompileSlice(outputDirectory, moduleName, frameworkSearchPath, dylibPath,
                simSlice, logger, commandRunner, internalTypeNames,
                additionalFrameworkSearchPaths, moduleNameForCollision: moduleNameForCollision,
                nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                swiftInterfacePath: swiftInterfacePath,
                skipThunkCompilation: skipThunkCompilation,
                depModuleNamesForCollision: depModuleNamesForCollision,
                linkFrameworks: linkFrameworks,
                linkLibraries: linkLibraries);
        }

        /// <summary>
        /// Compiles generated Swift wrapper files into a multi-slice xcframework (simulator + device).
        /// If the device resolution is null, produces a simulator-only xcframework.
        /// Returns null if no Swift files exist in the output directory.
        /// </summary>
        public static SwiftWrapperCompilationResult? CompileAll(
            string outputDirectory,
            string moduleName,
            XCFrameworkResolution simulatorResolution,
            XCFrameworkResolution? deviceResolution,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            HashSet<string>? internalTypeNames = null,
            IReadOnlyList<string>? simAdditionalSearchPaths = null,
            IReadOnlyList<string>? deviceAdditionalSearchPaths = null,
            bool skipThunkCompilation = false,
            PlatformInfo? platformInfo = null,
            string? moduleNameForCollision = null,
            HashSet<string>? nestedTypesInCollidingClass = null,
            string? swiftInterfacePath = null,
            IReadOnlyList<string>? depModuleNamesForCollisionSimulator = null,
            IReadOnlyList<string>? depModuleNamesForCollisionDevice = null,
            IReadOnlyList<string>? linkFrameworks = null,
            IReadOnlyList<string>? linkLibraries = null)
        {
            commandRunner ??= new SystemCommandRunner();
            var wrapperModuleName = $"{moduleName}SwiftBindings";

            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            // Override architecture from resolution (defense-in-depth: not all xcframeworks have arm64)
            var simSlice = pi.GetSlice(true).WithArchitecture(simulatorResolution.SelectedArchitecture);
            var deviceSlice = deviceResolution != null
                ? pi.DeviceSlice.WithArchitecture(deviceResolution.SelectedArchitecture)
                : pi.DeviceSlice;
            var primaryAdditionalSearchPaths = !pi.HasSimulatorVariant && deviceAdditionalSearchPaths != null
                ? deviceAdditionalSearchPaths
                : simAdditionalSearchPaths;

            // 1. Collect and post-process Swift files (once — source is architecture-agnostic)
            var swiftFiles = CollectSwiftFiles(outputDirectory);
            // Thunk assembly is emitted per-arch (.arm64.s / .x86_64.s); gate on the slice's
            // actual architecture so an x86_64 pass doesn't false-negative on .arm64.s.
            var hasAssemblyFiles = !skipThunkCompilation &&
                NativeThunkCompiler.CollectAssemblyFiles(outputDirectory, simSlice.Architecture).Count > 0;

            if (swiftFiles.Count == 0 && !hasAssemblyFiles)
            {
                logger.LogInformation("No Swift wrapper files or thunk assembly files found in {Dir} — skipping wrapper compilation.", outputDirectory);
                return null;
            }

            logger.LogInformation("Compiling wrapper into {Module}.xcframework ({SwiftCount} Swift file(s), thunks: {HasThunks})...",
                wrapperModuleName, swiftFiles.Count, hasAssemblyFiles ? "yes" : "no");

            var cleanedDir = Path.Combine(outputDirectory, ".wrapper-build");
            if (Directory.Exists(cleanedDir))
                Directory.Delete(cleanedDir, true);
            Directory.CreateDirectory(cleanedDir);

            int totalStripped = 0;
            var allStrippedSymbols = new HashSet<string>();
            var subCauseTotals = NewSubCauseTotals();
            var cleanedFiles = new List<string>();
            // Unique staging tree for the two-slice build; assigned at step 3. Tracked out here so
            // the finally drops it if it was never promoted (compile/validation failure or the
            // no-slice early return). On success it is MOVED to the canonical path.
            string? stagingXcfwPath = null;

            try
            {
                foreach (var swiftFile in swiftFiles)
                {
                    var content = File.ReadAllText(swiftFile);
                    var result = SwiftWrapperPostProcessor.Process(content, internalTypeNames,
                        warning => logger.LogWarning("{Warning}", warning), currentModuleName: moduleName);
                    totalStripped += result.StrippedBlockCount;
                    allStrippedSymbols.UnionWith(result.StrippedSymbols);
                    AccumulateSubCauseTotals(subCauseTotals, result.StrippedBlocksBySubCause);

                    if (result.StrippedBlockCount > 0)
                    {
                        logger.LogInformation("  Stripped {Count} broken wrapper(s) from {File} ({SubCauses})",
                            result.StrippedBlockCount, Path.GetFileName(swiftFile),
                            FormatSubCauseSummary(result.StrippedBlocksBySubCause));
                    }

                    if (!string.IsNullOrWhiteSpace(result.CleanedContent))
                    {
                        var cleanedPath = Path.Combine(cleanedDir, Path.GetFileName(swiftFile));
                        File.WriteAllText(cleanedPath, result.CleanedContent);
                        cleanedFiles.Add(cleanedPath);
                    }
                }

                if (cleanedFiles.Count == 0 && !hasAssemblyFiles)
                {
                    logger.LogWarning("All Swift wrapper code was stripped as broken ({Count} block(s); {SubCauses}).",
                        totalStripped, FormatSubCauseSummary(subCauseTotals));
                    return new SwiftWrapperCompilationResult
                    {
                        XCFrameworkPath = "",
                        CompiledFileCount = 0,
                        StrippedBlockCount = totalStripped,
                        StrippedBlocksBySubCause = subCauseTotals,
                        StrippedSymbols = allStrippedSymbols,
                        SliceCount = 0
                    };
                }

                // 1b. Detect simulator-only members (for wrapper guards and thunk filtering)
                SimulatorOnlyResult? simulatorOnlyMembers = null;
                if (deviceResolution != null)
                {
                    simulatorOnlyMembers = SimulatorOnlyMemberDetector.Detect(
                        simulatorResolution.AbiJsonPath, deviceResolution.AbiJsonPath, logger);

                    // Apply #if targetEnvironment(simulator) guards to wrapper Swift files
                    if (simulatorOnlyMembers.Count > 0 && cleanedFiles.Count > 0)
                    {
                        foreach (var cleanedFile in cleanedFiles)
                        {
                            var content = File.ReadAllText(cleanedFile);
                            var (guarded, count) = SimulatorOnlyMemberDetector.ApplySimulatorGuards(
                                content, moduleName, simulatorOnlyMembers);
                            if (count > 0)
                            {
                                File.WriteAllText(cleanedFile, guarded);
                                logger.LogInformation("  Applied #if targetEnvironment(simulator) guards to {Count} wrapper(s) in {File}",
                                    count, Path.GetFileName(cleanedFile));
                            }
                        }
                    }
                }

                // 2. Resolve deployment target (clamped to this generation target's floor)
                var minOS = ResolveDeploymentTarget(simulatorResolution.DylibPath, logger, commandRunner, pi);

                // 2b. Resource bundle stubs: detect SPM resource bundles in the framework
                // and create empty .bundle directories in the output directory at build time.
                // SPM-generated resource_bundle_accessor.swift searches Bundle.main for named
                // bundles — stubs placed in the output directory get copied to the app bundle
                // root by Sdk.targets, where the accessor will discover them.
                var bundleNames = DetectResourceBundleNames(simulatorResolution.DylibPath, commandRunner, logger);
                if (bundleNames.Count > 0)
                {
                    CreateResourceBundleStubs(bundleNames, outputDirectory, logger, simulatorResolution.DylibPath);
                }

                // 3. Create xcframework directory structure. Build both slices into a unique
                // staging tree OUTSIDE the canonical path, validate, then atomically promote — so
                // a timed-out/killed swiftc on either slice can never leave a partial or
                // half-written tree at the canonical path the SDK consumes. (The previous
                // delete-then-build destroyed the prior good tree before the first swiftc ran.)
                var xcframeworkPath = Path.Combine(outputDirectory, $"{wrapperModuleName}.xcframework");
                RecoverStrandedSupersededXcframework(xcframeworkPath, logger);
                stagingXcfwPath = MakeStagingXcframeworkPath(outputDirectory, wrapperModuleName, simSlice.SliceId);

                var sliceCount = 0;

                // 4. Compile simulator slice
                var simFrameworkDir = Path.Combine(stagingXcfwPath, simSlice.SliceId, $"{wrapperModuleName}.framework");
                Directory.CreateDirectory(simFrameworkDir);
                WriteFrameworkPlist(simFrameworkDir, wrapperModuleName, minOS, simSlice.PlistPlatformName);

                var simSdkPath = ResolveSdkPath(simSlice.SdkName, commandRunner);
                var simTargetTriple = simSlice.GetTargetTriple(minOS);
                var simBinaryPath = Path.Combine(simFrameworkDir, wrapperModuleName);

                // Compile thunk assembly for simulator slice
                // FATAL if .arm64.s files exist — P/Invokes reference thunk symbols
                NativeThunkCompilationResult? simThunkResult = null;
                if (!skipThunkCompilation)
                {
                    try
                    {
                        simThunkResult = NativeThunkCompiler.CompileThunkObjects(
                            outputDirectory, simTargetTriple, simSdkPath, logger, commandRunner,
                            arch: simSlice.Architecture);
                    }
                    catch (Exception ex)
                    {
                        if (hasAssemblyFiles)
                        {
                            logger.LogError("Thunk compilation failed for simulator slice and .arm64.s files exist — P/Invokes will reference missing symbols: {Message}", ex.Message);
                            throw;
                        }
                        logger.LogWarning("Thunk compilation failed for simulator slice (non-fatal, no .arm64.s files): {Message}", ex.Message);
                    }
                }

                // Pre-compile colliding module(s) for simulator slice. The shadow .swiftmodule
                // is keyed on bound module + target triple, so EVERY collision (bound-module
                // EC-1, dep-module collisions like GTMAppAuth→GTMSessionFetcher, and XCTest)
                // must be patched in a SINGLE call — separate calls would overwrite each
                // other's binary and silently drop all but the last collision's regex.
                // The precompile also needs the SAME -F dependency search paths that the
                // final swiftc invocation gets: the patched .swiftinterface still references
                // sibling modules (e.g. GTMSessionFetcher imports GTMAppAuth), and without
                // their framework paths swift-frontend bails with "no such module …",
                // returns null, and the final wrapper compile re-hits the collision.
                var simEffectiveSearchPaths = primaryAdditionalSearchPaths != null
                    ? new List<string>(primaryAdditionalSearchPaths) : new List<string>();
                var simPrecompileTargets = new List<CollisionPatchTarget>();
                var simPrecompileExtraSearchPaths = primaryAdditionalSearchPaths != null
                    ? new List<string>(primaryAdditionalSearchPaths) : new List<string>();
                if (!string.IsNullOrEmpty(moduleNameForCollision))
                    simPrecompileTargets.Add(new CollisionPatchTarget(moduleNameForCollision, nestedTypesInCollidingClass));
                if (depModuleNamesForCollisionSimulator != null)
                {
                    foreach (var depModule in depModuleNamesForCollisionSimulator)
                        simPrecompileTargets.Add(new CollisionPatchTarget(depModule, null));
                }
                if (DetectXCTestDependency(swiftInterfacePath))
                {
                    var simPlatformPath = ResolvePlatformPath(simSlice.SdkName, commandRunner);
                    var platformFrameworksPath = Path.Combine(simPlatformPath, "Developer", "Library", "Frameworks");
                    simEffectiveSearchPaths.Add(platformFrameworksPath);
                    logger.LogInformation("Detected XCTest dependency — added platform frameworks search path for simulator slice.");
                    simPrecompileTargets.Add(new CollisionPatchTarget("XCTest", null));
                    simPrecompileExtraSearchPaths.Add(platformFrameworksPath);
                }

                var simPrecompiledShadowPaths = new List<string>();
                if (!string.IsNullOrEmpty(swiftInterfacePath))
                {
                    // Helper does its own gating: fires on collisions OR private-interface
                    // sanitization. Returns null when neither applies.
                    var shadow = PrecompileSanitizedShadowFramework(
                        moduleName, swiftInterfacePath, simTargetTriple, simSdkPath,
                        cleanedDir, commandRunner, logger, simPrecompileTargets,
                        simPrecompileExtraSearchPaths.Count > 0 ? simPrecompileExtraSearchPaths : null);
                    if (!string.IsNullOrEmpty(shadow))
                        simPrecompiledShadowPaths.Add(shadow);
                }

                // Gap 2: force-load a static-archive simulator primary into the wrapper.
                var simForceLoad = ResolvePrimaryForceLoad(simulatorResolution.DylibPath, commandRunner, logger);

                if (cleanedFiles.Count > 0)
                {
                    InvokeSwiftCompiler(
                        cleanedFiles, simBinaryPath, wrapperModuleName,
                        simTargetTriple, simSdkPath,
                        simulatorResolution.FrameworkSearchPath, commandRunner, logger,
                        simEffectiveSearchPaths,
                        simPrecompiledShadowPaths.Count > 0 ? simPrecompiledShadowPaths : null,
                        simThunkResult?.ObjectFiles, moduleName,
                        forceLoadBinaries: simForceLoad,
                        linkFrameworks: linkFrameworks,
                        linkLibraries: linkLibraries,
                        transitiveScanSearchPaths: primaryAdditionalSearchPaths);
                    sliceCount++;
                }
                else if (simThunkResult != null && simThunkResult.ObjectFiles.Count > 0)
                {
                    logger.LogInformation("No Swift wrappers — linking thunk objects with clang (simulator).");
                    NativeThunkCompiler.LinkWithClang(
                        simThunkResult.ObjectFiles, simBinaryPath, wrapperModuleName,
                        simTargetTriple, simSdkPath, commandRunner, logger,
                        simulatorResolution.FrameworkSearchPath, moduleName,
                        forceLoadBinaries: simForceLoad,
                        linkFrameworks: linkFrameworks,
                        linkLibraries: linkLibraries,
                        transitiveFrameworks: CollectTransitiveFrameworkNames(primaryAdditionalSearchPaths, new[] { moduleName }),
                        buildLinkFailureHint: stderr => BuildSystemLinkDependencyHint(
                            stderr, linkFrameworks, linkLibraries, simForceLoad, commandRunner, logger),
                        transitiveFrameworkSearchPaths: primaryAdditionalSearchPaths);
                    sliceCount++;
                }

                if (sliceCount > 0)
                {
                    // Validate before reporting the slice built — a timed-out/killed swiftc throws
                    // above; a truncated/wrong-arch binary is rejected here.
                    ValidateCompiledSliceBinary(simBinaryPath, simSlice.Architecture, commandRunner, logger);
                    logger.LogInformation("Compiled simulator slice for {Module}.", wrapperModuleName);
                }

                // 5. Compile device slice (if available)
                if (deviceResolution != null)
                {
                    var deviceSliceCountBefore = sliceCount;
                    var devFrameworkDir = Path.Combine(stagingXcfwPath, deviceSlice.SliceId, $"{wrapperModuleName}.framework");
                    Directory.CreateDirectory(devFrameworkDir);
                    WriteFrameworkPlist(devFrameworkDir, wrapperModuleName, minOS, deviceSlice.PlistPlatformName);

                    var devSdkPath = ResolveSdkPath(deviceSlice.SdkName, commandRunner);
                    var devTargetTriple = deviceSlice.GetTargetTriple(minOS);
                    var devBinaryPath = Path.Combine(devFrameworkDir, wrapperModuleName);

                    // Compile thunk assembly for device slice
                    // FATAL if .arm64.s files exist — P/Invokes reference thunk symbols
                    // Filter out thunks for simulator-only members before device compilation.
                    NativeThunkCompilationResult? devThunkResult = null;
                    var deviceThunkDir = outputDirectory;
                    if (simulatorOnlyMembers != null && simulatorOnlyMembers.Count > 0 && hasAssemblyFiles)
                    {
                        var deviceThunkBuildDir = Path.Combine(cleanedDir, ".device-thunks");
                        Directory.CreateDirectory(deviceThunkBuildDir);
                        bool anyFiltered = false;

                        foreach (var asmFile in NativeThunkCompiler.CollectAssemblyFiles(outputDirectory, deviceSlice.Architecture))
                        {
                            var filterResult = SimulatorOnlyMemberDetector.FilterThunkAssembly(
                                asmFile, simulatorOnlyMembers, deviceThunkBuildDir);
                            if (filterResult != null)
                            {
                                logger.LogInformation("  Filtered {Count} simulator-only thunk(s) from {File} for device slice",
                                    filterResult.Value.RemovedCount, Path.GetFileName(asmFile));
                                anyFiltered = true;
                            }
                            else
                            {
                                // No filtering needed — copy as-is
                                File.Copy(asmFile, Path.Combine(deviceThunkBuildDir, Path.GetFileName(asmFile)), overwrite: true);
                            }
                        }

                        if (anyFiltered)
                            deviceThunkDir = deviceThunkBuildDir;
                    }

                    if (!skipThunkCompilation)
                    {
                        try
                        {
                            devThunkResult = NativeThunkCompiler.CompileThunkObjects(
                                deviceThunkDir, devTargetTriple, devSdkPath, logger, commandRunner,
                                arch: deviceSlice.Architecture);
                        }
                        catch (Exception ex)
                        {
                            if (hasAssemblyFiles)
                            {
                                logger.LogError("Thunk compilation failed for device slice and .arm64.s files exist — P/Invokes will reference missing symbols: {Message}", ex.Message);
                                throw;
                            }
                            logger.LogWarning("Thunk compilation failed for device slice (non-fatal, no .arm64.s files): {Message}", ex.Message);
                        }
                    }

                    // Pre-compile colliding module(s) for device slice. Mirrors the simulator
                    // consolidated call above — all collisions (bound-module + dep-modules +
                    // XCTest) MUST be patched in a single PrecompileCollidingModule call.
                    // Same -F propagation: the precompile gets every dependency framework
                    // search path the final swiftc invocation gets, so imported sibling
                    // modules in the patched .swiftinterface resolve.
                    var devEffectiveSearchPaths = deviceAdditionalSearchPaths != null
                        ? new List<string>(deviceAdditionalSearchPaths) : new List<string>();
                    var devPrecompileTargets = new List<CollisionPatchTarget>();
                    var devPrecompileExtraSearchPaths = deviceAdditionalSearchPaths != null
                        ? new List<string>(deviceAdditionalSearchPaths) : new List<string>();
                    if (!string.IsNullOrEmpty(moduleNameForCollision))
                        devPrecompileTargets.Add(new CollisionPatchTarget(moduleNameForCollision, nestedTypesInCollidingClass));
                    if (depModuleNamesForCollisionDevice != null)
                    {
                        foreach (var depModule in depModuleNamesForCollisionDevice)
                            devPrecompileTargets.Add(new CollisionPatchTarget(depModule, null));
                    }
                    if (DetectXCTestDependency(deviceResolution.SwiftInterfacePath))
                    {
                        var devPlatformPath = ResolvePlatformPath(deviceSlice.SdkName, commandRunner);
                        var devPlatformFrameworksPath = Path.Combine(devPlatformPath, "Developer", "Library", "Frameworks");
                        devEffectiveSearchPaths.Add(devPlatformFrameworksPath);
                        logger.LogInformation("Detected XCTest dependency — added platform frameworks search path for device slice.");
                        devPrecompileTargets.Add(new CollisionPatchTarget("XCTest", null));
                        devPrecompileExtraSearchPaths.Add(devPlatformFrameworksPath);
                    }

                    var devPrecompiledShadowPaths = new List<string>();
                    if (!string.IsNullOrEmpty(deviceResolution.SwiftInterfacePath))
                    {
                        var shadow = PrecompileSanitizedShadowFramework(
                            moduleName, deviceResolution.SwiftInterfacePath,
                            devTargetTriple, devSdkPath,
                            cleanedDir, commandRunner, logger, devPrecompileTargets,
                            devPrecompileExtraSearchPaths.Count > 0 ? devPrecompileExtraSearchPaths : null);
                        if (!string.IsNullOrEmpty(shadow))
                            devPrecompiledShadowPaths.Add(shadow);
                    }

                    // Gap 2: force-load a static-archive device primary into the wrapper.
                    var devForceLoad = ResolvePrimaryForceLoad(deviceResolution.DylibPath, commandRunner, logger);

                    if (cleanedFiles.Count > 0)
                    {
                        InvokeSwiftCompiler(
                            cleanedFiles, devBinaryPath, wrapperModuleName,
                            devTargetTriple, devSdkPath,
                            deviceResolution.FrameworkSearchPath, commandRunner, logger,
                            devEffectiveSearchPaths,
                            devPrecompiledShadowPaths.Count > 0 ? devPrecompiledShadowPaths : null,
                            devThunkResult?.ObjectFiles, moduleName,
                            forceLoadBinaries: devForceLoad,
                            linkFrameworks: linkFrameworks,
                            linkLibraries: linkLibraries,
                            transitiveScanSearchPaths: deviceAdditionalSearchPaths);
                        sliceCount++;
                    }
                    else if (devThunkResult != null && devThunkResult.ObjectFiles.Count > 0)
                    {
                        logger.LogInformation("No Swift wrappers — linking thunk objects with clang (device).");
                        NativeThunkCompiler.LinkWithClang(
                            devThunkResult.ObjectFiles, devBinaryPath, wrapperModuleName,
                            devTargetTriple, devSdkPath, commandRunner, logger,
                            deviceResolution.FrameworkSearchPath, moduleName,
                            forceLoadBinaries: devForceLoad,
                            linkFrameworks: linkFrameworks,
                            linkLibraries: linkLibraries,
                            transitiveFrameworks: CollectTransitiveFrameworkNames(deviceAdditionalSearchPaths, new[] { moduleName }),
                            buildLinkFailureHint: stderr => BuildSystemLinkDependencyHint(
                                stderr, linkFrameworks, linkLibraries, devForceLoad, commandRunner, logger),
                            transitiveFrameworkSearchPaths: deviceAdditionalSearchPaths);
                        sliceCount++;
                    }

                    if (sliceCount > deviceSliceCountBefore)
                    {
                        // Validate the device binary independently of the sim slice (a sim-only
                        // thunk failure can leave sliceCount at 1 here even though the device slice
                        // was built and must still be checked before promotion).
                        ValidateCompiledSliceBinary(devBinaryPath, deviceSlice.Architecture, commandRunner, logger);
                        logger.LogInformation("Compiled device slice for {Module}.", wrapperModuleName);
                    }
                }

                // Guard: if no slices were compiled (all Swift stripped + thunks failed), return failure
                if (sliceCount == 0)
                {
                    logger.LogWarning("No wrapper binary produced: Swift wrappers stripped and thunk compilation failed.");
                    return new SwiftWrapperCompilationResult
                    {
                        XCFrameworkPath = "",
                        CompiledFileCount = 0,
                        StrippedBlockCount = totalStripped,
                        StrippedBlocksBySubCause = subCauseTotals,
                        StrippedSymbols = allStrippedSymbols,
                        SliceCount = 0
                    };
                }

                // 6. Write xcframework Info.plist into the staging tree, then atomically promote
                // the validated tree to the canonical path.
                WriteXCFrameworkPlist(stagingXcfwPath, wrapperModuleName, deviceResolution != null,
                    slice: simSlice, deviceSlice: deviceSlice);
                PromoteStagedXcframework(stagingXcfwPath, xcframeworkPath, logger);

                logger.LogInformation("{Module}.xcframework built successfully at {Path} ({SliceCount} slice(s)).",
                    wrapperModuleName, xcframeworkPath, sliceCount);

                // Write stripped symbols manifest for diagnostics
                if (allStrippedSymbols.Count > 0)
                {
                    var symbolsPath = Path.Combine(outputDirectory, "stripped-symbols.json");
                    var sorted = allStrippedSymbols.OrderBy(s => s).ToArray();
                    var jsonLines = new List<string> { "[" };
                    for (int idx = 0; idx < sorted.Length; idx++)
                    {
                        var comma = idx < sorted.Length - 1 ? "," : "";
                        jsonLines.Add($"  \"{sorted[idx]}\"{comma}");
                    }
                    jsonLines.Add("]");
                    File.WriteAllText(symbolsPath, string.Join("\n", jsonLines));
                    logger.LogInformation("Wrote {Count} stripped symbol(s) to {Path}",
                        allStrippedSymbols.Count, symbolsPath);
                }

                return new SwiftWrapperCompilationResult
                {
                    XCFrameworkPath = xcframeworkPath,
                    CompiledFileCount = cleanedFiles.Count,
                    StrippedBlockCount = totalStripped,
                    StrippedBlocksBySubCause = subCauseTotals,
                    StrippedSymbols = allStrippedSymbols,
                    SliceCount = sliceCount
                };
            }
            finally
            {
                try
                {
                    if (Directory.Exists(cleanedDir))
                        Directory.Delete(cleanedDir, true);
                }
                catch { /* best-effort cleanup */ }

                // Drop the staging tree if it was not promoted. On success it was MOVED to the
                // canonical path, so this is a no-op.
                try
                {
                    if (stagingXcfwPath != null && Directory.Exists(stagingXcfwPath))
                        Directory.Delete(stagingXcfwPath, true);
                }
                catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Compiles generated Swift wrapper files into a single-slice xcframework.
        /// </summary>
        /// <param name="outputDirectory">Directory containing generated Swift wrapper files.</param>
        /// <param name="moduleName">The Swift module name (e.g., "MyModule").</param>
        /// <param name="frameworkSearchPath">The -F flag target (e.g., xcframework slice directory).</param>
        /// <param name="dylibPath">Path to the source framework's dylib (used to locate Info.plist for min OS).</param>
        /// <param name="slice">The slice variant describing platform, SDK, target triple, etc.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        public static SwiftWrapperCompilationResult? CompileSlice(
            string outputDirectory,
            string moduleName,
            string frameworkSearchPath,
            string dylibPath,
            SliceVariant slice,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            HashSet<string>? internalTypeNames = null,
            IReadOnlyList<string>? additionalFrameworkSearchPaths = null,
            string? moduleNameForCollision = null,
            HashSet<string>? nestedTypesInCollidingClass = null,
            string? swiftInterfacePath = null,
            bool skipThunkCompilation = false,
            IReadOnlyList<string>? depModuleNamesForCollision = null,
            IReadOnlyList<string>? linkFrameworks = null,
            IReadOnlyList<string>? linkLibraries = null)
        {
            commandRunner ??= new SystemCommandRunner();
            var wrapperModuleName = $"{moduleName}SwiftBindings";

            // 1. Collect Swift files and assembly files. Thunk assembly is emitted per-arch
            // (.arm64.s / .x86_64.s); gate on this slice's architecture.
            var swiftFiles = CollectSwiftFiles(outputDirectory);
            var hasAssemblyFiles = !skipThunkCompilation &&
                NativeThunkCompiler.CollectAssemblyFiles(outputDirectory, slice.Architecture).Count > 0;

            if (swiftFiles.Count == 0 && !hasAssemblyFiles)
            {
                logger.LogInformation("No Swift wrapper files or thunk assembly files found in {Dir} — skipping wrapper compilation.", outputDirectory);
                return null;
            }

            logger.LogInformation("Compiling wrapper into {Module}.xcframework ({SwiftCount} Swift file(s), thunks: {HasThunks})...",
                wrapperModuleName, swiftFiles.Count, hasAssemblyFiles ? "yes" : "no");

            // 2. Post-process Swift files into temp dir
            var cleanedDir = Path.Combine(outputDirectory, ".wrapper-build");
            if (Directory.Exists(cleanedDir))
                Directory.Delete(cleanedDir, true);
            Directory.CreateDirectory(cleanedDir);

            int totalStripped = 0;
            var allStrippedSymbols = new HashSet<string>();
            var subCauseTotals = NewSubCauseTotals();
            var cleanedFiles = new List<string>();
            // Unique staging tree for this slice's build output; assigned inside the try once the
            // slice id is known. Tracked out here so the finally can drop it if it was never
            // promoted (compile/timeout/validation failure, or a no-binary early return).
            string? stagingXcfwPath = null;

            try
            {
                foreach (var swiftFile in swiftFiles)
                {
                    var content = File.ReadAllText(swiftFile);
                    var result = SwiftWrapperPostProcessor.Process(content, internalTypeNames,
                        warning => logger.LogWarning("{Warning}", warning), currentModuleName: moduleName);
                    totalStripped += result.StrippedBlockCount;
                    allStrippedSymbols.UnionWith(result.StrippedSymbols);
                    AccumulateSubCauseTotals(subCauseTotals, result.StrippedBlocksBySubCause);

                    if (result.StrippedBlockCount > 0)
                    {
                        logger.LogInformation("  Stripped {Count} broken wrapper(s) from {File} ({SubCauses})",
                            result.StrippedBlockCount, Path.GetFileName(swiftFile),
                            FormatSubCauseSummary(result.StrippedBlocksBySubCause));
                    }

                    // Only write files that have content left after processing
                    if (!string.IsNullOrWhiteSpace(result.CleanedContent))
                    {
                        var cleanedPath = Path.Combine(cleanedDir, Path.GetFileName(swiftFile));
                        File.WriteAllText(cleanedPath, result.CleanedContent);
                        cleanedFiles.Add(cleanedPath);
                    }
                }

                if (cleanedFiles.Count == 0 && !hasAssemblyFiles)
                {
                    logger.LogWarning("All Swift wrapper code was stripped as broken ({Count} block(s); {SubCauses}).",
                        totalStripped, FormatSubCauseSummary(subCauseTotals));
                    return new SwiftWrapperCompilationResult
                    {
                        XCFrameworkPath = "",
                        CompiledFileCount = 0,
                        StrippedBlockCount = totalStripped,
                        StrippedBlocksBySubCause = subCauseTotals,
                        StrippedSymbols = allStrippedSymbols
                    };
                }

                // 3. Resolve deployment target from source framework (clamped to this slice's
                // platform floor — the floor is platform-keyed, so derive it from the slice).
                var minOS = ResolveDeploymentTarget(
                    dylibPath, logger, commandRunner, PlatformInfoFactory.Create(slice.Platform));

                // 4. Create xcframework directory structure. Build into a unique staging tree
                // OUTSIDE the canonical path: swiftc (which can time out and be killed mid-write
                // under CI contention) never touches the canonical xcframework the SDK consumes.
                // We validate the staged binary and atomically promote it only on success, so the
                // canonical path stays byte-for-byte intact (or absent) on any failure/timeout.
                var isSimulator = slice.IsSimulator;
                var sliceId = slice.SliceId;
                var xcframeworkPath = Path.Combine(outputDirectory, $"{wrapperModuleName}.xcframework");
                // Recover a tree stranded by a process killed mid-promote on a prior run, so a
                // failure this run still leaves the last good build at the canonical path.
                RecoverStrandedSupersededXcframework(xcframeworkPath, logger);
                stagingXcfwPath = MakeStagingXcframeworkPath(outputDirectory, wrapperModuleName, sliceId);
                var frameworkDir = Path.Combine(stagingXcfwPath, sliceId, $"{wrapperModuleName}.framework");
                var outputBinaryPath = Path.Combine(frameworkDir, wrapperModuleName);
                CreateXCFrameworkStructure(stagingXcfwPath, frameworkDir, wrapperModuleName, minOS, slice);

                // 5. Resolve SDK path
                var sdkPath = ResolveSdkPath(slice.SdkName, commandRunner);

                // 6. Build target triple
                var targetTriple = slice.GetTargetTriple(minOS);

                // 6a. Compile thunk assembly files (.arm64.s → .o)
                // FATAL if .arm64.s files exist — generated P/Invokes reference thunk symbols
                // that won't exist in the binary if compilation fails.
                NativeThunkCompilationResult? thunkResult = null;
                if (!skipThunkCompilation)
                {
                    try
                    {
                        thunkResult = NativeThunkCompiler.CompileThunkObjects(
                            outputDirectory, targetTriple, sdkPath, logger, commandRunner,
                            arch: slice.Architecture);
                    }
                    catch (Exception ex)
                    {
                        if (hasAssemblyFiles)
                        {
                            // .arm64.s files exist → P/Invokes reference thunk symbols → compilation MUST succeed
                            logger.LogError("Thunk compilation failed and .arm64.s files exist — generated P/Invokes will reference missing symbols: {Message}", ex.Message);
                            throw;
                        }
                        logger.LogWarning("Thunk compilation failed (non-fatal, no .arm64.s files): {Message}", ex.Message);
                    }
                }

                // 6b. Pre-compile colliding module(s) if needed. The shadow .swiftmodule is
                // keyed on bound module + target triple, so every collision (bound-module
                // EC-1, dep-module collisions, XCTest 6c) must be patched in ONE call. Build
                // the consolidated target list and the XCTest search path together. The
                // precompile receives the full set of dependency framework search paths so
                // imported sibling modules in the patched .swiftinterface resolve under
                // swift-frontend, exactly as they do for the final wrapper-compile invocation.
                var effectiveSearchPaths = additionalFrameworkSearchPaths != null
                    ? new List<string>(additionalFrameworkSearchPaths) : new List<string>();
                var precompileTargets = new List<CollisionPatchTarget>();
                var precompileExtraSearchPaths = additionalFrameworkSearchPaths != null
                    ? new List<string>(additionalFrameworkSearchPaths) : new List<string>();
                if (!string.IsNullOrEmpty(moduleNameForCollision))
                    precompileTargets.Add(new CollisionPatchTarget(moduleNameForCollision, nestedTypesInCollidingClass));
                if (depModuleNamesForCollision != null)
                {
                    foreach (var depModule in depModuleNamesForCollision)
                        precompileTargets.Add(new CollisionPatchTarget(depModule, null));
                }

                // 6c. XCTest dependency: add platform framework search path + collision resolution.
                // XCTest.framework lives at the platform level. The XCTest module also has a class
                // named "XCTest", causing Swift to misresolve XCTest.XCTestCase as a nested type.
                if (DetectXCTestDependency(swiftInterfacePath))
                {
                    var platformPath = ResolvePlatformPath(slice.SdkName, commandRunner);
                    var platformFrameworksPath = Path.Combine(platformPath, "Developer", "Library", "Frameworks");
                    effectiveSearchPaths.Add(platformFrameworksPath);
                    logger.LogInformation("Detected XCTest dependency — added platform frameworks search path.");
                    precompileTargets.Add(new CollisionPatchTarget("XCTest", null));
                    precompileExtraSearchPaths.Add(platformFrameworksPath);
                }

                var precompiledShadowPaths = new List<string>();
                if (!string.IsNullOrEmpty(swiftInterfacePath))
                {
                    var shadow = PrecompileSanitizedShadowFramework(
                        moduleName, swiftInterfacePath, targetTriple, sdkPath,
                        cleanedDir, commandRunner, logger, precompileTargets,
                        precompileExtraSearchPaths.Count > 0 ? precompileExtraSearchPaths : null);
                    if (!string.IsNullOrEmpty(shadow))
                        precompiledShadowPaths.Add(shadow);
                }

                // 6d. Resource bundle stubs: detect SPM resource bundles in the framework
                // and create empty .bundle directories in the output directory at build time.
                // SPM-generated resource_bundle_accessor.swift searches Bundle.main for named
                // bundles — stubs placed in the output directory get copied to the app bundle
                // root by Sdk.targets, where the accessor will discover them.
                var bundleNames = DetectResourceBundleNames(dylibPath, commandRunner, logger);
                if (bundleNames.Count > 0)
                {
                    CreateResourceBundleStubs(bundleNames, outputDirectory, logger, dylibPath);
                }

                // Combine thunk object files for linking
                var objectFilesToLink = thunkResult?.ObjectFiles?.Count > 0
                    ? (IReadOnlyList<string>)thunkResult.ObjectFiles : null;

                // Gap 2: when the bound module's native binary is a static `ar` archive, it
                // must be force-loaded into the wrapper so the wrapper is the SOLE carrier of
                // its ObjC classes (and the source xcframework is then dropped from every
                // consumer reference/pack site — see _SwiftBindingSourceNativeLinkage). A
                // dynamic primary, or the direct/Apple path's `.tbd` text stub, returns
                // Dynamic and is left to the normal auto-linked `-framework`.
                var forceLoadBinaries = ResolvePrimaryForceLoad(dylibPath, commandRunner, logger);

                // 7. Link into wrapper binary
                if (cleanedFiles.Count > 0)
                {
                    // Normal path: swiftc compiles Swift + links thunk .o files
                    InvokeSwiftCompiler(
                        cleanedFiles, outputBinaryPath, wrapperModuleName,
                        targetTriple, sdkPath, frameworkSearchPath, commandRunner, logger,
                        effectiveSearchPaths,
                        precompiledShadowPaths.Count > 0 ? precompiledShadowPaths : null,
                        objectFilesToLink, moduleName,
                        forceLoadBinaries: forceLoadBinaries,
                        linkFrameworks: linkFrameworks,
                        linkLibraries: linkLibraries,
                        transitiveScanSearchPaths: additionalFrameworkSearchPaths);
                }
                else if (objectFilesToLink != null && objectFilesToLink.Count > 0)
                {
                    // Edge case: no Swift wrappers (all functions thunked).
                    // swiftc requires at least one .swift input, so use clang -shared.
                    logger.LogInformation("No Swift wrappers — linking {Count} object file(s) with clang.", objectFilesToLink.Count);
                    NativeThunkCompiler.LinkWithClang(
                        objectFilesToLink, outputBinaryPath, wrapperModuleName,
                        targetTriple, sdkPath, commandRunner, logger,
                        frameworkSearchPath, moduleName,
                        forceLoadBinaries: forceLoadBinaries,
                        linkFrameworks: linkFrameworks,
                        linkLibraries: linkLibraries,
                        transitiveFrameworks: CollectTransitiveFrameworkNames(additionalFrameworkSearchPaths, new[] { moduleName }),
                        buildLinkFailureHint: stderr => BuildSystemLinkDependencyHint(
                            stderr, linkFrameworks, linkLibraries, forceLoadBinaries, commandRunner, logger),
                        transitiveFrameworkSearchPaths: additionalFrameworkSearchPaths);
                }
                else if (cleanedFiles.Count == 0)
                {
                    // All Swift wrappers stripped AND thunk compilation failed — nothing to link
                    logger.LogWarning("No wrapper binary produced: Swift wrappers stripped and thunk compilation failed.");
                    return new SwiftWrapperCompilationResult
                    {
                        XCFrameworkPath = "",
                        CompiledFileCount = 0,
                        StrippedBlockCount = totalStripped,
                        StrippedBlocksBySubCause = subCauseTotals,
                        StrippedSymbols = allStrippedSymbols
                    };
                }

                // Validate the staged binary, then atomically promote it to the canonical path.
                // A timed-out or killed swiftc throws above (never reaching here); a
                // truncated/wrong-arch binary is rejected here — so the canonical tree only ever
                // receives a real, complete slice, and HasWrapperXCFramework is only ever recorded
                // off a validated artifact.
                ValidateCompiledSliceBinary(outputBinaryPath, slice.Architecture, commandRunner, logger);
                PromoteStagedXcframework(stagingXcfwPath, xcframeworkPath, logger);

                logger.LogInformation("{Module}.xcframework built successfully at {Path}",
                    wrapperModuleName, xcframeworkPath);

                // Write stripped symbols manifest for diagnostics
                if (allStrippedSymbols.Count > 0)
                {
                    var symbolsPath = Path.Combine(outputDirectory, "stripped-symbols.json");
                    var sorted = allStrippedSymbols.OrderBy(s => s).ToArray();
                    var jsonLines = new List<string> { "[" };
                    for (int idx = 0; idx < sorted.Length; idx++)
                    {
                        var comma = idx < sorted.Length - 1 ? "," : "";
                        jsonLines.Add($"  \"{sorted[idx]}\"{comma}");
                    }
                    jsonLines.Add("]");
                    File.WriteAllText(symbolsPath, string.Join("\n", jsonLines));
                    logger.LogInformation("Wrote {Count} stripped symbol(s) to {Path}",
                        allStrippedSymbols.Count, symbolsPath);
                }

                return new SwiftWrapperCompilationResult
                {
                    XCFrameworkPath = xcframeworkPath,
                    CompiledFileCount = cleanedFiles.Count,
                    StrippedBlockCount = totalStripped,
                    StrippedBlocksBySubCause = subCauseTotals,
                    StrippedSymbols = allStrippedSymbols
                };
            }
            finally
            {
                // 8. Cleanup temp dir
                try
                {
                    if (Directory.Exists(cleanedDir))
                        Directory.Delete(cleanedDir, true);
                }
                catch { /* best-effort cleanup */ }

                // Drop the staging tree if it was not promoted (compile/timeout/validation
                // failure, or a no-binary early return). On success it was MOVED to the canonical
                // path, so Directory.Exists is false here and this is a no-op.
                try
                {
                    if (stagingXcfwPath != null && Directory.Exists(stagingXcfwPath))
                        Directory.Delete(stagingXcfwPath, true);
                }
                catch { /* best-effort cleanup */ }
            }
        }

        private static Dictionary<StripSubCause, int> NewSubCauseTotals()
            => new()
            {
                [StripSubCause.InternalType] = 0,
                [StripSubCause.NSInvocation] = 0,
                [StripSubCause.Other] = 0,
            };

        private static void AccumulateSubCauseTotals(
            Dictionary<StripSubCause, int> totals,
            IReadOnlyDictionary<StripSubCause, int> add)
        {
            if (add == null) return;
            foreach (var (cause, count) in add)
                totals[cause] = totals.GetValueOrDefault(cause) + count;
        }

        private static string FormatSubCauseSummary(IReadOnlyDictionary<StripSubCause, int> counts)
        {
            if (counts == null || counts.Count == 0) return "no sub-causes";
            return string.Join(", ", counts
                .Where(kv => kv.Value > 0)
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}={kv.Value}"));
        }

        /// <summary>Backward-compatible overload that accepts string platformVariant/sdkName.</summary>
        public static SwiftWrapperCompilationResult? CompileSlice(
            string outputDirectory,
            string moduleName,
            string frameworkSearchPath,
            string dylibPath,
            string platformVariant,
            string sdkName,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            HashSet<string>? internalTypeNames = null,
            IReadOnlyList<string>? additionalFrameworkSearchPaths = null,
            PlatformInfo? platformInfo = null,
            string? moduleNameForCollision = null,
            HashSet<string>? nestedTypesInCollidingClass = null,
            string? swiftInterfacePath = null,
            bool skipThunkCompilation = false,
            string? resolvedArchitecture = null,
            IReadOnlyList<string>? depModuleNamesForCollision = null,
            IReadOnlyList<string>? linkFrameworks = null,
            IReadOnlyList<string>? linkLibraries = null)
        {
            var isSimulator = platformVariant == "simulator";
            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            var slice = pi.GetSlice(isSimulator);
            // Override the slice CPU arch when one was explicitly resolved (e.g. forcing x86_64
            // for an Intel/Rosetta target). Defaults preserve the historical arm64 slice arch.
            if (!string.IsNullOrEmpty(resolvedArchitecture))
                slice = slice.WithArchitecture(resolvedArchitecture);
            return CompileSlice(outputDirectory, moduleName, frameworkSearchPath, dylibPath,
                slice, logger, commandRunner, internalTypeNames, additionalFrameworkSearchPaths,
                moduleNameForCollision: moduleNameForCollision,
                nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                swiftInterfacePath: swiftInterfacePath,
                skipThunkCompilation: skipThunkCompilation,
                depModuleNamesForCollision: depModuleNamesForCollision,
                linkFrameworks: linkFrameworks,
                linkLibraries: linkLibraries);
        }

        /// <summary>
        /// Collects Swift files from the output directory, excluding SwiftUI bridge files.
        /// </summary>
        internal static List<string> CollectSwiftFiles(string outputDirectory)
        {
            if (!Directory.Exists(outputDirectory))
                return new List<string>();

            return Directory.GetFiles(outputDirectory, "*.swift")
                .Where(f => !f.EndsWith(".SwiftUIBridge.swift", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Collects SwiftUI bridge Swift files from the output directory.
        /// </summary>
        internal static List<string> CollectBridgeSwiftFiles(string outputDirectory)
        {
            if (!Directory.Exists(outputDirectory))
                return new List<string>();

            return Directory.GetFiles(outputDirectory, "*.SwiftUIBridge.swift")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Compiles SwiftUI bridge Swift files into a single-slice {Module}Bridge.xcframework.
        /// Bridge compilation is simpler than wrapper: no post-processing, no thunks, no pre-compiled module.
        /// Returns null if no bridge Swift files exist.
        /// </summary>
        public static SwiftWrapperCompilationResult? CompileBridge(
            string outputDirectory,
            string moduleName,
            string frameworkSearchPath,
            string dylibPath,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            IReadOnlyList<string>? additionalFrameworkSearchPaths = null,
            PlatformInfo? platformInfo = null,
            string? resolvedArchitecture = null)
        {
            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            var simSlice = pi.GetSlice(true);
            // Rewrite the slice's arch (and arch-stamped slice ID) so a fat-fold's x86_64 pass lands in
            // ios-x86_64-simulator, not the default ios-arm64-simulator — exactly like wrapper Compile.
            if (!string.IsNullOrEmpty(resolvedArchitecture))
                simSlice = simSlice.WithArchitecture(resolvedArchitecture);
            return CompileBridgeSlice(outputDirectory, moduleName, frameworkSearchPath, dylibPath,
                simSlice, logger, commandRunner, additionalFrameworkSearchPaths);
        }

        /// <summary>
        /// Compiles SwiftUI bridge Swift files into a multi-slice {Module}Bridge.xcframework.
        /// Returns null if no bridge Swift files exist.
        /// </summary>
        public static SwiftWrapperCompilationResult? CompileBridgeAll(
            string outputDirectory,
            string moduleName,
            XCFrameworkResolution simulatorResolution,
            XCFrameworkResolution? deviceResolution,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            IReadOnlyList<string>? simAdditionalSearchPaths = null,
            IReadOnlyList<string>? deviceAdditionalSearchPaths = null,
            PlatformInfo? platformInfo = null)
        {
            commandRunner ??= new SystemCommandRunner();
            var bridgeModuleName = $"{moduleName}Bridge";

            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            // Override architecture from resolution so a fat-fold's per-arch pass writes a slice ID
            // matching its CPU (ios-x86_64-simulator vs ios-arm64-simulator) — same as wrapper CompileAll.
            var simSlice = pi.GetSlice(true).WithArchitecture(simulatorResolution.SelectedArchitecture);
            var deviceSlice = deviceResolution != null
                ? pi.DeviceSlice.WithArchitecture(deviceResolution.SelectedArchitecture)
                : pi.DeviceSlice;
            var primaryAdditionalSearchPaths = !pi.HasSimulatorVariant && deviceAdditionalSearchPaths != null
                ? deviceAdditionalSearchPaths
                : simAdditionalSearchPaths;

            var bridgeFiles = CollectBridgeSwiftFiles(outputDirectory);
            if (bridgeFiles.Count == 0)
            {
                logger.LogInformation("No SwiftUI bridge files found in {Dir} — skipping bridge compilation.", outputDirectory);
                return null;
            }

            logger.LogInformation("Compiling bridge into {Module}.xcframework ({Count} file(s))...",
                bridgeModuleName, bridgeFiles.Count);

            // Resolve deployment target (clamped to this generation target's floor)
            var minOS = ResolveDeploymentTarget(simulatorResolution.DylibPath, logger, commandRunner, pi);

            // Create xcframework directory structure. Build both slices into a unique staging
            // tree, validate, then atomically promote — same hazard as the wrapper paths: a
            // timed-out/killed swiftc must never leave a partial tree at the canonical path.
            var xcframeworkPath = Path.Combine(outputDirectory, $"{bridgeModuleName}.xcframework");
            RecoverStrandedSupersededXcframework(xcframeworkPath, logger);
            var stagingXcfwPath = MakeStagingXcframeworkPath(outputDirectory, bridgeModuleName, simSlice.SliceId);

            try
            {
                var sliceCount = 0;

                // Compile simulator slice
                var simFrameworkDir = Path.Combine(stagingXcfwPath, simSlice.SliceId, $"{bridgeModuleName}.framework");
                Directory.CreateDirectory(simFrameworkDir);
                WriteFrameworkPlist(simFrameworkDir, bridgeModuleName, minOS, simSlice.PlistPlatformName);

                var simSdkPath = ResolveSdkPath(simSlice.SdkName, commandRunner);
                var simTargetTriple = simSlice.GetTargetTriple(minOS);
                var simBinaryPath = Path.Combine(simFrameworkDir, bridgeModuleName);

                InvokeSwiftCompiler(
                    bridgeFiles, simBinaryPath, bridgeModuleName,
                    simTargetTriple, simSdkPath,
                    simulatorResolution.FrameworkSearchPath, commandRunner, logger,
                    primaryAdditionalSearchPaths, originalModuleName: moduleName);
                ValidateCompiledSliceBinary(simBinaryPath, simSlice.Architecture, commandRunner, logger);
                sliceCount++;

                logger.LogInformation("Compiled simulator slice for {Module}.", bridgeModuleName);

                // Compile device slice (if available)
                if (deviceResolution != null)
                {
                    var devFrameworkDir = Path.Combine(stagingXcfwPath, deviceSlice.SliceId, $"{bridgeModuleName}.framework");
                    Directory.CreateDirectory(devFrameworkDir);
                    WriteFrameworkPlist(devFrameworkDir, bridgeModuleName, minOS, deviceSlice.PlistPlatformName);

                    var devSdkPath = ResolveSdkPath(deviceSlice.SdkName, commandRunner);
                    var devTargetTriple = deviceSlice.GetTargetTriple(minOS);
                    var devBinaryPath = Path.Combine(devFrameworkDir, bridgeModuleName);

                    InvokeSwiftCompiler(
                        bridgeFiles, devBinaryPath, bridgeModuleName,
                        devTargetTriple, devSdkPath,
                        deviceResolution.FrameworkSearchPath, commandRunner, logger,
                        deviceAdditionalSearchPaths, originalModuleName: moduleName);
                    ValidateCompiledSliceBinary(devBinaryPath, deviceSlice.Architecture, commandRunner, logger);
                    sliceCount++;

                    logger.LogInformation("Compiled device slice for {Module}.", bridgeModuleName);
                }

                // Write xcframework Info.plist into staging, then atomically promote to canonical.
                WriteXCFrameworkPlist(stagingXcfwPath, bridgeModuleName, deviceResolution != null,
                    slice: simSlice, deviceSlice: deviceSlice);
                PromoteStagedXcframework(stagingXcfwPath, xcframeworkPath, logger);

                logger.LogInformation("{Module}.xcframework built successfully at {Path} ({SliceCount} slice(s)).",
                    bridgeModuleName, xcframeworkPath, sliceCount);

                return new SwiftWrapperCompilationResult
                {
                    XCFrameworkPath = xcframeworkPath,
                    CompiledFileCount = bridgeFiles.Count,
                    StrippedBlockCount = 0,
                    SliceCount = sliceCount
                };
            }
            finally
            {
                try
                {
                    if (Directory.Exists(stagingXcfwPath))
                        Directory.Delete(stagingXcfwPath, true);
                }
                catch { /* best-effort cleanup */ }
            }
        }

        /// <summary>
        /// Compiles SwiftUI bridge Swift files into a single-slice xcframework.
        /// </summary>
        internal static SwiftWrapperCompilationResult? CompileBridgeSlice(
            string outputDirectory,
            string moduleName,
            string frameworkSearchPath,
            string dylibPath,
            SliceVariant slice,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            IReadOnlyList<string>? additionalFrameworkSearchPaths = null)
        {
            commandRunner ??= new SystemCommandRunner();
            var bridgeModuleName = $"{moduleName}Bridge";

            var bridgeFiles = CollectBridgeSwiftFiles(outputDirectory);
            if (bridgeFiles.Count == 0)
            {
                logger.LogInformation("No SwiftUI bridge files found in {Dir} — skipping bridge compilation.", outputDirectory);
                return null;
            }

            logger.LogInformation("Compiling bridge into {Module}.xcframework ({Count} file(s))...",
                bridgeModuleName, bridgeFiles.Count);

            // Resolve deployment target (clamped to this slice's platform floor)
            var minOS = ResolveDeploymentTarget(
                dylibPath, logger, commandRunner, PlatformInfoFactory.Create(slice.Platform));

            // Create xcframework directory structure. Build into a unique staging tree, validate,
            // and atomically promote — same hazard as the wrapper slice: a timed-out/killed swiftc
            // must never leave a partial binary at the canonical path the SDK consumes.
            var xcframeworkPath = Path.Combine(outputDirectory, $"{bridgeModuleName}.xcframework");
            RecoverStrandedSupersededXcframework(xcframeworkPath, logger);
            var stagingXcfwPath = MakeStagingXcframeworkPath(outputDirectory, bridgeModuleName, slice.SliceId);
            var frameworkDir = Path.Combine(stagingXcfwPath, slice.SliceId, $"{bridgeModuleName}.framework");
            CreateXCFrameworkStructure(stagingXcfwPath, frameworkDir, bridgeModuleName, minOS, slice);

            // Resolve SDK path and build target triple
            var sdkPath = ResolveSdkPath(slice.SdkName, commandRunner);
            var targetTriple = slice.GetTargetTriple(minOS);
            var outputBinaryPath = Path.Combine(frameworkDir, bridgeModuleName);

            try
            {
                // Compile bridge — no post-processing, no thunks, no pre-compiled module
                InvokeSwiftCompiler(
                    bridgeFiles, outputBinaryPath, bridgeModuleName,
                    targetTriple, sdkPath, frameworkSearchPath, commandRunner, logger,
                    additionalFrameworkSearchPaths, originalModuleName: moduleName);

                ValidateCompiledSliceBinary(outputBinaryPath, slice.Architecture, commandRunner, logger);
                PromoteStagedXcframework(stagingXcfwPath, xcframeworkPath, logger);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(stagingXcfwPath))
                        Directory.Delete(stagingXcfwPath, true);
                }
                catch { /* best-effort cleanup */ }
            }

            logger.LogInformation("{Module}.xcframework built successfully at {Path}",
                bridgeModuleName, xcframeworkPath);

            return new SwiftWrapperCompilationResult
            {
                XCFrameworkPath = xcframeworkPath,
                CompiledFileCount = bridgeFiles.Count,
                StrippedBlockCount = 0
            };
        }

        /// <summary>
        /// Reads MinimumOSVersion from the source framework's Info.plist and clamps it to the
        /// generation target's deployment floor (the target <c>PlatformInfo.DefaultMinimumOS</c>,
        /// e.g. iOS 15.0, macOS 12.0). Falls back to that floor if not found, missing, or set to
        /// a vendor sentinel (e.g. some SDKs ship every framework with a sentinel version like
        /// "100.0"). The resolved value flows into <c>swiftc -target arm64-apple-{os}{minOS}…</c>,
        /// so it must match the floor the binding advertises in <c>SupportedOSPlatformVersion</c>
        /// — otherwise the wrapper binary's minimum-OS would exceed (and refuse to load under)
        /// the deployment target the csproj declares.
        /// </summary>
        internal static string ResolveDeploymentTarget(
            string dylibPath, ILogger logger, ICommandRunner? commandRunner = null,
            PlatformInfo? platformInfo = null)
        {
            // ClampMinimumOSVersion(null, …) yields the target platform's floor, keeping the
            // not-found/floor fallback on the same single source of truth as the clamp itself.
            var fallback = XCFrameworkMetadataExtractor.ClampMinimumOSVersion(null, platformInfo);

            var frameworkDir = Path.GetDirectoryName(dylibPath);
            if (string.IsNullOrEmpty(frameworkDir))
                return fallback;

            var infoPlistPath = Path.Combine(frameworkDir, "Info.plist");
            var data = PlistReader.ReadPlistDict(infoPlistPath, commandRunner, logger);
            if (data != null && data.TryGetValue("MinimumOSVersion", out var minOS) && minOS is string minOSStr)
            {
                // Route through the shared clamp so the deployment floor and the sentinel
                // ceiling stay in lockstep with the metadata extractor. Features requiring
                // newer SDKs are handled by @available attributes on generated wrappers.
                var resolved = XCFrameworkMetadataExtractor.ClampMinimumOSVersion(minOSStr, platformInfo);
                if (resolved != minOSStr)
                    logger.LogInformation("Adjusted deployment target from {Source} to {Resolved} (floor/sentinel filter).", minOSStr, resolved);
                else
                    logger.LogInformation("Resolved deployment target {Version} from source framework.", resolved);
                return resolved;
            }

            logger.LogDebug("Could not read MinimumOSVersion from Info.plist, using default {Version}", fallback);
            return fallback;
        }

        /// <summary>
        /// Returns the higher of two version strings (major.minor comparison).
        /// Used to enforce minimum deployment targets for language features.
        /// </summary>
        internal static string EnforceMinimumDeploymentTarget(string sourceVersion, string minimumVersion)
        {
            if (Version.TryParse(sourceVersion, out var src) && Version.TryParse(minimumVersion, out var min))
                return src >= min ? sourceVersion : minimumVersion;
            return sourceVersion; // Can't parse — keep original
        }

        /// <summary>
        /// Resolves an SDK path via xcrun.
        /// </summary>
        /// <param name="sdkName">SDK name: "iphonesimulator", "iphoneos", "macosx", etc.</param>
        /// <param name="commandRunner">Command runner.</param>
        internal static string ResolveSdkPath(string sdkName, ICommandRunner commandRunner)
        {
            var (exitCode, sdkPath, stderr) = commandRunner.Run("xcrun", $"--sdk {sdkName} --show-sdk-path");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(sdkPath))
            {
                throw new InvalidOperationException(
                    $"Failed to resolve SDK path for '{sdkName}'. Ensure Xcode and the platform SDK are installed. " +
                    $"Error: {stderr}");
            }
            return sdkPath;
        }

        /// <summary>
        /// Resolves the iOS Simulator SDK path via xcrun. Backward-compatible overload.
        /// </summary>
        internal static string ResolveSdkPath(ICommandRunner commandRunner)
        {
            return ResolveSdkPath("iphonesimulator", commandRunner);
        }

        /// <summary>
        /// Resolves a platform path via xcrun (e.g., iPhoneSimulator.platform directory).
        /// Used to locate platform-level frameworks like XCTest that live outside the SDK.
        /// </summary>
        /// <param name="sdkName">SDK name: "iphonesimulator", "iphoneos", "macosx", etc.</param>
        /// <param name="commandRunner">Command runner.</param>
        internal static string ResolvePlatformPath(string sdkName, ICommandRunner commandRunner)
        {
            var (exitCode, platformPath, stderr) = commandRunner.Run("xcrun", $"--sdk {sdkName} --show-sdk-platform-path");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(platformPath))
            {
                throw new InvalidOperationException(
                    $"Failed to resolve platform path for '{sdkName}'. Ensure Xcode and the platform SDK are installed. " +
                    $"Error: {stderr}");
            }
            return platformPath;
        }

        /// <summary>
        /// Detects whether a Swift framework depends on XCTest by scanning its swiftinterface
        /// for <c>import XCTest</c>. XCTest.framework lives at the platform level (not SDK level),
        /// so extra framework search paths and collision resolution are needed.
        /// </summary>
        internal static bool DetectXCTestDependency(string? swiftInterfacePath)
        {
            if (string.IsNullOrEmpty(swiftInterfacePath) || !File.Exists(swiftInterfacePath))
                return false;

            // Read the swiftinterface and look for "import XCTest" on its own line.
            // Match: "import XCTest", "import XCTest.Sub", "@_exported import XCTest"
            // Reject: "import XCTestUtils" (different module)
            foreach (var line in File.ReadLines(swiftInterfacePath))
            {
                var trimmed = line.TrimStart();
                if (trimmed == "import XCTest" ||
                    trimmed.StartsWith("import XCTest.", StringComparison.Ordinal) ||
                    trimmed.StartsWith("import XCTest ", StringComparison.Ordinal))
                    return true;
                if (trimmed == "@_exported import XCTest" ||
                    trimmed.StartsWith("@_exported import XCTest.", StringComparison.Ordinal) ||
                    trimmed.StartsWith("@_exported import XCTest ", StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Creates the xcframework directory structure with both Info.plists (single-slice).
        /// </summary>
        internal static void CreateXCFrameworkStructure(
            string xcframeworkPath, string frameworkDir, string wrapperModuleName, string minOS,
            SliceVariant? slice = null)
        {
            // Remove previous build
            if (Directory.Exists(xcframeworkPath))
                Directory.Delete(xcframeworkPath, true);

            Directory.CreateDirectory(frameworkDir);

            var platformName = slice?.PlistPlatformName ?? "iPhoneSimulator";
            WriteFrameworkPlist(frameworkDir, wrapperModuleName, minOS, platformName);
            WriteXCFrameworkPlist(xcframeworkPath, wrapperModuleName, includeDeviceSlice: false, slice: slice);
        }

        /// <summary>
        /// Computes a unique, same-directory staging path for a wrapper/bridge slice build. Building
        /// into a staging tree — instead of straight into the canonical <c>{module}.xcframework</c>
        /// path — means a timed-out or killed <c>swiftc</c> can never leave a partial/missing binary
        /// at the path the SDK consumes: the canonical tree is touched only by an atomic promote
        /// AFTER the staged binary is validated. The staging name deliberately avoids the literal
        /// <c>.xcframework</c> token so a <c>*.xcframework*</c> glob can't pick it up, and lives in the
        /// canonical path's parent so the promote is a same-volume rename.
        /// </summary>
        internal static string MakeStagingXcframeworkPath(
            string outputDirectory, string moduleName, string sliceId)
            => Path.Combine(outputDirectory, $"{moduleName}.xcfwstaging-{sliceId}-{Guid.NewGuid():N}");

        /// <summary>
        /// Validates a freshly compiled wrapper/bridge slice binary before it is promoted to the
        /// canonical xcframework path or reported as a successful build. Guards against a swiftc step
        /// that timed out / was killed mid-write (missing or truncated <c>-o</c> target). Throws
        /// <see cref="InvalidOperationException"/> — which callers treat exactly like a compile
        /// failure — when the binary is missing, empty, not a Mach-O image, or lacks the expected
        /// architecture. <c>lipo</c> is authoritative for thin and fat binaries.
        /// </summary>
        internal static void ValidateCompiledSliceBinary(
            string binaryPath, string expectedArch, ICommandRunner commandRunner, ILogger logger)
        {
            var fileInfo = new FileInfo(binaryPath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
                throw new InvalidOperationException(
                    $"Wrapper compilation produced no binary at '{binaryPath}' (missing or empty) — " +
                    "the swiftc step likely timed out or was killed mid-write.");

            if (!IsMachOImageFile(binaryPath))
                throw new InvalidOperationException(
                    $"Wrapper binary at '{binaryPath}' is not a valid Mach-O image (truncated or corrupt output).");

            var (exitCode, stdout, stderr) = commandRunner.Run("lipo", $"-archs \"{binaryPath}\"");
            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"Could not read architectures of wrapper binary '{binaryPath}' (lipo exit {exitCode}): {stderr}");
            var archs = stdout.Split(
                new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (!archs.Contains(expectedArch, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"Wrapper binary '{binaryPath}' is missing the expected architecture '{expectedArch}' " +
                    $"(found: {(archs.Length > 0 ? string.Join(", ", archs) : "none")}).");

            logger.LogDebug("Validated wrapper slice binary {Path} (arch {Arch}, {Bytes} bytes).",
                binaryPath, expectedArch, fileInfo.Length);
        }

        /// <summary>
        /// Atomically promotes a validated staging xcframework tree to the canonical path using a
        /// recoverable swap (mirrors the SDK <c>_merge_slices</c> <c>.superseded</c> pattern). The
        /// canonical tree is parked at a <c>.superseded</c> sibling before the staged tree moves in,
        /// so a crash between the two renames never leaves the canonical path empty-and-unrecoverable:
        /// the next build's <see cref="RecoverStrandedSupersededXcframework"/> restores it. A failed
        /// move-in rolls the original back. Both directories share the canonical path's parent, so
        /// each rename is a same-volume operation; <see cref="Directory.Move(string,string)"/> cannot
        /// overwrite a non-empty destination, which is why the canonical tree is parked first rather
        /// than deleted.
        /// </summary>
        internal static void PromoteStagedXcframework(
            string stagingXcfwPath, string canonicalXcfwPath, ILogger logger)
        {
            var superseded = canonicalXcfwPath + ".superseded";
            if (Directory.Exists(superseded))
                Directory.Delete(superseded, true); // stale from a prior interrupted promote

            var parked = false;
            if (Directory.Exists(canonicalXcfwPath))
            {
                Directory.Move(canonicalXcfwPath, superseded);
                parked = true;
            }

            try
            {
                Directory.Move(stagingXcfwPath, canonicalXcfwPath);
            }
            catch
            {
                // Roll the original back so a failed promote never destroys the prior good tree.
                if (parked && !Directory.Exists(canonicalXcfwPath) && Directory.Exists(superseded))
                    Directory.Move(superseded, canonicalXcfwPath);
                throw;
            }

            // The validated wrapper is now installed at the canonical path — the promote SUCCEEDED.
            // Dropping the parked prior tree is pure housekeeping, so a delete failure here must NOT
            // propagate: callers convert any throw out of compilation into a null result and record
            // HasWrapperXCFramework=False, which would drop the consumer NativeReference for a wrapper
            // that is actually present on disk. A leftover '.superseded' is harmless — the next build's
            // RecoverStrandedSupersededXcframework drops it once it sees the canonical tree present.
            if (Directory.Exists(superseded))
            {
                try
                {
                    Directory.Delete(superseded, true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Could not remove the parked '{Superseded}' tree after a successful promote; " +
                        "it will be cleaned up on the next build.",
                        superseded);
                }
            }
        }

        /// <summary>
        /// Recovers a canonical xcframework left only at its <c>.superseded</c> sibling by a process
        /// that died between the two renames in <see cref="PromoteStagedXcframework"/>. Restores the
        /// parked tree only when the canonical path is absent (otherwise the <c>.superseded</c> is a
        /// stale leftover and is dropped). A cheap no-op in the common case where neither parked tree
        /// exists.
        /// </summary>
        internal static void RecoverStrandedSupersededXcframework(string canonicalXcfwPath, ILogger logger)
        {
            var superseded = canonicalXcfwPath + ".superseded";
            if (!Directory.Exists(superseded))
                return;
            if (!Directory.Exists(canonicalXcfwPath))
            {
                logger.LogInformation(
                    "Recovered xcframework parked at '{Superseded}' from an interrupted build.", superseded);
                Directory.Move(superseded, canonicalXcfwPath);
            }
            else
            {
                Directory.Delete(superseded, true);
            }
        }

        /// <summary>
        /// Writes an individual framework slice's Info.plist.
        /// </summary>
        internal static void WriteFrameworkPlist(
            string frameworkDir, string wrapperModuleName, string minOS, string platformName)
        {
            var frameworkPlist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>CFBundleIdentifier</key>
                    <string>com.swiftbindings.{wrapperModuleName}</string>
                    <key>CFBundleName</key>
                    <string>{wrapperModuleName}</string>
                    <key>CFBundleExecutable</key>
                    <string>{wrapperModuleName}</string>
                    <key>CFBundlePackageType</key>
                    <string>FMWK</string>
                    <key>CFBundleVersion</key>
                    <string>1.0</string>
                    <key>CFBundleShortVersionString</key>
                    <string>1.0</string>
                    <key>MinimumOSVersion</key>
                    <string>{minOS}</string>
                    <key>CFBundleSupportedPlatforms</key>
                    <array>
                        <string>{platformName}</string>
                    </array>
                </dict>
                </plist>
                """;
            File.WriteAllText(Path.Combine(frameworkDir, "Info.plist"), frameworkPlist);
        }

        /// <summary>
        /// Writes the top-level xcframework Info.plist with one or two slice entries.
        /// </summary>
        internal static void WriteXCFrameworkPlist(
            string xcframeworkPath, string wrapperModuleName, bool includeDeviceSlice,
            SliceVariant? slice = null, SliceVariant? deviceSlice = null)
        {
            var simSliceId = slice?.SliceId ?? "ios-arm64-simulator";
            var simPlatform = slice?.XCFrameworkPlatformString ?? "ios";
            var simArch = slice?.Architecture ?? "arm64";
            var devArch = deviceSlice?.Architecture ?? "arm64";
            // Only default to "simulator" when no slice is provided (backward compat).
            // When a slice IS provided, use its actual variant (null for macOS/Catalyst device).
            var simVariant = slice != null ? slice.XCFrameworkPlatformVariant : "simulator";

            var simVariantEntry = simVariant != null
                ? $"""

                            <key>SupportedPlatformVariant</key>
                            <string>{simVariant}</string>
                    """
                : "";

            var devSliceId = deviceSlice?.SliceId ?? "ios-arm64";
            var devPlatform = deviceSlice?.XCFrameworkPlatformString ?? "ios";
            var devVariantEntry = deviceSlice?.XCFrameworkPlatformVariant != null
                ? $"""

                            <key>SupportedPlatformVariant</key>
                            <string>{deviceSlice.XCFrameworkPlatformVariant}</string>
                    """
                : "";

            var deviceSliceEntry = includeDeviceSlice
                ? $"""

                            <dict>
                                <key>BinaryPath</key>
                                <string>{wrapperModuleName}.framework/{wrapperModuleName}</string>
                                <key>LibraryIdentifier</key>
                                <string>{devSliceId}</string>
                                <key>LibraryPath</key>
                                <string>{wrapperModuleName}.framework</string>
                                <key>SupportedArchitectures</key>
                                <array>
                                    <string>{devArch}</string>
                                </array>
                                <key>SupportedPlatform</key>
                                <string>{devPlatform}</string>{devVariantEntry}
                            </dict>
                    """
                : "";

            var xcframeworkPlist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>BinaryPath</key>
                            <string>{wrapperModuleName}.framework/{wrapperModuleName}</string>
                            <key>LibraryIdentifier</key>
                            <string>{simSliceId}</string>
                            <key>LibraryPath</key>
                            <string>{wrapperModuleName}.framework</string>
                            <key>SupportedArchitectures</key>
                            <array>
                                <string>{simArch}</string>
                            </array>
                            <key>SupportedPlatform</key>
                            <string>{simPlatform}</string>{simVariantEntry}
                        </dict>{deviceSliceEntry}
                    </array>
                    <key>CFBundlePackageType</key>
                    <string>XFWK</string>
                    <key>XCFrameworkFormatVersion</key>
                    <string>1.0</string>
                </dict>
                </plist>
                """;
            File.WriteAllText(Path.Combine(xcframeworkPath, "Info.plist"), xcframeworkPlist);
        }

        /// <summary>
        /// Invokes xcrun swiftc to compile the wrapper library.
        /// </summary>
        /// <param name="additionalFrameworkSearchPaths">
        /// Additional -F search paths for dependency frameworks (e.g., from --framework-dependency).
        /// Emits a <c>-F</c> for every entry. May include internally-injected platform paths
        /// (XCTest <c>Developer/Library/Frameworks</c>) that exist purely for module resolution.
        /// </param>
        /// <param name="transitiveScanSearchPaths">
        /// The genuine user-passed dependency search paths to scan for sibling
        /// <c>--framework-dependency</c> companions to emit as transitive <c>-framework</c> flags.
        /// Distinct from <paramref name="additionalFrameworkSearchPaths"/> because that set may
        /// carry the internally-added XCTest platform path, which must drive <c>-F</c> (module
        /// resolution) but must NOT contribute <c>-framework</c> flags for test-host frameworks.
        /// Falls back to <paramref name="additionalFrameworkSearchPaths"/> when null.
        /// </param>
        internal static void InvokeSwiftCompiler(
            List<string> swiftFiles,
            string outputBinaryPath,
            string wrapperModuleName,
            string targetTriple,
            string sdkPath,
            string frameworkSearchPath,
            ICommandRunner commandRunner,
            ILogger logger,
            IReadOnlyList<string>? additionalFrameworkSearchPaths = null,
            IReadOnlyList<string>? precompiledShadowFrameworkPaths = null,
            IReadOnlyList<string>? thunkObjectFiles = null,
            string? originalModuleName = null,
            IReadOnlyList<string>? forceLoadBinaries = null,
            IReadOnlyList<string>? linkFrameworks = null,
            IReadOnlyList<string>? linkLibraries = null,
            IReadOnlyList<string>? transitiveScanSearchPaths = null)
        {
            var fileArgs = string.Join(" ", swiftFiles.Select(f => $"\"{f}\""));

            // Append thunk .o files — swiftc passes them through to the linker.
            // Also add -framework for the original module so the linker can resolve
            // external symbols (Tj dispatch thunks, metadata accessors) referenced by thunk .o code.
            var thunkLinkerFlags = "";
            if (thunkObjectFiles != null && thunkObjectFiles.Count > 0)
            {
                var objectArgs = string.Join(" ", thunkObjectFiles.Select(f => $"\"{f}\""));
                fileArgs += " " + objectArgs;

                // The Swift import in the wrapper .swift file auto-links the framework for Swift symbols,
                // but thunk .o files reference symbols via bl instructions that the linker needs to resolve.
                // Adding -framework explicitly ensures the linker includes the framework in its search.
                if (!string.IsNullOrEmpty(originalModuleName))
                {
                    thunkLinkerFlags = $"-Xlinker -framework -Xlinker {originalModuleName} ";
                }
            }

            var effectiveAdditionalFrameworkSearchPaths = additionalFrameworkSearchPaths != null
                ? new List<string>(additionalFrameworkSearchPaths)
                : new List<string>();
            var catalystIOSSupportPath = TryGetMacCatalystIOSSupportFrameworkPath(targetTriple, sdkPath);
            if (catalystIOSSupportPath != null &&
                !effectiveAdditionalFrameworkSearchPaths.Contains(catalystIOSSupportPath, StringComparer.Ordinal))
            {
                effectiveAdditionalFrameworkSearchPaths.Add(catalystIOSSupportPath);
            }

            var additionalFFlags = "";
            // Collect transitive framework names so we can explicitly link them. The wrapper
            // `import <bound module>` auto-links the bound framework, but transitive ObjC-only
            // deps and other non-imported
            // frameworks must be explicitly `-framework`-linked or the linker leaves their
            // OBJC_CLASS_$ / C++ / C symbols unresolved.
            //
            // The scan is limited to the genuine user-passed dependency search paths
            // (`transitiveScanSearchPaths`, falling back to `additionalFrameworkSearchPaths`),
            // NOT internally-added platform paths: the Catalyst iOS Support path (added below to
            // the -F-only `effectiveAdditionalFrameworkSearchPaths`, never to the scan set) and the
            // XCTest `Developer/Library/Frameworks` path (which the caller folds into the -F set it
            // passes as `additionalFrameworkSearchPaths`, but deliberately keeps OUT of the raw set
            // it passes here as `transitiveScanSearchPaths`). Those internal paths exist to satisfy
            // `import XCTest`-style module resolution; they must not contribute `-framework` flags
            // for Testing.framework, XCUIAutomation, etc. A binding library has no business pulling
            // in test-host frameworks. The thunk-only clang path scans the same raw set for parity.
            var transitiveFrameworkLinkerFlags = new System.Text.StringBuilder();
            var seenLinkedFrameworks = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(originalModuleName))
            {
                seenLinkedFrameworks.Add(originalModuleName);
            }
            // Emit -F for every effective search path so swiftc can resolve modules from any
            // of them, but scan only the user-passed deps for transitive -framework flags.
            foreach (var path in effectiveAdditionalFrameworkSearchPaths)
            {
                additionalFFlags += $" -F \"{path}\"";
            }
            // Only link a framework that has a real linker-consumable binary on disk —
            // header/swiftmodule-only slices have no binary and would trigger ld
            // "framework not found". CollectTransitiveFrameworkNames verifies the magic bytes
            // (not just file presence) and de-duplicates against already-seen names; the clang
            // thunk-only link path uses the same helper for parity.
            foreach (var frameworkName in
                CollectTransitiveFrameworkNames(transitiveScanSearchPaths ?? additionalFrameworkSearchPaths, seenLinkedFrameworks))
            {
                seenLinkedFrameworks.Add(frameworkName);
                transitiveFrameworkLinkerFlags.Append($"-Xlinker -framework -Xlinker {frameworkName} ");
            }

            // Pre-compiled shadow module paths for collision resolution: each is a shadow
            // framework directory containing a binary .swiftmodule produced by
            // PrecompileCollidingModule. Each must be emitted as a higher-priority -F BEFORE
            // the real framework search path, so swiftc finds the binary module first and
            // bypasses the textual swiftinterface that would re-trigger the collision. Multiple
            // shadow paths support concurrent collisions (e.g., bound-module collision + dep-module
            // collision + XCTest precompile) — order within the list is preserved.
            var precompiledFFlag = "";
            if (precompiledShadowFrameworkPaths != null)
            {
                foreach (var shadowPath in precompiledShadowFrameworkPaths)
                {
                    if (!string.IsNullOrEmpty(shadowPath))
                        precompiledFFlag += $"-F \"{shadowPath}\" ";
                }
            }

            // Gap 2: force-load the bound source framework's native when it ships as a
            // static `ar` archive, so the wrapper becomes the SOLE runtime carrier of its
            // ObjC classes (the consumer-side source NativeReference is dropped in tandem).
            // A bare `-framework <X>` against a static archive does lazy resolution — it
            // pulls only the members that satisfy symbols the wrapper code already
            // references, leaving any class not directly referenced out of the binary.
            // `-force_load` pulls every member. Referencing the same archive again via the
            // auto-linked `-framework` is a no-op (ld loads each member once), so
            // force_load + -framework on one archive is safe.
            //
            // Only the primary source framework is force-loaded here. Static *transitive
            // dependencies* are deliberately NOT force-loaded: a Swift-to-Swift dependency
            // carries its own native in its own wrapper nupkg, so force-loading it into
            // this wrapper too would double-embed it across both wrappers — worse than the
            // single duplicate this fix removes. That case needs its own design.
            var forceLoadFlags = new System.Text.StringBuilder();
            if (forceLoadBinaries != null)
            {
                foreach (var binary in forceLoadBinaries)
                {
                    if (!string.IsNullOrEmpty(binary) && System.IO.File.Exists(binary))
                        forceLoadFlags.Append($"-Xlinker -force_load -Xlinker \"{binary}\" ");
                }
            }

            // Author-declared link inputs (--link-framework / --link-library, surfaced by the
            // SDK as <SwiftLinkFramework> / <SwiftLinkLibrary>). A force-loaded static-archive
            // source (Gap 2) drags in every object it ships, including ones that reference Apple
            // system frameworks (CoreVideo, Metal, OpenGLES, Accelerate, …) and libc++. Those
            // dependencies are NOT discoverable: a C/C++ static archive built without
            // LC_LINKER_OPTION autolink hints exposes nothing to otool, and the generator does
            // not read CocoaPods podspecs. The binding author must declare them; here they
            // become real `-framework`/`-l` flags so the wrapper dylib carries the matching
            // LC_LOAD_DYLIB load commands and dyld resolves them transitively at app load.
            var explicitLinkFlags = new System.Text.StringBuilder();
            if (linkFrameworks != null)
            {
                foreach (var fw in linkFrameworks)
                {
                    if (string.IsNullOrWhiteSpace(fw)) continue;
                    if (seenLinkedFrameworks.Add(fw.Trim()))
                        explicitLinkFlags.Append($"-Xlinker -framework -Xlinker {fw.Trim()} ");
                }
            }
            if (linkLibraries != null)
            {
                var seenLinkedLibraries = new HashSet<string>(StringComparer.Ordinal);
                foreach (var lib in linkLibraries)
                {
                    if (string.IsNullOrWhiteSpace(lib)) continue;
                    // Accept either bare names ("c++") or already-prefixed ("-lc++"); normalize
                    // to a single -l<name> token so swiftc forwards it to the linker.
                    var name = lib.Trim();
                    if (name.StartsWith("-l", StringComparison.Ordinal))
                        name = name.Substring(2);
                    if (name.Length > 0 && seenLinkedLibraries.Add(name))
                        explicitLinkFlags.Append($"-l{name} ");
                }
            }

            string BuildArgs(string extraLinkerFlags) =>
                $"swiftc -emit-library -target {targetTriple} " +
                $"-sdk \"{sdkPath}\" " +
                $"-strict-concurrency=minimal " +   // Temporary: see roadmap for actor-aware emission
                $"{precompiledFFlag}-F \"{frameworkSearchPath}\"{additionalFFlags} " +
                $"-module-name {wrapperModuleName} " +
                $"{thunkLinkerFlags}" +
                $"{transitiveFrameworkLinkerFlags}" +
                $"{forceLoadFlags}" +
                $"{explicitLinkFlags}" +
                $"{extraLinkerFlags}" +
                $"-Xlinker -install_name -Xlinker @rpath/{wrapperModuleName}.framework/{wrapperModuleName} " +
                $"-o \"{outputBinaryPath}\" " +
                fileArgs;

            var args = BuildArgs("");

            logger.LogDebug("Invoking: xcrun {Args}", args);

            // Configurable wall-clock cap (raised default; env/property override) — the wrapper
            // compile is the step that times out under runner contention on large modules.
            var swiftcTimeoutMs = GeneratorTimeouts.ResolveSwiftcTimeoutMs();
            var (exitCode, stdout, stderr) = commandRunner.Run("xcrun", args, timeoutMs: swiftcTimeoutMs);

            // Profile-runtime auto-link retry: bound frameworks compiled with
            // `-fprofile-instr-generate` reference `___llvm_profile_runtime_user`,
            // which pulls in the unresolved `___llvm_profile_runtime` weak symbol
            // satisfied by `libclang_rt.profile_<platform>.a`. swiftc -emit-library
            // does not auto-link this archive (clang does, when invoked with the
            // matching -fprofile-* flag). Detect this specific link failure and
            // retry with the toolchain archive appended via -Xlinker. The archive
            // is benign for non-instrumented inputs (ar lazy-resolution leaves it
            // unused), so we only pay the retry cost for libraries that need it.
            if (exitCode != 0 && IsLLVMProfileRuntimeMissing(stderr))
            {
                var profileArchive = ResolveProfileRuntimeArchive(targetTriple, commandRunner, logger);
                if (!string.IsNullOrEmpty(profileArchive))
                {
                    logger.LogInformation(
                        "Retrying wrapper compile with profile runtime archive: {Archive}", profileArchive);
                    var retryFlags = $"-Xlinker \"{profileArchive}\" ";
                    var retryArgs = BuildArgs(retryFlags);
                    logger.LogDebug("Retrying: xcrun {Args}", retryArgs);
                    (exitCode, stdout, stderr) = commandRunner.Run("xcrun", retryArgs, timeoutMs: swiftcTimeoutMs);
                }
            }

            if (exitCode != 0)
            {
                logger.LogDebug("Full swiftc stderr:\n{Stderr}", stderr);
                // Dump full stderr to a sibling file so callers can inspect it even when the
                // preview is filtered/truncated. Stable filename next to the output binary.
                try
                {
                    var stderrPath = outputBinaryPath + ".swiftc-stderr.txt";
                    System.IO.File.WriteAllText(stderrPath, stderr);
                }
                catch { /* best-effort diagnostic dump */ }
                // Filter to error-bearing and Undefined-symbol lines so the preview surfaces the
                // real failure even when swiftc emits hundreds of warnings before the first error
                // (e.g. linker failures where "Undefined symbols" is several lines from "error:").
                var stderrForPreview = stderr;
                var allLines = stderr.Split('\n');
                var diagnosticLines = allLines.Where(l =>
                    l.Contains(" error:") ||
                    l.TrimStart().StartsWith("error:", StringComparison.Ordinal) ||
                    // Case-insensitive + singular-tolerant, matching BuildSystemLinkDependencyHint's
                    // gate, so the preview surfaces the link failure under any linker casing/wording.
                    l.Contains("undefined symbol", StringComparison.OrdinalIgnoreCase) ||
                    l.Contains("referenced from:") ||
                    l.TrimStart().StartsWith("\"_") ||
                    l.Contains("ld: ") ||
                    l.Contains("clang: error")).Take(40).ToList();
                if (diagnosticLines.Count > 0)
                {
                    stderrForPreview = string.Join("\n", diagnosticLines);
                }
                var errorPreview = stderrForPreview.Length > 4000 ? stderrForPreview.Substring(0, 4000) + "..." : stderrForPreview;

                var missingModules = ExtractMissingModules(stderr);
                var hint = "";
                if (missingModules.Count > 0)
                {
                    var moduleList = string.Join(", ", missingModules.Select(m => $"'{m}'"));
                    hint = $"\n\nMissing module(s): {moduleList}. " +
                           "Provide the xcframework(s) for these modules:\n" +
                           $"  CLI:  --framework-dependency /path/to/<Module>.xcframework (repeat for each)\n" +
                           $"  SDK:  Declare both items — SwiftFrameworkDependency for build-time " +
                           "framework resolution, PackageReference for NuGet restore:\n" +
                           $"          <SwiftFrameworkDependency Include=\"path/to/<Module>.xcframework\" " +
                           "PackageId=\"<Module>.Swift.iOS\" PackageVersion=\"1.0.0\" />\n" +
                           $"          <PackageReference Include=\"<Module>.Swift.iOS\" Version=\"1.0.0\" />\n" +
                           $"        For local source builds, use <ProjectReference> to the sibling " +
                           "binding csproj instead.";
                }

                // System-framework / libc++ undefined symbols come from a force-loaded static
                // archive whose link dependencies the generator cannot discover (no autolink
                // hints, no podspec reading). Point the author at the explicit declaration knobs
                // rather than leaving an opaque "Undefined symbols" wall.
                hint += BuildSystemLinkDependencyHint(
                    stderr, linkFrameworks, linkLibraries, forceLoadBinaries, commandRunner, logger);

                throw new InvalidOperationException(
                    $"Swift wrapper compilation failed (exit code {exitCode}): {errorPreview}{hint}");
            }
        }

        // Maps a few common families of Apple system-framework undefined symbols to the framework
        // (or library) that defines them, so a link failure caused by an undeclared system
        // dependency of a force-loaded static archive produces actionable guidance instead of a
        // raw "Undefined symbols" wall. Intentionally a small, high-signal table — not an attempt
        // to model every framework. Returns "" when the failure doesn't look like a system-link gap
        // so we never claim a misleading cause for an unrelated error.
        //
        // Two complementary symbol sources feed the table, so the author gets EVERY missing
        // -framework in one pass instead of a multi-round whack-a-mole:
        //   1. the linker's printed "Undefined symbols" list (always available on a link failure);
        //   2. the force-loaded static archive's own `nm -u` undefined symbols (when the archive
        //      path + a command runner are supplied) — authoritative and complete, so a linker that
        //      prints only part of its undefined-symbol list (backtrace cap, truncated transcript)
        //      can't cause the hint to under-report a framework.
        internal static string BuildSystemLinkDependencyHint(
            string stderr,
            IReadOnlyList<string>? alreadyDeclaredFrameworks = null,
            IReadOnlyList<string>? alreadyDeclaredLibraries = null,
            IReadOnlyList<string>? forceLoadedArchives = null,
            ICommandRunner? commandRunner = null,
            ILogger? logger = null)
        {
            // Gate case-insensitively: Apple's ld prints "Undefined symbols for architecture …",
            // but tolerate casing/wording drift (e.g. lld's "undefined symbol:"). This only decides
            // whether to *run* the probe — the per-symbol "_needle / StartsWith matching below still
            // gates what (if anything) is emitted, so a looser gate cannot manufacture a false hint.
            if (string.IsNullOrEmpty(stderr) ||
                !stderr.Contains("Undefined symbol", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            // (bare-symbol-prefix -> framework name). Matched two ways: against the linker stderr
            // anchored as `"` + needle (the linker renders `"_name", referenced from:`, so anchoring
            // at the quote keeps a generic stem like `_gl` from matching `_global…` mid-text), and
            // against the archive's `nm -u` symbols via StartsWith. Matches are de-duplicated.
            var frameworkProbes = new (string Needle, string Framework)[]
            {
                ("_CVPixelBuffer", "CoreVideo"),
                ("_CVMetalTexture", "CoreVideo"),
                ("_CVOpenGLESTexture", "CoreVideo"),
                ("_CVPixelFormat", "CoreVideo"),
                ("_CVBuffer", "CoreVideo"),
                ("_MTL", "Metal"),
                ("_OBJC_CLASS_$_MTL", "Metal"),
                ("_gl", "OpenGLES"),
                ("_OBJC_CLASS_$_EAGL", "OpenGLES"),
                ("_vDSP_", "Accelerate"),
                ("_vImage", "Accelerate"),
                ("_cblas_", "Accelerate"),
                ("_BNNS", "Accelerate"),
                ("_AudioComponent", "AudioToolbox"),
                ("_OBJC_CLASS_$_AV", "AVFoundation"),
                ("_CMTime", "CoreMedia"),
            };

            var declaredFw = new HashSet<string>(
                alreadyDeclaredFrameworks?.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim())
                    ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            // libc++ symbols are C++ mangled std:: names (start with __ZNSt / __ZSt) or std:: spellings.
            var declaredLibs = new HashSet<string>(
                (alreadyDeclaredLibraries ?? Enumerable.Empty<string>())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Trim().StartsWith("-l", StringComparison.Ordinal) ? l.Trim().Substring(2) : l.Trim()),
                StringComparer.Ordinal);

            var frameworks = new SortedSet<string>(StringComparer.Ordinal);

            // Source 1: the symbols the linker actually printed, rendered as `"_name", referenced
            // from:`. Extract each quoted symbol and run it through the same SymbolMatchesNeedle
            // test as the archive scan, so the `_gl` CamelCase guard (and any future anchoring)
            // applies here too — a quoted `"_global_ctors"` must not be misread as OpenGLES.
            foreach (var sym in ExtractQuotedSymbols(stderr))
            {
                foreach (var (needle, framework) in frameworkProbes)
                {
                    if (declaredFw.Contains(framework)) continue;
                    if (SymbolMatchesNeedle(sym, needle))
                        frameworks.Add(framework);
                }
            }
            var wantsCxx = !declaredLibs.Contains("c++") &&
                (stderr.Contains("std::") || stderr.Contains("__ZNSt") || stderr.Contains("__ZSt") ||
                 stderr.Contains("operator new") || stderr.Contains("operator delete"));

            // Source 2 (completeness): scan the force-loaded archive's undefined symbols directly.
            // Best-effort and error-path only (we're already inside a link failure, so no cost on
            // success); a failed/absent nm simply leaves Source 1's result in place. This catches
            // frameworks the linker's truncated list omitted. The match is prefix-based against the
            // probe table: each needle is an Apple-reserved C/ObjC namespace (`_vDSP_`, `_MTL`,
            // `_CVPixelBuffer`, `_OBJC_CLASS_$_…`), so in practice a table-matched undefined symbol
            // the archive references and hasn't already declared is genuinely a missing system dep.
            // It is high-signal, not provably collision-free — `SymbolMatchesNeedle` tightens the
            // one short lowercase stem (`_gl`) that could collide with ordinary symbols.
            if (forceLoadedArchives != null && forceLoadedArchives.Count > 0 &&
                commandRunner != null && logger != null)
            {
                var scan = NativeSymbolProbe.ScanUndefinedSymbols(forceLoadedArchives, commandRunner, logger);
                // Advisory hint only: act solely on a positive read. A systemic probe failure
                // (AllFailed) or no archive to read (NothingToProbe) simply leaves Source 1's
                // result in place — this is an error-path completeness aid, not the over-binding
                // guard, so it must never hard-fail.
                if (scan.Outcome == NativeSymbolProbeOutcome.Gathered)
                {
                    foreach (var sym in scan.UndefinedSymbols)
                    {
                        foreach (var (needle, framework) in frameworkProbes)
                        {
                            if (declaredFw.Contains(framework)) continue;
                            if (SymbolMatchesNeedle(sym, needle))
                                frameworks.Add(framework);
                        }
                        if (!wantsCxx && !declaredLibs.Contains("c++") &&
                            (sym.StartsWith("__ZNSt", StringComparison.Ordinal) ||
                             sym.StartsWith("__ZSt", StringComparison.Ordinal)))
                        {
                            wantsCxx = true;
                        }
                    }
                }
            }

            if (frameworks.Count == 0 && !wantsCxx)
                return string.Empty;

            var declareFlags = new System.Text.StringBuilder();
            var fwItems = new System.Text.StringBuilder();
            foreach (var fw in frameworks)
            {
                declareFlags.Append($" --link-framework {fw}");
                fwItems.Append($"          <SwiftLinkFramework Include=\"{fw}\" />\n");
            }
            if (wantsCxx)
            {
                declareFlags.Append(" --link-library c++");
                fwItems.Append("          <SwiftLinkLibrary Include=\"c++\" />\n");
            }

            return "\n\nThe undefined symbols above are defined by Apple system framework(s)/" +
                   "librarie(s) that a force-loaded static archive depends on. These cannot be " +
                   "auto-discovered (the archive carries no autolink hints and the generator does " +
                   "not read podspecs), so they must be declared explicitly:\n" +
                   $"  CLI:  add{declareFlags}\n" +
                   "  SDK:  add to the binding's <ItemGroup>:\n" +
                   fwItems.ToString().TrimEnd('\n');
        }

        // Matches one bare undefined symbol against a probe needle. Both sources feed it bare
        // symbols — the archive scan from `nm -u`, and the linker-stderr path after stripping the
        // surrounding quotes (see ExtractQuotedSymbols) — so the anchoring lives here once for both.
        // A short lowercase stem needs a tighter test than StartsWith: `_gl` is an OpenGL ES
        // entry-point stem (`glBindTexture` → `_glBindTexture`), and those are always `gl` +
        // CamelCase — requiring an uppercase letter after `_gl` keeps unrelated symbols like
        // `_global_ctors` / `_glue_init` from being misattributed to OpenGLES (whether they arrive
        // bare from the archive OR quoted as `"_global_ctors"` from the linker). Other needles carry
        // enough of their own prefix (`_MTL`, `_CVPixelBuffer`, `_vImage`, `_OBJC_CLASS_$_…`) to be
        // unambiguous on their own.
        private static bool SymbolMatchesNeedle(string sym, string needle)
        {
            if (!sym.StartsWith(needle, StringComparison.Ordinal))
                return false;
            if (needle == "_gl")
                return sym.Length > needle.Length && char.IsAsciiLetterUpper(sym[needle.Length]);
            return true;
        }

        // Pulls bare symbol names out of a linker "Undefined symbols" dump, which renders each as
        // `"_name", referenced from:`. Yielding the unquoted name lets Source 1 reuse the exact same
        // SymbolMatchesNeedle test as the archive scan (Source 2), so a quoted `"_global_ctors"`
        // cannot slip past the `_gl` guard that the bare-symbol path already enforces. Restricted to
        // `_`-prefixed tokens (every Mach-O symbol carries a leading underscore) so a stray quoted
        // path/word in the dump is not mistaken for a symbol.
        private static IEnumerable<string> ExtractQuotedSymbols(string stderr)
        {
            int i = 0;
            while (true)
            {
                int open = stderr.IndexOf('"', i);
                if (open < 0) yield break;
                int close = stderr.IndexOf('"', open + 1);
                if (close < 0) yield break;
                if (close > open + 1)
                {
                    var token = stderr.Substring(open + 1, close - open - 1);
                    if (token.StartsWith("_", StringComparison.Ordinal))
                        yield return token;
                }
                i = close + 1;
            }
        }

        // Verify a file begins with a linker-consumable binary header so the transitive-linker
        // scan only emits -framework for real binaries. Marker/manifest files at the expected
        // <Framework>.framework/<Framework> path would otherwise pass through and fail at link.
        // Accepts thin Mach-O, fat Mach-O (32- and 64-bit fat headers), and ar(1) static archives
        // (e.g. static-framework xcframeworks ship the framework binary as a `.a`-format file).
        /// <summary>
        /// Scans <paramref name="searchPaths"/> for <c>*.framework</c> directories that contain a
        /// linker-consumable binary (Mach-O thin/fat or an <c>ar</c> static archive) and returns
        /// their framework names — de-duplicated and excluding anything already in
        /// <paramref name="alreadySeen"/>. Shared by the swiftc link path (its transitive
        /// <c>-framework</c> flags) and the clang thunk-only link path so both surface the same
        /// sibling framework-dependency load commands from one probe (same rationale as
        /// <see cref="ResolvePrimaryForceLoad"/> centralising the force-load decision). Header/
        /// swiftmodule-only slices have no binary and are skipped so ld never sees a
        /// "framework not found".
        /// </summary>
        internal static List<string> CollectTransitiveFrameworkNames(
            IReadOnlyList<string>? searchPaths, IEnumerable<string>? alreadySeen = null)
        {
            var result = new List<string>();
            if (searchPaths == null)
                return result;
            var seen = new HashSet<string>(
                alreadySeen?.Where(s => !string.IsNullOrEmpty(s)) ?? Enumerable.Empty<string>(),
                StringComparer.Ordinal);
            foreach (var path in searchPaths)
            {
                if (!System.IO.Directory.Exists(path))
                    continue;
                foreach (var frameworkDir in System.IO.Directory.EnumerateDirectories(path, "*.framework"))
                {
                    var frameworkName = System.IO.Path.GetFileNameWithoutExtension(frameworkDir);
                    if (string.IsNullOrEmpty(frameworkName))
                        continue;
                    var binaryPath = System.IO.Path.Combine(frameworkDir, frameworkName);
                    if (!IsLinkableFrameworkBinary(binaryPath))
                        continue;
                    if (seen.Add(frameworkName))
                        result.Add(frameworkName);
                }
            }
            return result;
        }

        private static bool IsLinkableFrameworkBinary(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path))
                    return false;
                using var stream = System.IO.File.OpenRead(path);
                Span<byte> magic = stackalloc byte[8];
                var read = stream.Read(magic);
                if (BeginsWithMachOMagic(magic, read))
                    return true;
                // ar(1) static archive: "!<arch>\n" — 8 bytes (0x21 3C 61 72 63 68 3E 0A).
                if (read >= 8
                    && magic[0] == 0x21 && magic[1] == 0x3C && magic[2] == 0x61 && magic[3] == 0x72
                    && magic[4] == 0x63 && magic[5] == 0x68 && magic[6] == 0x3E && magic[7] == 0x0A)
                {
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// True when the first bytes of a file are a Mach-O thin or fat (universal) image magic,
        /// in either endianness. Shared by <see cref="IsLinkableFrameworkBinary"/> and
        /// <see cref="IsMachOImageFile"/> so the magic table is defined once.
        /// </summary>
        private static bool BeginsWithMachOMagic(ReadOnlySpan<byte> magic, int read)
        {
            if (read < 4)
                return false;
            // Mach-O thin (32/64-bit, both endians):
            //   MH_MAGIC_64 LE (CF FA ED FE), MH_MAGIC LE (CE FA ED FE)
            //   MH_MAGIC_64 BE (FE ED FA CF), MH_MAGIC BE (FE ED FA CE)
            // Mach-O fat (universal):
            //   FAT_MAGIC    (CA FE BA BE) / FAT_CIGAM    (BE BA FE CA)
            //   FAT_MAGIC_64 (CA FE BA BF) / FAT_CIGAM_64 (BF BA FE CA)
            return
                   (magic[0] == 0xCF && magic[1] == 0xFA && magic[2] == 0xED && magic[3] == 0xFE)
                || (magic[0] == 0xCE && magic[1] == 0xFA && magic[2] == 0xED && magic[3] == 0xFE)
                || (magic[0] == 0xFE && magic[1] == 0xED && magic[2] == 0xFA && magic[3] == 0xCF)
                || (magic[0] == 0xFE && magic[1] == 0xED && magic[2] == 0xFA && magic[3] == 0xCE)
                || (magic[0] == 0xCA && magic[1] == 0xFE && magic[2] == 0xBA && magic[3] == 0xBE)
                || (magic[0] == 0xCA && magic[1] == 0xFE && magic[2] == 0xBA && magic[3] == 0xBF)
                || (magic[0] == 0xBE && magic[1] == 0xBA && magic[2] == 0xFE && magic[3] == 0xCA)
                || (magic[0] == 0xBF && magic[1] == 0xBA && magic[2] == 0xFE && magic[3] == 0xCA);
        }

        /// <summary>
        /// True when <paramref name="path"/> exists and begins with a Mach-O thin/fat magic. Used
        /// to reject a truncated/corrupt wrapper binary (an <c>ar</c> archive is NOT accepted here —
        /// a compiled framework slice is always a linked Mach-O image, never a static archive).
        /// </summary>
        internal static bool IsMachOImageFile(string path)
        {
            try
            {
                if (!System.IO.File.Exists(path))
                    return false;
                using var stream = System.IO.File.OpenRead(path);
                Span<byte> magic = stackalloc byte[8];
                var read = stream.Read(magic);
                return BeginsWithMachOMagic(magic, read);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gap 2 primary-native force-load decision. Returns a single-element list containing
        /// <paramref name="dylibPath"/> when it is a static <c>ar</c> archive (so the wrapper
        /// force-loads it and becomes the sole carrier of its ObjC classes), or <c>null</c>
        /// for a dynamic library / TBD text stub / missing path. Centralised so the swiftc and
        /// clang link paths make the identical decision from one probe.
        /// </summary>
        private static List<string>? ResolvePrimaryForceLoad(
            string dylibPath, ICommandRunner commandRunner, ILogger logger)
        {
            if (NativeLinkageProbe.Detect(dylibPath, commandRunner, logger) != NativeLinkage.Static)
                return null;

            logger.LogInformation(
                "Source native is a static archive — force-loading into the wrapper as the sole carrier: {Path}",
                dylibPath);
            return new List<string> { dylibPath };
        }

        internal static string? TryGetMacCatalystIOSSupportFrameworkPath(string targetTriple, string sdkPath)
        {
            if (!targetTriple.Contains("-macabi", StringComparison.Ordinal))
                return null;

            var iOSSupportFrameworksPath = Path.Combine(
                sdkPath, "System", "iOSSupport", "System", "Library", "Frameworks");
            return Directory.Exists(iOSSupportFrameworksPath) ? iOSSupportFrameworksPath : null;
        }

        /// <summary>
        /// Extracts distinct missing module names from swiftc stderr output.
        /// Matches the pattern: error: no such module 'ModuleName'
        /// </summary>
        internal static List<string> ExtractMissingModules(string stderr)
        {
            var matches = Regex.Matches(stderr, @"no such module '([^']+)'");
            return matches.Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Detects whether swiftc stderr describes the LLVM profile-runtime
        /// undefined-symbol failure produced when the bound framework was built
        /// with profile coverage instrumentation but the wrapper link line does
        /// not include the matching libclang_rt.profile_*.a archive.
        /// </summary>
        internal static bool IsLLVMProfileRuntimeMissing(string stderr)
        {
            if (string.IsNullOrEmpty(stderr))
                return false;
            // The exact ld diagnostic shape:
            //   "___llvm_profile_runtime", referenced from:
            //       ___llvm_profile_runtime_user in <frameworkBinary>(<obj>)
            // Matching on the bare symbol token is sufficient — it never appears
            // outside of profile-instrumentation link errors.
            return stderr.Contains("___llvm_profile_runtime", StringComparison.Ordinal);
        }

        /// <summary>
        /// Maps a swiftc target triple to the platform suffix used by clang's
        /// per-platform profile runtime archive (libclang_rt.profile_<suffix>.a).
        /// Returns null when the triple is not recognised — callers fall back to
        /// re-throwing the original link error rather than guessing.
        /// </summary>
        internal static string? MapTargetTripleToProfilePlatform(string targetTriple)
        {
            if (string.IsNullOrEmpty(targetTriple))
                return null;
            bool isSimulator = targetTriple.EndsWith("-simulator", StringComparison.Ordinal);
            bool isMacCatalyst = targetTriple.Contains("-macabi", StringComparison.Ordinal);
            // Order matters: macabi check before plain ios so iOS Mac Catalyst
            // doesn't fall into the device-iOS branch. Mac Catalyst links the
            // host's libclang_rt.profile_osx.a — clang's `-target *-macabi
            // -fprofile-instr-generate -###` confirms; the toolchain ships no
            // standalone `iosmaccatalyst` profile archive.
            if (isMacCatalyst)
                return "osx";
            if (targetTriple.Contains("-ios", StringComparison.Ordinal))
                return isSimulator ? "iossim" : "ios";
            if (targetTriple.Contains("-tvos", StringComparison.Ordinal))
                return isSimulator ? "tvossim" : "tvos";
            if (targetTriple.Contains("-watchos", StringComparison.Ordinal))
                return isSimulator ? "watchossim" : "watchos";
            if (targetTriple.Contains("-xros", StringComparison.Ordinal)
                || targetTriple.Contains("-visionos", StringComparison.Ordinal))
                return isSimulator ? "xrossim" : "xros";
            if (targetTriple.Contains("-macos", StringComparison.Ordinal)
                || targetTriple.Contains("-darwin", StringComparison.Ordinal))
                return "osx";
            return null;
        }

        /// <summary>
        /// Resolves the absolute path to libclang_rt.profile_&lt;platform&gt;.a inside
        /// the active Xcode toolchain. Returns null when the platform is unknown
        /// or the archive cannot be located so the caller surfaces the original
        /// linker error rather than a synthesised one.
        /// </summary>
        internal static string? ResolveProfileRuntimeArchive(
            string targetTriple, ICommandRunner commandRunner, ILogger logger)
        {
            var platform = MapTargetTripleToProfilePlatform(targetTriple);
            if (platform == null)
            {
                logger.LogDebug(
                    "Unable to map target triple '{Triple}' to profile runtime platform.",
                    targetTriple);
                return null;
            }
            var (exitCode, stdout, _) = commandRunner.Run(
                "xcrun", "clang -print-resource-dir", timeoutMs: 30000);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                logger.LogDebug(
                    "xcrun clang -print-resource-dir failed (exit {Code}); cannot locate profile runtime archive.",
                    exitCode);
                return null;
            }
            var resourceDir = stdout.Trim();
            var archivePath = Path.Combine(
                resourceDir, "lib", "darwin", $"libclang_rt.profile_{platform}.a");
            if (!File.Exists(archivePath))
            {
                logger.LogDebug(
                    "Profile runtime archive not found at expected path: {Path}", archivePath);
                return null;
            }
            return archivePath;
        }

        /// <summary>
        /// Detects SPM resource bundle names required by the framework binary.
        /// SPM-generated resource_bundle_accessor.swift contains the fatalError message
        /// "unable to find bundle named {BundleName}" — we search for this pattern
        /// in the binary to extract the expected bundle name(s).
        /// </summary>
        internal static List<string> DetectResourceBundleNames(
            string dylibPath, ICommandRunner commandRunner, ILogger logger)
        {
            var bundleNames = new List<string>();
            try
            {
                // Use grep -ao on the binary to extract the bundle name pattern directly.
                // grep -a treats binary as text, -o outputs only the matching portion.
                // Exit code 1 = no match (not an error).
                var (exitCode, stdout, _) = commandRunner.Run(
                    "grep", $"-ao \"unable to find bundle named [A-Za-z0-9_]*\" \"{dylibPath}\"",
                    timeoutMs: 30000);

                if (exitCode == 1 || string.IsNullOrWhiteSpace(stdout))
                    return bundleNames; // grep exit 1 = no match
                if (exitCode != 0)
                {
                    logger.LogDebug("Resource bundle detection: grep exited with code {Code}", exitCode);
                    return bundleNames;
                }

                const string marker = "unable to find bundle named ";
                foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith(marker, StringComparison.Ordinal))
                    {
                        var name = trimmed.Substring(marker.Length).Trim();
                        if (!string.IsNullOrEmpty(name) && !bundleNames.Contains(name))
                            bundleNames.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug("Resource bundle detection failed (non-fatal): {Message}", ex.Message);
            }

            return bundleNames;
        }

        /// <summary>
        /// Copies real SPM resource bundles from the source framework into the output
        /// directory, or creates empty stubs when the real bundle cannot be located.
        /// SPM-generated resource_bundle_accessor.swift searches Bundle.main for named bundles.
        /// Bundles placed in the output directory are picked up by Sdk.targets (via
        /// _SwiftResourceBundles item) and copied to the app bundle root, where the
        /// accessor will discover them at runtime on both simulator and device.
        /// </summary>
        internal static void CreateResourceBundleStubs(
            List<string> bundleNames, string outputDirectory, ILogger logger,
            string? sourceDylibPath = null)
        {
            // The real .bundle directories live as siblings of the dylib inside the
            // framework directory (e.g., Library.xcframework/<slice>/Library.framework/<Name>.bundle/).
            string? sourceFrameworkDir = null;
            if (!string.IsNullOrEmpty(sourceDylibPath))
                sourceFrameworkDir = Path.GetDirectoryName(sourceDylibPath);

            foreach (var name in bundleNames)
            {
                var destBundlePath = Path.Combine(outputDirectory, $"{name}.bundle");

                // Try to copy the real bundle from the source framework
                var realBundle = sourceFrameworkDir != null
                    ? Path.Combine(sourceFrameworkDir, $"{name}.bundle")
                    : null;

                if (realBundle != null && Directory.Exists(realBundle))
                {
                    CopyDirectory(realBundle, destBundlePath);
                    logger.LogInformation("Copied real resource bundle '{Name}.bundle' from source framework", name);
                }
                else
                {
                    // Fall back to empty stub — prevents fatalError in resource_bundle_accessor.swift
                    // but resources (images, strings, JSON, etc.) will not be available at runtime.
                    if (!Directory.Exists(destBundlePath))
                        Directory.CreateDirectory(destBundlePath);
                    var placeholder = Path.Combine(destBundlePath, "_sbw_stub");
                    if (!File.Exists(placeholder))
                        File.WriteAllText(placeholder, "");
                    logger.LogWarning("Could not locate real resource bundle '{Name}.bundle' in source framework — created empty stub. " +
                        "Resources loaded from Bundle.module will not be available at runtime.", name);
                }
            }

            // Write manifest for Sdk.targets to discover the bundle names
            var manifestPath = Path.Combine(outputDirectory, "_resource-bundles.txt");
            File.WriteAllLines(manifestPath, bundleNames);

            logger.LogInformation("Prepared resource bundle(s) for {Count} bundle(s): {Names}",
                bundleNames.Count, string.Join(", ", bundleNames));
        }

        /// <summary>
        /// Recursively copies a directory tree.
        /// </summary>
        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
            }
            foreach (var subDir in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(subDir, Path.Combine(destDir, Path.GetFileName(subDir)));
            }
        }

        /// <summary>
        /// One module-type collision to resolve in a single shadow .swiftmodule. The bound
        /// module's own self-collision (EC-1) usually pairs with a non-empty <see cref="NestedTypes"/>
        /// set so nested references like <c>Module.NestedType</c> survive prefix stripping
        /// (EC-18); dep-module and XCTest collisions have no nested-type carveouts and pass
        /// <see cref="NestedTypes"/> as null.
        /// </summary>
        internal readonly record struct CollisionPatchTarget(string Module, HashSet<string>? NestedTypes);

        /// <summary>
        /// Stages a sanitized shadow framework that swiftc/ld will see before the real slice
        /// via higher-priority <c>-F</c> precedence. Fires on two independent triggers:
        ///
        /// <list type="number">
        /// <item><b>Collision (EC-1):</b> when a public type has the same name as its module,
        /// the textual <c>.swiftinterface</c> fails to import because Swift misresolves
        /// <c>Module.Type</c> as <c>Class.NestedType</c>. We patch the interface to strip the
        /// module prefix and precompile to a binary <c>.swiftmodule</c>, then drop the textual
        /// interface from the shadow so swiftc binds against the binary.</item>
        /// <item><b>Private interface sanitization:</b> when the source swiftmodule dir contains
        /// a <c>*.private.swiftinterface</c>, swiftc parses BOTH public and private interfaces
        /// during module materialization. A malformed private interface (e.g., GTMAppAuth's,
        /// which references a class-shadowed module name) kills the load even though the
        /// wrapper never uses <c>@_spi import</c>. We copy only the public interface into the
        /// shadow so swiftc never sees the private one.</item>
        /// </list>
        ///
        /// <para>
        /// All collisions for the same bound module/slice MUST be passed in a single call:
        /// the shadow .swiftmodule path is keyed on the bound module name + target triple,
        /// so a second call would overwrite the first slice's binary with one that only
        /// patches the second collision. The combined regex handles every supplied module
        /// in a single pass.
        /// </para>
        /// </summary>
        /// <returns>Framework search path to prepend as -F (higher priority), or null when
        /// nothing needs staging (no collisions and no private interface) or on failure.</returns>
        internal static string? PrecompileSanitizedShadowFramework(
            string moduleName,
            string swiftInterfacePath,
            string targetTriple,
            string sdkPath,
            string buildDir,
            ICommandRunner commandRunner,
            ILogger logger,
            IReadOnlyList<CollisionPatchTarget> collisions,
            IReadOnlyList<string>? additionalFrameworkSearchPaths = null)
        {
            if (string.IsNullOrEmpty(swiftInterfacePath))
                return null;

            var sourceModuleDir = Path.GetDirectoryName(swiftInterfacePath);
            bool hasCollisions = collisions != null && collisions.Count > 0;
            bool hasPrivateInterface = sourceModuleDir != null
                && Directory.Exists(sourceModuleDir)
                && Directory.EnumerateFiles(sourceModuleDir, "*.private.swiftinterface").Any();

            if (!hasCollisions && !hasPrivateInterface)
                return null;

            try
            {
                // Target-specific shadow dir keeps simulator/device staging from colliding.
                var safeTriple = targetTriple.Replace("/", "_");
                var precompileDir = Path.Combine(buildDir, $"precompiled-{safeTriple}");
                Directory.CreateDirectory(precompileDir);

                // Mirror the framework structure:
                //   {precompileDir}/{Module}.framework/
                //     ├── {Module}                            (symlink to real binary, defensive)
                //     ├── Headers/                            (symlinked from real, when present)
                //     └── Modules/
                //         ├── module.modulemap                (real or minimal Swift-only)
                //         └── {Module}.swiftmodule/
                //             └── (binary .swiftmodule | public .swiftinterface)
                var frameworkDir = Path.Combine(precompileDir, $"{moduleName}.framework");
                var shadowModulesDir = Path.Combine(frameworkDir, "Modules");
                var fwDir = Path.Combine(shadowModulesDir, $"{moduleName}.swiftmodule");
                Directory.CreateDirectory(fwDir);

                // Derive the slice framework search path from swiftInterfacePath:
                //   .../<slice>/<Module>.framework/Modules/<Module>.swiftmodule/<arch>.swiftinterface
                var swiftModuleParent = Path.GetDirectoryName(swiftInterfacePath); // .swiftmodule dir
                var modulesParent = Path.GetDirectoryName(swiftModuleParent);      // Modules dir
                var realFrameworkDir = Path.GetDirectoryName(modulesParent);       // <Module>.framework
                var sliceSearchPath = Path.GetDirectoryName(realFrameworkDir);     // slice dir (for -F)

                // Mirror the source modulemap + public Headers/ into the shadow so umbrella
                // header references in the bound .swiftinterface resolve. A Swift-only
                // modulemap would strip them, and same-module ObjC names (class wrappers,
                // constants) would fail to resolve. Falls back to a minimal modulemap only when
                // the source framework has no public modulemap (pure-Swift interface-only).
                StageShadowFrameworkLayout(moduleName, shadowModulesDir, frameworkDir, realFrameworkDir, logger);

                // Stage the real framework binary into the shadow at
                // <Module>.framework/<Module>. ld walks -F in order looking for that file
                // when resolving -framework <Module>; without this the linker falls
                // through to the real -F, a behavior detail we'd rather not depend on.
                // Prefer a symlink (cheap); copy-fallback if symlink creation isn't allowed
                // (sandbox/policy). Skip silently if the source has no binary (interface-only
                // frameworks).
                if (!string.IsNullOrEmpty(realFrameworkDir))
                {
                    var realBinaryPath = Path.Combine(realFrameworkDir!, moduleName);
                    if (File.Exists(realBinaryPath))
                    {
                        var shadowBinaryPath = Path.Combine(frameworkDir, moduleName);
                        try
                        {
                            File.CreateSymbolicLink(shadowBinaryPath, realBinaryPath);
                        }
                        catch (Exception symlinkEx)
                        {
                            try
                            {
                                // Delete any pre-existing link/file at the destination first.
                                // File.Copy(overwrite:true) follows an existing symlink at the
                                // destination, which could write through to the real framework
                                // binary if a stale symlink survives in a reused build dir.
                                if (File.Exists(shadowBinaryPath) || new FileInfo(shadowBinaryPath).LinkTarget != null)
                                {
                                    File.Delete(shadowBinaryPath);
                                }
                                File.Copy(realBinaryPath, shadowBinaryPath);
                            }
                            catch (Exception copyEx)
                            {
                                logger.LogDebug(
                                    "Shadow framework binary staging skipped (non-fatal): symlink={Symlink}, copy={Copy}",
                                    symlinkEx.Message, copyEx.Message);
                            }
                        }
                    }
                }

                if (hasCollisions)
                {
                    return StageCollisionPatchedShadow(
                        moduleName, swiftInterfacePath, sourceModuleDir, fwDir,
                        sliceSearchPath, precompileDir, targetTriple, sdkPath,
                        commandRunner, logger, collisions!, additionalFrameworkSearchPaths);
                }

                // Private-interface-only sanitization: copy public .swiftinterface files into
                // the shadow as-is. The public interface compiles on its own; swiftc reading
                // from the shadow swiftmodule dir never sees the private interface. No
                // precompile step needed — that's reserved for collision patching where the
                // textual interface must be rewritten.
                if (sourceModuleDir != null)
                {
                    foreach (var ifaceFile in Directory.GetFiles(sourceModuleDir, "*.swiftinterface"))
                    {
                        if (!SwiftInterfaceVariant.IsPublic(ifaceFile))
                            continue;
                        File.Copy(ifaceFile, Path.Combine(fwDir, Path.GetFileName(ifaceFile)), overwrite: true);
                    }
                    // Copy .swiftdoc files when present — swiftc emits a warning without them
                    // and they're cheap to mirror.
                    foreach (var docFile in Directory.GetFiles(sourceModuleDir, "*.swiftdoc"))
                    {
                        File.Copy(docFile, Path.Combine(fwDir, Path.GetFileName(docFile)), overwrite: true);
                    }
                }

                logger.LogInformation(
                    "Staged sanitized shadow framework for '{Module}' (private.swiftinterface in source dir; wrapper does not consume @_spi).",
                    moduleName);
                return precompileDir;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Shadow framework staging failed (non-fatal): {Message}", ex.Message);
                return null;
            }
        }

        // Compatibility alias for the prior name; new callers should use the renamed entry point.
        internal static string? PrecompileCollidingModule(
            string moduleName,
            string swiftInterfacePath,
            string targetTriple,
            string sdkPath,
            string buildDir,
            ICommandRunner commandRunner,
            ILogger logger,
            IReadOnlyList<CollisionPatchTarget> collisions,
            IReadOnlyList<string>? additionalFrameworkSearchPaths = null)
            => PrecompileSanitizedShadowFramework(
                moduleName, swiftInterfacePath, targetTriple, sdkPath, buildDir,
                commandRunner, logger, collisions, additionalFrameworkSearchPaths);

        /// <summary>
        /// Mirrors the source framework's public modulemap + Headers/ into the shadow
        /// framework dir. Falls back to a minimal Swift-only modulemap when the source
        /// framework has no public modulemap (rare — pure-Swift interface-only). The
        /// <c>module.private.modulemap</c> is never staged: that's the surface we're
        /// sanitizing, and copying it would re-expose the @_spi headers we excluded.
        /// </summary>
        internal static void StageShadowFrameworkLayout(
            string moduleName,
            string shadowModulesDir,
            string shadowFrameworkDir,
            string? realFrameworkDir,
            ILogger logger)
        {
            var shadowModulemap = Path.Combine(shadowModulesDir, "module.modulemap");
            bool stagedRealModulemap = false;
            if (!string.IsNullOrEmpty(realFrameworkDir))
            {
                var realModulemap = Path.Combine(realFrameworkDir!, "Modules", "module.modulemap");
                if (File.Exists(realModulemap))
                {
                    try
                    {
                        File.Copy(realModulemap, shadowModulemap, overwrite: true);
                        stagedRealModulemap = true;
                    }
                    catch (IOException ex)
                    {
                        logger.LogDebug(
                            "Shadow modulemap copy failed for '{Module}' (falling back to minimal): {Message}",
                            moduleName, ex.Message);
                    }
                }
            }

            if (!stagedRealModulemap)
            {
                var minimalModulemap = $"framework module {moduleName} {{\n}}\n";
                File.WriteAllText(shadowModulemap, minimalModulemap);
                return;
            }

            // Stage public Headers/ and PrivateHeaders/ so every header referenced by the
            // copied modulemap resolves. Frameworks routinely declare `header "x.h"` in the
            // public modulemap pointing at a file under PrivateHeaders/ (e.g., a library's
            // private config header) — swiftc searches both directories for module.modulemap
            // references. The @_spi surface lives in module.private.modulemap (never staged),
            // not in PrivateHeaders/.
            if (string.IsNullOrEmpty(realFrameworkDir))
                return;
            StageShadowHeaderDir(moduleName, "Headers", realFrameworkDir!, shadowFrameworkDir, logger);
            StageShadowHeaderDir(moduleName, "PrivateHeaders", realFrameworkDir!, shadowFrameworkDir, logger);
        }

        private static void StageShadowHeaderDir(
            string moduleName,
            string dirName,
            string realFrameworkDir,
            string shadowFrameworkDir,
            ILogger logger)
        {
            var realDir = Path.Combine(realFrameworkDir, dirName);
            if (!Directory.Exists(realDir))
                return;
            var shadowDir = Path.Combine(shadowFrameworkDir, dirName);
            if (Directory.Exists(shadowDir) || File.Exists(shadowDir))
            {
                try { Directory.Delete(shadowDir, recursive: true); }
                catch (IOException) { try { File.Delete(shadowDir); } catch (IOException) { } }
            }
            try
            {
                Directory.CreateSymbolicLink(shadowDir, realDir);
                return;
            }
            catch (Exception symEx)
            {
                logger.LogDebug(
                    "Shadow {Dir}/ symlink failed for '{Module}', copying recursively: {Message}",
                    dirName, moduleName, symEx.Message);
            }
            try
            {
                CopyDirectoryRecursive(realDir, shadowDir);
            }
            catch (IOException ex)
            {
                logger.LogWarning(
                    "Shadow {Dir}/ staging failed for '{Module}' (modulemap header may not resolve): {Message}",
                    dirName, moduleName, ex.Message);
            }
        }

        private static void CopyDirectoryRecursive(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.EnumerateDirectories(source))
                CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }

        private static string? StageCollisionPatchedShadow(
            string moduleName,
            string swiftInterfacePath,
            string? sourceModuleDir,
            string fwDir,
            string? sliceSearchPath,
            string precompileDir,
            string targetTriple,
            string sdkPath,
            ICommandRunner commandRunner,
            ILogger logger,
            IReadOnlyList<CollisionPatchTarget> collisions,
            IReadOnlyList<string>? additionalFrameworkSearchPaths)
        {
            // Combined collision regex spanning every supplied module. Sort longest-first so
            // `Foo` can never partial-match inside `FooBar` via left-to-right alternation.
            // Group 1 is the matched module name (used to dispatch the nested-type carveout);
            // group 2 is the trailing type chain.
            var nestedByModule = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var c in collisions)
            {
                if (string.IsNullOrEmpty(c.Module)) continue;
                if (c.NestedTypes != null && c.NestedTypes.Count > 0)
                    nestedByModule[c.Module] = c.NestedTypes;
            }
            var sortedModules = collisions
                .Where(c => !string.IsNullOrEmpty(c.Module))
                .Select(c => c.Module)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(m => m.Length)
                .ToList();
            if (sortedModules.Count == 0)
                return null;
            var alternation = string.Join("|", sortedModules.Select(System.Text.RegularExpressions.Regex.Escape));
            var collisionPattern = new System.Text.RegularExpressions.Regex(
                @"\b(" + alternation + @")\.(\w+(?:\.\w+)*)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

            // Copy and patch the primary .swiftinterface used as the precompile input.
            var patchedInterfacePath = Path.Combine(precompileDir, Path.GetFileName(swiftInterfacePath));
            PatchSwiftInterface(swiftInterfacePath, patchedInterfacePath, collisionPattern, nestedByModule);

            // Copy and patch every other public swiftinterface from the source .swiftmodule
            // dir into the shadow. Access-level-qualified interfaces (.private/.package) are
            // skipped: the binary .swiftmodule is self-contained for public API resolution,
            // and those variants can contain types from colliding modules that fail to
            // resolve even after patching.
            if (sourceModuleDir != null)
            {
                foreach (var ifaceFile in Directory.GetFiles(sourceModuleDir, "*.swiftinterface"))
                {
                    if (!SwiftInterfaceVariant.IsPublic(ifaceFile))
                        continue;
                    var destPath = Path.Combine(fwDir, Path.GetFileName(ifaceFile));
                    PatchSwiftInterface(ifaceFile, destPath, collisionPattern, nestedByModule);
                }
            }

            // Framework .swiftmodule dirs use versionless arch names (e.g.,
            // "arm64-apple-ios-simulator") while GetTargetTriple() includes the OS version
            // (e.g., "arm64-apple-ios17.0-simulator"). swiftc only finds the binary module
            // when the filename matches the versionless pattern, so derive it from the input
            // swiftinterface filename rather than the target triple.
            var interfaceBaseName = Path.GetFileNameWithoutExtension(
                Path.GetFileName(swiftInterfacePath));
            var outputModulePath = Path.Combine(fwDir, $"{interfaceBaseName}.swiftmodule");

            var fwSearchFlag = "";
            if (!string.IsNullOrEmpty(sliceSearchPath) && Directory.Exists(sliceSearchPath))
            {
                fwSearchFlag = $"-F \"{sliceSearchPath}\" ";
            }
            if (additionalFrameworkSearchPaths != null)
            {
                foreach (var addPath in additionalFrameworkSearchPaths)
                {
                    fwSearchFlag += $"-F \"{addPath}\" ";
                }
            }

            var args = $"swift-frontend -compile-module-from-interface " +
                       $"\"{patchedInterfacePath}\" " +
                       $"-target {targetTriple} " +
                       $"-module-name {moduleName} " +
                       $"-sdk \"{sdkPath}\" " +
                       $"{fwSearchFlag}" +
                       $"-o \"{outputModulePath}\"";

            var (exitCode, _, stderr) = commandRunner.Run("xcrun", args, timeoutMs: 60000);

            if (exitCode != 0)
            {
                logger.LogWarning(
                    "Pre-compilation of bound module '{Module}' with collisions [{Collisions}] failed (non-fatal): {Error}",
                    moduleName, string.Join(",", sortedModules),
                    stderr.Length > 500 ? stderr.Substring(0, 500) : stderr);
                return null;
            }

            // Drop textual .swiftinterface files from the shadow. The binary .swiftmodule is
            // self-contained; leaving the textual interfaces causes swiftc to fall back to
            // re-parsing them (which would hit the unpatched collision shape again).
            foreach (var ifaceFile in Directory.GetFiles(fwDir, "*.swiftinterface"))
            {
                File.Delete(ifaceFile);
            }

            logger.LogInformation(
                "Pre-compiled patched .swiftinterface for collision resolution ({Module}; collisions=[{Collisions}]).",
                moduleName, string.Join(",", sortedModules));
            return precompileDir;
        }
        /// <summary>
        /// Patches a .swiftinterface file by stripping the module prefix from type references
        /// to resolve module/type name collisions. Preserves references to types nested inside
        /// the colliding class (EC-18). When multiple modules collide, the combined regex
        /// captures the matched module in group 1; the per-module nested-type carveout is
        /// applied via <paramref name="nestedTypesByModule"/>.
        /// </summary>
        private static void PatchSwiftInterface(string sourcePath, string destPath,
            System.Text.RegularExpressions.Regex collisionPattern,
            IReadOnlyDictionary<string, HashSet<string>> nestedTypesByModule)
        {
            var content = File.ReadAllText(sourcePath);
            var patched = new System.Text.StringBuilder();
            foreach (var line in content.Split('\n'))
            {
                if (line.TrimStart().StartsWith("import ", StringComparison.Ordinal))
                {
                    patched.Append(line).Append('\n');
                    continue;
                }
                patched.Append(collisionPattern.Replace(line, match =>
                {
                    // Group 1 = matched module name. Group 2 = trailing type chain.
                    // Single-group legacy patterns (no module capture) fall back to
                    // Group 1 as the trailing chain — preserves the old test contract.
                    string moduleMatched;
                    string trailingChain;
                    if (match.Groups.Count >= 3 && match.Groups[2].Success)
                    {
                        moduleMatched = match.Groups[1].Value;
                        trailingChain = match.Groups[2].Value;
                    }
                    else
                    {
                        moduleMatched = string.Empty;
                        trailingChain = match.Groups[1].Value;
                    }

                    var dotIdx = trailingChain.IndexOf('.');
                    var topLevelName = dotIdx >= 0 ? trailingChain.Substring(0, dotIdx) : trailingChain;

                    if (!string.IsNullOrEmpty(moduleMatched) &&
                        nestedTypesByModule.TryGetValue(moduleMatched, out var nested) &&
                        nested.Contains(topLevelName))
                    {
                        return match.Value;
                    }

                    return trailingChain;
                })).Append('\n');
            }
            File.WriteAllText(destPath, patched.ToString());
        }
    }
}
