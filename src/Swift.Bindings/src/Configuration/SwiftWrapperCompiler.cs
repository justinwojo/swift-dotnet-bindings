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
        /// Number of architecture slices in the wrapper xcframework (1 = simulator only, 2 = both).
        /// </summary>
        public int SliceCount { get; init; } = 1;

        /// <summary>
        /// Set of @_cdecl / @_silgen_name symbols that were stripped from the wrapper.
        /// Used by C# co-gater to suppress P/Invokes targeting these symbols.
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
        /// <param name="moduleName">The Swift module name (e.g., "Nuke").</param>
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
            string? resolvedArchitecture = null)
        {
            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            var simSlice = pi.GetSlice(true);
            if (!string.IsNullOrEmpty(resolvedArchitecture))
                simSlice = simSlice with { Architecture = resolvedArchitecture };
            return CompileSlice(outputDirectory, moduleName, frameworkSearchPath, dylibPath,
                simSlice, logger, commandRunner, internalTypeNames,
                additionalFrameworkSearchPaths, moduleNameForCollision: moduleNameForCollision,
                nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                swiftInterfacePath: swiftInterfacePath,
                skipThunkCompilation: skipThunkCompilation);
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
            string? swiftInterfacePath = null)
        {
            commandRunner ??= new SystemCommandRunner();
            var wrapperModuleName = $"{moduleName}SwiftBindings";

            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            // Override architecture from resolution (defense-in-depth: not all xcframeworks have arm64)
            var simSlice = pi.GetSlice(true) with { Architecture = simulatorResolution.SelectedArchitecture };
            var deviceSlice = deviceResolution != null
                ? pi.DeviceSlice with { Architecture = deviceResolution.SelectedArchitecture }
                : pi.DeviceSlice;

            // 1. Collect and post-process Swift files (once — source is architecture-agnostic)
            var swiftFiles = CollectSwiftFiles(outputDirectory);
            var hasAssemblyFiles = !skipThunkCompilation &&
                NativeThunkCompiler.CollectAssemblyFiles(outputDirectory).Count > 0;

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
            var cleanedFiles = new List<string>();

            try
            {
                foreach (var swiftFile in swiftFiles)
                {
                    var content = File.ReadAllText(swiftFile);
                    var result = SwiftWrapperPostProcessor.Process(content, internalTypeNames,
                        warning => logger.LogWarning("{Warning}", warning),
                        moduleNameForCollision: moduleNameForCollision,
                        nestedTypesInCollidingClass: nestedTypesInCollidingClass);
                    totalStripped += result.StrippedBlockCount;
                    allStrippedSymbols.UnionWith(result.StrippedSymbols);

                    if (result.StrippedBlockCount > 0)
                    {
                        logger.LogInformation("  Stripped {Count} broken wrapper(s) from {File}",
                            result.StrippedBlockCount, Path.GetFileName(swiftFile));
                    }

                    if (result.ModuleNameCollisionReplacements > 0)
                    {
                        logger.LogInformation("  Fixed {Count} module/type name collision(s) in {File}",
                            result.ModuleNameCollisionReplacements, Path.GetFileName(swiftFile));
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
                    logger.LogWarning("All Swift wrapper code was stripped as broken ({Count} block(s)).", totalStripped);
                    return new SwiftWrapperCompilationResult
                    {
                        XCFrameworkPath = "",
                        CompiledFileCount = 0,
                        StrippedBlockCount = totalStripped,
                        StrippedSymbols = allStrippedSymbols,
                        SliceCount = 0
                    };
                }

                // 2. Resolve deployment target
                var minOS = ResolveDeploymentTarget(simulatorResolution.DylibPath, logger, commandRunner);

                // 3. Create xcframework directory structure
                var xcframeworkPath = Path.Combine(outputDirectory, $"{wrapperModuleName}.xcframework");
                if (Directory.Exists(xcframeworkPath))
                    Directory.Delete(xcframeworkPath, true);

                var sliceCount = 0;

                // 4. Compile simulator slice
                var simFrameworkDir = Path.Combine(xcframeworkPath, simSlice.SliceId, $"{wrapperModuleName}.framework");
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
                            outputDirectory, simTargetTriple, simSdkPath, logger, commandRunner);
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

                // Pre-compile colliding module for simulator slice (EC-1)
                string? simPrecompiledModulePath = null;
                if (!string.IsNullOrEmpty(moduleNameForCollision) && !string.IsNullOrEmpty(swiftInterfacePath))
                {
                    simPrecompiledModulePath = PrecompileCollidingModule(
                        moduleName, swiftInterfacePath, simTargetTriple, simSdkPath,
                        cleanedDir, commandRunner, logger, moduleNameForCollision,
                        nestedTypesInCollidingClass);
                }

                // XCTest dependency: add platform framework search path + collision resolution.
                // Pre-compile the source framework's interface with XCTest collision patched out.
                var simEffectiveSearchPaths = simAdditionalSearchPaths != null
                    ? new List<string>(simAdditionalSearchPaths) : new List<string>();
                if (DetectXCTestDependency(swiftInterfacePath))
                {
                    var simPlatformPath = ResolvePlatformPath(simSlice.SdkName, commandRunner);
                    var platformFrameworksPath = Path.Combine(simPlatformPath, "Developer", "Library", "Frameworks");
                    simEffectiveSearchPaths.Add(platformFrameworksPath);
                    logger.LogInformation("Detected XCTest dependency — added platform frameworks search path for simulator slice.");

                    if (!string.IsNullOrEmpty(swiftInterfacePath))
                    {
                        var xcTestPrecompiled = PrecompileCollidingModule(
                            moduleName, swiftInterfacePath, simTargetTriple, simSdkPath,
                            cleanedDir, commandRunner, logger, "XCTest",
                            additionalFrameworkSearchPaths: new[] { platformFrameworksPath });
                        simPrecompiledModulePath ??= xcTestPrecompiled;
                    }
                }

                if (cleanedFiles.Count > 0)
                {
                    InvokeSwiftCompiler(
                        cleanedFiles, simBinaryPath, wrapperModuleName,
                        simTargetTriple, simSdkPath,
                        simulatorResolution.FrameworkSearchPath, commandRunner, logger,
                        simEffectiveSearchPaths, simPrecompiledModulePath,
                        simThunkResult?.ObjectFiles, moduleName);
                    sliceCount++;
                }
                else if (simThunkResult != null && simThunkResult.ObjectFiles.Count > 0)
                {
                    logger.LogInformation("No Swift wrappers — linking thunk objects with clang (simulator).");
                    NativeThunkCompiler.LinkWithClang(
                        simThunkResult.ObjectFiles, simBinaryPath, wrapperModuleName,
                        simTargetTriple, simSdkPath, commandRunner, logger,
                        simulatorResolution.FrameworkSearchPath, moduleName);
                    sliceCount++;
                }

                if (sliceCount > 0)
                    logger.LogInformation("Compiled simulator slice for {Module}.", wrapperModuleName);

                // 5. Compile device slice (if available)
                if (deviceResolution != null)
                {
                    var devFrameworkDir = Path.Combine(xcframeworkPath, deviceSlice.SliceId, $"{wrapperModuleName}.framework");
                    Directory.CreateDirectory(devFrameworkDir);
                    WriteFrameworkPlist(devFrameworkDir, wrapperModuleName, minOS, deviceSlice.PlistPlatformName);

                    var devSdkPath = ResolveSdkPath(deviceSlice.SdkName, commandRunner);
                    var devTargetTriple = deviceSlice.GetTargetTriple(minOS);
                    var devBinaryPath = Path.Combine(devFrameworkDir, wrapperModuleName);

                    // Compile thunk assembly for device slice
                    // FATAL if .arm64.s files exist — P/Invokes reference thunk symbols
                    NativeThunkCompilationResult? devThunkResult = null;
                    if (!skipThunkCompilation)
                    {
                        try
                        {
                            devThunkResult = NativeThunkCompiler.CompileThunkObjects(
                                outputDirectory, devTargetTriple, devSdkPath, logger, commandRunner);
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

                    // Pre-compile colliding module for device slice (EC-1)
                    // Must be per-slice as target triple and SDK differ.
                    string? devPrecompiledModulePath = null;
                    if (!string.IsNullOrEmpty(moduleNameForCollision) &&
                        !string.IsNullOrEmpty(deviceResolution.SwiftInterfacePath))
                    {
                        devPrecompiledModulePath = PrecompileCollidingModule(
                            moduleName, deviceResolution.SwiftInterfacePath,
                            devTargetTriple, devSdkPath,
                            cleanedDir, commandRunner, logger, moduleNameForCollision,
                            nestedTypesInCollidingClass);
                    }

                    // XCTest dependency: add platform framework search path + collision resolution (device)
                    var devEffectiveSearchPaths = deviceAdditionalSearchPaths != null
                        ? new List<string>(deviceAdditionalSearchPaths) : new List<string>();
                    if (DetectXCTestDependency(deviceResolution.SwiftInterfacePath))
                    {
                        var devPlatformPath = ResolvePlatformPath(deviceSlice.SdkName, commandRunner);
                        var devPlatformFrameworksPath = Path.Combine(devPlatformPath, "Developer", "Library", "Frameworks");
                        devEffectiveSearchPaths.Add(devPlatformFrameworksPath);
                        logger.LogInformation("Detected XCTest dependency — added platform frameworks search path for device slice.");

                        if (!string.IsNullOrEmpty(deviceResolution.SwiftInterfacePath))
                        {
                            var xcTestPrecompiled = PrecompileCollidingModule(
                                moduleName, deviceResolution.SwiftInterfacePath,
                                devTargetTriple, devSdkPath,
                                cleanedDir, commandRunner, logger, "XCTest",
                                additionalFrameworkSearchPaths: new[] { devPlatformFrameworksPath });
                            devPrecompiledModulePath ??= xcTestPrecompiled;
                        }
                    }

                    if (cleanedFiles.Count > 0)
                    {
                        InvokeSwiftCompiler(
                            cleanedFiles, devBinaryPath, wrapperModuleName,
                            devTargetTriple, devSdkPath,
                            deviceResolution.FrameworkSearchPath, commandRunner, logger,
                            devEffectiveSearchPaths, devPrecompiledModulePath,
                            devThunkResult?.ObjectFiles, moduleName);
                        sliceCount++;
                    }
                    else if (devThunkResult != null && devThunkResult.ObjectFiles.Count > 0)
                    {
                        logger.LogInformation("No Swift wrappers — linking thunk objects with clang (device).");
                        NativeThunkCompiler.LinkWithClang(
                            devThunkResult.ObjectFiles, devBinaryPath, wrapperModuleName,
                            devTargetTriple, devSdkPath, commandRunner, logger,
                            deviceResolution.FrameworkSearchPath, moduleName);
                        sliceCount++;
                    }

                    if (sliceCount > 1)
                        logger.LogInformation("Compiled device slice for {Module}.", wrapperModuleName);
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
                        StrippedSymbols = allStrippedSymbols,
                        SliceCount = 0
                    };
                }

                // 6. Write xcframework Info.plist
                WriteXCFrameworkPlist(xcframeworkPath, wrapperModuleName, deviceResolution != null,
                    slice: simSlice, deviceSlice: deviceSlice);

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
            }
        }

        /// <summary>
        /// Compiles generated Swift wrapper files into a single-slice xcframework.
        /// </summary>
        /// <param name="outputDirectory">Directory containing generated Swift wrapper files.</param>
        /// <param name="moduleName">The Swift module name (e.g., "Nuke").</param>
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
            bool skipThunkCompilation = false)
        {
            commandRunner ??= new SystemCommandRunner();
            var wrapperModuleName = $"{moduleName}SwiftBindings";

            // 1. Collect Swift files and assembly files
            var swiftFiles = CollectSwiftFiles(outputDirectory);
            var hasAssemblyFiles = !skipThunkCompilation &&
                NativeThunkCompiler.CollectAssemblyFiles(outputDirectory).Count > 0;

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
            var cleanedFiles = new List<string>();

            try
            {
                foreach (var swiftFile in swiftFiles)
                {
                    var content = File.ReadAllText(swiftFile);
                    var result = SwiftWrapperPostProcessor.Process(content, internalTypeNames,
                        warning => logger.LogWarning("{Warning}", warning),
                        moduleNameForCollision: moduleNameForCollision,
                        nestedTypesInCollidingClass: nestedTypesInCollidingClass);
                    totalStripped += result.StrippedBlockCount;
                    allStrippedSymbols.UnionWith(result.StrippedSymbols);

                    if (result.StrippedBlockCount > 0)
                    {
                        logger.LogInformation("  Stripped {Count} broken wrapper(s) from {File}",
                            result.StrippedBlockCount, Path.GetFileName(swiftFile));
                    }

                    if (result.ModuleNameCollisionReplacements > 0)
                    {
                        logger.LogInformation("  Fixed {Count} module/type name collision(s) in {File}",
                            result.ModuleNameCollisionReplacements, Path.GetFileName(swiftFile));
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
                    logger.LogWarning("All Swift wrapper code was stripped as broken ({Count} block(s)).", totalStripped);
                    return new SwiftWrapperCompilationResult
                    {
                        XCFrameworkPath = "",
                        CompiledFileCount = 0,
                        StrippedBlockCount = totalStripped,
                        StrippedSymbols = allStrippedSymbols
                    };
                }

                // 3. Resolve deployment target from source framework
                var minOS = ResolveDeploymentTarget(dylibPath, logger, commandRunner);

                // 4. Create xcframework directory structure
                var isSimulator = slice.IsSimulator;
                var sliceId = slice.SliceId;
                var xcframeworkPath = Path.Combine(outputDirectory, $"{wrapperModuleName}.xcframework");
                var frameworkDir = Path.Combine(xcframeworkPath, sliceId, $"{wrapperModuleName}.framework");
                var outputBinaryPath = Path.Combine(frameworkDir, wrapperModuleName);
                CreateXCFrameworkStructure(xcframeworkPath, frameworkDir, wrapperModuleName, minOS, slice);

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
                            outputDirectory, targetTriple, sdkPath, logger, commandRunner);
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

                // 6b. Pre-compile colliding module if needed (EC-1)
                string? precompiledModulePath = null;
                if (!string.IsNullOrEmpty(moduleNameForCollision) && !string.IsNullOrEmpty(swiftInterfacePath))
                {
                    precompiledModulePath = PrecompileCollidingModule(
                        moduleName, swiftInterfacePath, targetTriple, sdkPath,
                        cleanedDir, commandRunner, logger, moduleNameForCollision,
                        nestedTypesInCollidingClass);
                }

                // 6c. XCTest dependency: add platform framework search path + collision resolution.
                // XCTest.framework lives at the platform level. The XCTest module also has a class
                // named "XCTest", causing Swift to misresolve XCTest.XCTestCase as a nested type.
                // Fix: pre-compile the SOURCE framework's interface with XCTest collision patching.
                var effectiveSearchPaths = additionalFrameworkSearchPaths != null
                    ? new List<string>(additionalFrameworkSearchPaths) : new List<string>();
                if (DetectXCTestDependency(swiftInterfacePath))
                {
                    var platformPath = ResolvePlatformPath(slice.SdkName, commandRunner);
                    var platformFrameworksPath = Path.Combine(platformPath, "Developer", "Library", "Frameworks");
                    effectiveSearchPaths.Add(platformFrameworksPath);
                    logger.LogInformation("Detected XCTest dependency — added platform frameworks search path.");

                    // Pre-compile the source framework's interface with XCTest collision patched out.
                    // This creates a shadow framework with a binary .swiftmodule that resolves types correctly.
                    if (!string.IsNullOrEmpty(swiftInterfacePath))
                    {
                        var xcTestPrecompiled = PrecompileCollidingModule(
                            moduleName, swiftInterfacePath, targetTriple, sdkPath,
                            cleanedDir, commandRunner, logger, "XCTest",
                            additionalFrameworkSearchPaths: new[] { platformFrameworksPath });
                        precompiledModulePath ??= xcTestPrecompiled;
                    }
                }

                // 7. Link into wrapper binary
                if (cleanedFiles.Count > 0)
                {
                    // Normal path: swiftc compiles Swift + links thunk .o files
                    InvokeSwiftCompiler(
                        cleanedFiles, outputBinaryPath, wrapperModuleName,
                        targetTriple, sdkPath, frameworkSearchPath, commandRunner, logger,
                        effectiveSearchPaths, precompiledModulePath,
                        thunkResult?.ObjectFiles, moduleName);
                }
                else if (thunkResult != null && thunkResult.ObjectFiles.Count > 0)
                {
                    // Edge case: no Swift wrappers (all functions thunked).
                    // swiftc requires at least one .swift input, so use clang -shared.
                    logger.LogInformation("No Swift wrappers — linking thunk objects with clang.");
                    NativeThunkCompiler.LinkWithClang(
                        thunkResult.ObjectFiles, outputBinaryPath, wrapperModuleName,
                        targetTriple, sdkPath, commandRunner, logger,
                        frameworkSearchPath, moduleName);
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
                        StrippedSymbols = allStrippedSymbols
                    };
                }

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
            }
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
            bool skipThunkCompilation = false)
        {
            var isSimulator = platformVariant == "simulator";
            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            var slice = pi.GetSlice(isSimulator);
            return CompileSlice(outputDirectory, moduleName, frameworkSearchPath, dylibPath,
                slice, logger, commandRunner, internalTypeNames, additionalFrameworkSearchPaths,
                moduleNameForCollision: moduleNameForCollision,
                nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                swiftInterfacePath: swiftInterfacePath,
                skipThunkCompilation: skipThunkCompilation);
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
            PlatformInfo? platformInfo = null)
        {
            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            var simSlice = pi.GetSlice(true);
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
            var simSlice = pi.GetSlice(true);
            var deviceSlice = pi.DeviceSlice;

            var bridgeFiles = CollectBridgeSwiftFiles(outputDirectory);
            if (bridgeFiles.Count == 0)
            {
                logger.LogInformation("No SwiftUI bridge files found in {Dir} — skipping bridge compilation.", outputDirectory);
                return null;
            }

            logger.LogInformation("Compiling bridge into {Module}.xcframework ({Count} file(s))...",
                bridgeModuleName, bridgeFiles.Count);

            // Resolve deployment target
            var minOS = ResolveDeploymentTarget(simulatorResolution.DylibPath, logger, commandRunner);

            // Create xcframework directory structure
            var xcframeworkPath = Path.Combine(outputDirectory, $"{bridgeModuleName}.xcframework");
            if (Directory.Exists(xcframeworkPath))
                Directory.Delete(xcframeworkPath, true);

            var sliceCount = 0;

            // Compile simulator slice
            var simFrameworkDir = Path.Combine(xcframeworkPath, simSlice.SliceId, $"{bridgeModuleName}.framework");
            Directory.CreateDirectory(simFrameworkDir);
            WriteFrameworkPlist(simFrameworkDir, bridgeModuleName, minOS, simSlice.PlistPlatformName);

            var simSdkPath = ResolveSdkPath(simSlice.SdkName, commandRunner);
            var simTargetTriple = simSlice.GetTargetTriple(minOS);
            var simBinaryPath = Path.Combine(simFrameworkDir, bridgeModuleName);

            InvokeSwiftCompiler(
                bridgeFiles, simBinaryPath, bridgeModuleName,
                simTargetTriple, simSdkPath,
                simulatorResolution.FrameworkSearchPath, commandRunner, logger,
                simAdditionalSearchPaths, originalModuleName: moduleName);
            sliceCount++;

            logger.LogInformation("Compiled simulator slice for {Module}.", bridgeModuleName);

            // Compile device slice (if available)
            if (deviceResolution != null)
            {
                var devFrameworkDir = Path.Combine(xcframeworkPath, deviceSlice.SliceId, $"{bridgeModuleName}.framework");
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
                sliceCount++;

                logger.LogInformation("Compiled device slice for {Module}.", bridgeModuleName);
            }

            // Write xcframework Info.plist
            WriteXCFrameworkPlist(xcframeworkPath, bridgeModuleName, deviceResolution != null,
                slice: simSlice, deviceSlice: deviceSlice);

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

            // Resolve deployment target
            var minOS = ResolveDeploymentTarget(dylibPath, logger, commandRunner);

            // Create xcframework directory structure
            var xcframeworkPath = Path.Combine(outputDirectory, $"{bridgeModuleName}.xcframework");
            var frameworkDir = Path.Combine(xcframeworkPath, slice.SliceId, $"{bridgeModuleName}.framework");
            CreateXCFrameworkStructure(xcframeworkPath, frameworkDir, bridgeModuleName, minOS, slice);

            // Resolve SDK path and build target triple
            var sdkPath = ResolveSdkPath(slice.SdkName, commandRunner);
            var targetTriple = slice.GetTargetTriple(minOS);
            var outputBinaryPath = Path.Combine(frameworkDir, bridgeModuleName);

            // Compile bridge — no post-processing, no thunks, no pre-compiled module
            InvokeSwiftCompiler(
                bridgeFiles, outputBinaryPath, bridgeModuleName,
                targetTriple, sdkPath, frameworkSearchPath, commandRunner, logger,
                additionalFrameworkSearchPaths, originalModuleName: moduleName);

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
        /// Reads MinimumOSVersion from the source framework's Info.plist.
        /// Falls back to "15.0" if not found.
        /// </summary>
        internal static string ResolveDeploymentTarget(
            string dylibPath, ILogger logger, ICommandRunner? commandRunner = null)
        {
            const string fallback = "16.0";

            var frameworkDir = Path.GetDirectoryName(dylibPath);
            if (string.IsNullOrEmpty(frameworkDir))
                return fallback;

            var infoPlistPath = Path.Combine(frameworkDir, "Info.plist");
            var data = PlistReader.ReadPlistDict(infoPlistPath, commandRunner, logger);
            if (data != null && data.TryGetValue("MinimumOSVersion", out var minOS) && minOS is string minOSStr)
            {
                // Ensure minimum 16.0 for parameterized protocol syntax (any AsyncSequence<T, E>)
                // which the generator may emit in wrapper code. Parameterized existentials
                // require Swift 5.7 runtime support (iOS 16.0+).
                var resolved = EnforceMinimumDeploymentTarget(minOSStr, "16.0");
                if (resolved != minOSStr)
                    logger.LogInformation("Raised deployment target from {Source} to {Resolved} (parameterized protocols require 16.0+).", minOSStr, resolved);
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
            var simVariant = slice?.XCFrameworkPlatformVariant ?? "simulator";

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
                                    <string>arm64</string>
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
                                <string>arm64</string>
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
            string? precompiledModulePath = null,
            IReadOnlyList<string>? thunkObjectFiles = null,
            string? originalModuleName = null)
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

            var additionalFFlags = "";
            if (additionalFrameworkSearchPaths != null)
            {
                foreach (var path in additionalFrameworkSearchPaths)
                {
                    additionalFFlags += $" -F \"{path}\"";
                }
            }

            // Pre-compiled module path for collision resolution: shadow framework directory
            // with binary .swiftmodule. Added as a higher-priority -F path BEFORE the real
            // framework search path, so swiftc finds the binary module first.
            var precompiledFFlag = "";
            if (!string.IsNullOrEmpty(precompiledModulePath))
            {
                precompiledFFlag = $"-F \"{precompiledModulePath}\" ";
            }

            var args = $"swiftc -emit-library -target {targetTriple} " +
                       $"-sdk \"{sdkPath}\" " +
                       $"-strict-concurrency=minimal " +   // Temporary: see roadmap for actor-aware emission
                       $"{precompiledFFlag}-F \"{frameworkSearchPath}\"{additionalFFlags} " +
                       $"-module-name {wrapperModuleName} " +
                       $"{thunkLinkerFlags}" +
                       $"-Xlinker -install_name -Xlinker @rpath/{wrapperModuleName}.framework/{wrapperModuleName} " +
                       $"-o \"{outputBinaryPath}\" " +
                       fileArgs;

            logger.LogDebug("Invoking: xcrun {Args}", args);

            var (exitCode, stdout, stderr) = commandRunner.Run("xcrun", args, timeoutMs: 120000);

            if (exitCode != 0)
            {
                logger.LogDebug("Full swiftc stderr:\n{Stderr}", stderr);
                var errorPreview = stderr.Length > 2000 ? stderr.Substring(0, 2000) + "..." : stderr;

                var missingModules = ExtractMissingModules(stderr);
                var hint = "";
                if (missingModules.Count > 0)
                {
                    var moduleList = string.Join(", ", missingModules.Select(m => $"'{m}'"));
                    hint = $"\n\nMissing module(s): {moduleList}. " +
                           "Provide the xcframework(s) for these modules:\n" +
                           $"  CLI:  --framework-dependency /path/to/<Module>.xcframework (repeat for each)\n" +
                           $"  SDK:  <SwiftFrameworkDependency Include=\"path/to/<Module>.xcframework\" " +
                           "PackageId=\"<Module>.Swift.iOS\" PackageVersion=\"1.0.0\" />";
                }

                throw new InvalidOperationException(
                    $"Swift wrapper compilation failed (exit code {exitCode}): {errorPreview}{hint}");
            }
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
        /// Pre-compiles a patched .swiftinterface into a binary .swiftmodule to resolve
        /// module/type name collisions (EC-1). When a module has a public type with the
        /// same name as the module, the textual .swiftinterface fails to import because
        /// Swift misresolves Module.Type as Class.NestedType. The fix:
        /// 1. Copy .swiftinterface to temp dir
        /// 2. Patch collision: strip module prefix from type references
        /// 3. Compile to binary .swiftmodule via swift-frontend
        /// 4. Return the directory path for -I flag (binary module takes precedence)
        /// </summary>
        /// <returns>Directory path containing the pre-compiled .swiftmodule, or null on failure.</returns>
        /// <summary>
        /// Pre-compiles a patched .swiftinterface into a binary .swiftmodule to resolve
        /// module/type name collisions (EC-1). Creates a temp framework directory structure
        /// that shadows the real framework when added as a higher-priority -F path.
        /// </summary>
        /// <returns>Framework search path to prepend as -F (higher priority), or null on failure.</returns>
        internal static string? PrecompileCollidingModule(
            string moduleName,
            string swiftInterfacePath,
            string targetTriple,
            string sdkPath,
            string buildDir,
            ICommandRunner commandRunner,
            ILogger logger,
            string moduleNameForCollision,
            HashSet<string>? nestedTypesInCollidingClass = null,
            IReadOnlyList<string>? additionalFrameworkSearchPaths = null)
        {
            try
            {
                // Create a subdirectory for pre-compiled modules (target-specific)
                var safeTriple = targetTriple.Replace("/", "_");
                var precompileDir = Path.Combine(buildDir, $"precompiled-{safeTriple}");
                Directory.CreateDirectory(precompileDir);

                // 1. Prepare the collision regex
                var collisionPattern = new System.Text.RegularExpressions.Regex(
                    @"\b" + System.Text.RegularExpressions.Regex.Escape(moduleNameForCollision) +
                    @"\.(\w+(?:\.\w+)*)",
                    System.Text.RegularExpressions.RegexOptions.Compiled);

                // 2. Create a shadow framework directory structure that overrides the real one
                // via -F precedence. Structure: {precompileDir}/{Module}.framework/Modules/{Module}.swiftmodule/
                var fwDir = Path.Combine(precompileDir, $"{moduleName}.framework", "Modules",
                    $"{moduleName}.swiftmodule");
                Directory.CreateDirectory(fwDir);

                // Copy and patch the .swiftinterface
                var patchedInterfacePath = Path.Combine(precompileDir, Path.GetFileName(swiftInterfacePath));
                PatchSwiftInterface(swiftInterfacePath, patchedInterfacePath, collisionPattern, nestedTypesInCollidingClass);

                // Copy and patch swiftinterface files from the source .swiftmodule dir
                // into the shadow framework. Exclude .private.swiftinterface files:
                // the binary .swiftmodule is self-contained for public API resolution,
                // and private interfaces can contain types from colliding modules that
                // fail to resolve even after patching (e.g., XCTest class/module collision).
                var sourceModuleDir = Path.GetDirectoryName(swiftInterfacePath);
                if (sourceModuleDir != null)
                {
                    foreach (var ifaceFile in Directory.GetFiles(sourceModuleDir, "*.swiftinterface"))
                    {
                        if (ifaceFile.EndsWith(".private.swiftinterface", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var destPath = Path.Combine(fwDir, Path.GetFileName(ifaceFile));
                        PatchSwiftInterface(ifaceFile, destPath, collisionPattern, nestedTypesInCollidingClass);
                    }
                }

                // Create a minimal Swift-only modulemap for the shadow framework.
                // The real framework's modulemap may reference umbrella headers that don't exist
                // in the shadow. For Swift-only imports, a bare module declaration suffices.
                var shadowModulesDir = Path.Combine(precompileDir, $"{moduleName}.framework", "Modules");
                var minimalModulemap = $"framework module {moduleName} {{\n}}\n";
                File.WriteAllText(Path.Combine(shadowModulesDir, "module.modulemap"), minimalModulemap);

                // Derive binary module name from the .swiftinterface filename pattern.
                // Framework .swiftmodule dirs use versionless arch names (e.g., "arm64-apple-ios-simulator")
                // while GetTargetTriple() includes the OS version (e.g., "arm64-apple-ios17.0-simulator").
                // swiftc only finds the binary module if the filename matches the versionless pattern.
                var interfaceBaseName = Path.GetFileNameWithoutExtension(
                    Path.GetFileName(swiftInterfacePath));
                var outputModulePath = Path.Combine(fwDir, $"{interfaceBaseName}.swiftmodule");

                // 3. Compile to binary .swiftmodule
                // Derive the framework search path from the swiftinterface path.
                // swiftInterfacePath is like: .../ios-arm64_x86_64-simulator/Module.framework/Modules/Module.swiftmodule/arch.swiftinterface
                // Framework search path is: .../ios-arm64_x86_64-simulator/
                var swiftModuleParent = Path.GetDirectoryName(swiftInterfacePath); // .swiftmodule dir
                var modulesParent = Path.GetDirectoryName(swiftModuleParent);      // Modules dir
                var frameworkParent = Path.GetDirectoryName(modulesParent);         // Module.framework dir
                var sliceSearchPath = Path.GetDirectoryName(frameworkParent);       // slice dir (for -F)

                var fwSearchFlag = "";
                if (!string.IsNullOrEmpty(sliceSearchPath) && Directory.Exists(sliceSearchPath))
                {
                    fwSearchFlag = $"-F \"{sliceSearchPath}\" ";
                }

                // Append additional -F paths (e.g., platform frameworks for XCTest dependency)
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
                        "Pre-compilation of colliding module '{Module}' failed (non-fatal): {Error}",
                        moduleName, stderr.Length > 500 ? stderr.Substring(0, 500) : stderr);
                    return null;
                }

                // Remove textual .swiftinterface files from the shadow framework.
                // The binary .swiftmodule is self-contained — leaving the textual interfaces
                // causes swiftc to fall back to re-parsing them (which hits the collision).
                foreach (var ifaceFile in Directory.GetFiles(fwDir, "*.swiftinterface"))
                {
                    File.Delete(ifaceFile);
                }

                logger.LogInformation("Pre-compiled patched .swiftinterface for collision resolution ({Module}).", moduleName);
                return precompileDir;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Pre-compilation failed (non-fatal): {Message}", ex.Message);
                return null;
            }
        }
        /// <summary>
        /// Patches a .swiftinterface file by stripping the module prefix from type references
        /// to resolve module/type name collisions. Preserves references to types nested inside
        /// the colliding class (EC-18).
        /// </summary>
        private static void PatchSwiftInterface(string sourcePath, string destPath,
            System.Text.RegularExpressions.Regex collisionPattern,
            HashSet<string>? nestedTypesInCollidingClass)
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
                    var firstComponent = match.Groups[1].Value;
                    var dotIdx = firstComponent.IndexOf('.');
                    var topLevelName = dotIdx >= 0 ? firstComponent.Substring(0, dotIdx) : firstComponent;

                    if (nestedTypesInCollidingClass != null &&
                        nestedTypesInCollidingClass.Contains(topLevelName))
                        return match.Value;

                    return match.Groups[1].Value;
                })).Append('\n');
            }
            File.WriteAllText(destPath, patched.ToString());
        }
    }
}
