// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
            PlatformInfo? platformInfo = null)
        {
            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            var simSlice = pi.GetSlice(true);
            return CompileSlice(outputDirectory, moduleName, frameworkSearchPath, dylibPath,
                simSlice, logger, commandRunner, internalTypeNames,
                additionalFrameworkSearchPaths);
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
            PlatformInfo? platformInfo = null)
        {
            commandRunner ??= new SystemCommandRunner();
            var wrapperModuleName = $"{moduleName}SwiftBindings";

            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            var simSlice = pi.GetSlice(true);
            var deviceSlice = pi.DeviceSlice;

            // 1. Collect and post-process Swift files (once — source is architecture-agnostic)
            var swiftFiles = CollectSwiftFiles(outputDirectory);
            if (swiftFiles.Count == 0)
            {
                logger.LogInformation("No Swift wrapper files found in {Dir} — skipping wrapper compilation.", outputDirectory);
                return null;
            }

            logger.LogInformation("Compiling {Count} Swift wrapper file(s) into {Module}.xcframework...",
                swiftFiles.Count, wrapperModuleName);

            var cleanedDir = Path.Combine(outputDirectory, ".wrapper-build");
            if (Directory.Exists(cleanedDir))
                Directory.Delete(cleanedDir, true);
            Directory.CreateDirectory(cleanedDir);

            int totalStripped = 0;
            var cleanedFiles = new List<string>();

            try
            {
                foreach (var swiftFile in swiftFiles)
                {
                    var content = File.ReadAllText(swiftFile);
                    var result = SwiftWrapperPostProcessor.Process(content, internalTypeNames);
                    totalStripped += result.StrippedBlockCount;

                    if (result.StrippedBlockCount > 0)
                    {
                        logger.LogInformation("  Stripped {Count} broken wrapper(s) from {File}",
                            result.StrippedBlockCount, Path.GetFileName(swiftFile));
                    }

                    if (!string.IsNullOrWhiteSpace(result.CleanedContent))
                    {
                        var cleanedPath = Path.Combine(cleanedDir, Path.GetFileName(swiftFile));
                        File.WriteAllText(cleanedPath, result.CleanedContent);
                        cleanedFiles.Add(cleanedPath);
                    }
                }

                if (cleanedFiles.Count == 0)
                {
                    logger.LogWarning("All Swift wrapper code was stripped as broken ({Count} block(s)).", totalStripped);
                    return new SwiftWrapperCompilationResult
                    {
                        XCFrameworkPath = "",
                        CompiledFileCount = 0,
                        StrippedBlockCount = totalStripped,
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
                var simBinaryPath = Path.Combine(simFrameworkDir, wrapperModuleName);
                InvokeSwiftCompiler(
                    cleanedFiles, simBinaryPath, wrapperModuleName,
                    simSlice.GetTargetTriple(minOS), simSdkPath,
                    simulatorResolution.FrameworkSearchPath, commandRunner, logger,
                    simAdditionalSearchPaths);
                sliceCount++;

                logger.LogInformation("Compiled simulator slice for {Module}.", wrapperModuleName);

                // 5. Compile device slice (if available)
                if (deviceResolution != null)
                {
                    var devFrameworkDir = Path.Combine(xcframeworkPath, deviceSlice.SliceId, $"{wrapperModuleName}.framework");
                    Directory.CreateDirectory(devFrameworkDir);
                    WriteFrameworkPlist(devFrameworkDir, wrapperModuleName, minOS, deviceSlice.PlistPlatformName);

                    var devSdkPath = ResolveSdkPath(deviceSlice.SdkName, commandRunner);
                    var devBinaryPath = Path.Combine(devFrameworkDir, wrapperModuleName);
                    InvokeSwiftCompiler(
                        cleanedFiles, devBinaryPath, wrapperModuleName,
                        deviceSlice.GetTargetTriple(minOS), devSdkPath,
                        deviceResolution.FrameworkSearchPath, commandRunner, logger,
                        deviceAdditionalSearchPaths);
                    sliceCount++;

                    logger.LogInformation("Compiled device slice for {Module}.", wrapperModuleName);
                }

                // 6. Write xcframework Info.plist
                WriteXCFrameworkPlist(xcframeworkPath, wrapperModuleName, deviceResolution != null,
                    slice: simSlice, deviceSlice: deviceSlice);

                logger.LogInformation("{Module}.xcframework built successfully at {Path} ({SliceCount} slice(s)).",
                    wrapperModuleName, xcframeworkPath, sliceCount);

                return new SwiftWrapperCompilationResult
                {
                    XCFrameworkPath = xcframeworkPath,
                    CompiledFileCount = cleanedFiles.Count,
                    StrippedBlockCount = totalStripped,
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
            IReadOnlyList<string>? additionalFrameworkSearchPaths = null)
        {
            commandRunner ??= new SystemCommandRunner();
            var wrapperModuleName = $"{moduleName}SwiftBindings";

            // 1. Collect Swift files (exclude SwiftUI bridge)
            var swiftFiles = CollectSwiftFiles(outputDirectory);
            if (swiftFiles.Count == 0)
            {
                logger.LogInformation("No Swift wrapper files found in {Dir} — skipping wrapper compilation.", outputDirectory);
                return null;
            }

            logger.LogInformation("Compiling {Count} Swift wrapper file(s) into {Module}.xcframework...",
                swiftFiles.Count, wrapperModuleName);

            // 2. Post-process each file into temp dir
            var cleanedDir = Path.Combine(outputDirectory, ".wrapper-build");
            if (Directory.Exists(cleanedDir))
                Directory.Delete(cleanedDir, true);
            Directory.CreateDirectory(cleanedDir);

            int totalStripped = 0;
            var cleanedFiles = new List<string>();

            try
            {
                foreach (var swiftFile in swiftFiles)
                {
                    var content = File.ReadAllText(swiftFile);
                    var result = SwiftWrapperPostProcessor.Process(content, internalTypeNames);
                    totalStripped += result.StrippedBlockCount;

                    if (result.StrippedBlockCount > 0)
                    {
                        logger.LogInformation("  Stripped {Count} broken wrapper(s) from {File}",
                            result.StrippedBlockCount, Path.GetFileName(swiftFile));
                    }

                    // Only write files that have content left after processing
                    if (!string.IsNullOrWhiteSpace(result.CleanedContent))
                    {
                        var cleanedPath = Path.Combine(cleanedDir, Path.GetFileName(swiftFile));
                        File.WriteAllText(cleanedPath, result.CleanedContent);
                        cleanedFiles.Add(cleanedPath);
                    }
                }

                if (cleanedFiles.Count == 0)
                {
                    logger.LogWarning("All Swift wrapper code was stripped as broken ({Count} block(s)).", totalStripped);
                    return new SwiftWrapperCompilationResult
                    {
                        XCFrameworkPath = "",
                        CompiledFileCount = 0,
                        StrippedBlockCount = totalStripped
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

                // 7. Invoke swiftc
                InvokeSwiftCompiler(
                    cleanedFiles, outputBinaryPath, wrapperModuleName,
                    targetTriple, sdkPath, frameworkSearchPath, commandRunner, logger,
                    additionalFrameworkSearchPaths);

                logger.LogInformation("{Module}.xcframework built successfully at {Path}",
                    wrapperModuleName, xcframeworkPath);

                return new SwiftWrapperCompilationResult
                {
                    XCFrameworkPath = xcframeworkPath,
                    CompiledFileCount = cleanedFiles.Count,
                    StrippedBlockCount = totalStripped
                };
            }
            finally
            {
                // 7. Cleanup temp dir
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
            PlatformInfo? platformInfo = null)
        {
            var isSimulator = platformVariant == "simulator";
            var pi = platformInfo ?? PlatformInfoFactory.Create(ApplePlatform.iOS);
            var slice = pi.GetSlice(isSimulator);
            return CompileSlice(outputDirectory, moduleName, frameworkSearchPath, dylibPath,
                slice, logger, commandRunner, internalTypeNames, additionalFrameworkSearchPaths);
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
        /// Reads MinimumOSVersion from the source framework's Info.plist.
        /// Falls back to "15.0" if not found.
        /// </summary>
        internal static string ResolveDeploymentTarget(
            string dylibPath, ILogger logger, ICommandRunner? commandRunner = null)
        {
            const string fallback = "15.0";

            var frameworkDir = Path.GetDirectoryName(dylibPath);
            if (string.IsNullOrEmpty(frameworkDir))
                return fallback;

            var infoPlistPath = Path.Combine(frameworkDir, "Info.plist");
            var data = PlistReader.ReadPlistDict(infoPlistPath, commandRunner, logger);
            if (data != null && data.TryGetValue("MinimumOSVersion", out var minOS) && minOS is string minOSStr)
            {
                logger.LogInformation("Resolved deployment target {Version} from source framework.", minOSStr);
                return minOSStr;
            }

            logger.LogDebug("Could not read MinimumOSVersion from Info.plist, using default {Version}", fallback);
            return fallback;
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
            IReadOnlyList<string>? additionalFrameworkSearchPaths = null)
        {
            var fileArgs = string.Join(" ", swiftFiles.Select(f => $"\"{f}\""));

            var additionalFFlags = "";
            if (additionalFrameworkSearchPaths != null)
            {
                foreach (var path in additionalFrameworkSearchPaths)
                {
                    additionalFFlags += $" -F \"{path}\"";
                }
            }

            var args = $"swiftc -emit-library -target {targetTriple} " +
                       $"-sdk \"{sdkPath}\" " +
                       $"-strict-concurrency=minimal " +   // Temporary: see roadmap for actor-aware emission
                       $"-F \"{frameworkSearchPath}\"{additionalFFlags} " +
                       $"-module-name {wrapperModuleName} " +
                       $"-Xlinker -install_name -Xlinker @rpath/{wrapperModuleName}.framework/{wrapperModuleName} " +
                       $"-o \"{outputBinaryPath}\" " +
                       fileArgs;

            logger.LogDebug("Invoking: xcrun {Args}", args);

            var (exitCode, stdout, stderr) = commandRunner.Run("xcrun", args, timeoutMs: 120000);

            if (exitCode != 0)
            {
                logger.LogDebug("Full swiftc stderr:\n{Stderr}", stderr);
                var errorPreview = stderr.Length > 2000 ? stderr.Substring(0, 2000) + "..." : stderr;
                throw new InvalidOperationException(
                    $"Swift wrapper compilation failed (exit code {exitCode}): {errorPreview}");
            }
        }
    }
}
