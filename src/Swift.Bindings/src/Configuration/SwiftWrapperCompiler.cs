// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Xml;
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
    /// Simulator-only (arm64). Device slices are deferred to Step 5.
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
        /// Compiles generated Swift wrapper files into an xcframework.
        /// Returns null if no Swift files exist in the output directory.
        /// </summary>
        /// <param name="outputDirectory">Directory containing generated Swift wrapper files.</param>
        /// <param name="moduleName">The Swift module name (e.g., "Nuke").</param>
        /// <param name="frameworkSearchPath">The -F flag target (e.g., xcframework slice directory).</param>
        /// <param name="dylibPath">Path to the source framework's dylib (used to locate Info.plist for min OS).</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        public static SwiftWrapperCompilationResult? Compile(
            string outputDirectory,
            string moduleName,
            string frameworkSearchPath,
            string dylibPath,
            ILogger logger,
            ICommandRunner? commandRunner = null)
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
                    var result = SwiftWrapperPostProcessor.Process(content);
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
                var minOS = ResolveDeploymentTarget(dylibPath, logger);

                // 4. Create xcframework directory structure
                var xcframeworkPath = Path.Combine(outputDirectory, $"{wrapperModuleName}.xcframework");
                var frameworkDir = Path.Combine(xcframeworkPath, "ios-arm64-simulator", $"{wrapperModuleName}.framework");
                var outputBinaryPath = Path.Combine(frameworkDir, wrapperModuleName);
                CreateXCFrameworkStructure(xcframeworkPath, frameworkDir, wrapperModuleName, minOS);

                // 5. Resolve SDK path
                var sdkPath = ResolveSdkPath(commandRunner);

                // 6. Invoke swiftc
                InvokeSwiftCompiler(
                    cleanedFiles, outputBinaryPath, wrapperModuleName,
                    minOS, sdkPath, frameworkSearchPath, commandRunner, logger);

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
        internal static string ResolveDeploymentTarget(string dylibPath, ILogger logger)
        {
            const string fallback = "15.0";

            try
            {
                // dylibPath is inside the .framework directory (e.g., .../Nuke.framework/Nuke)
                var frameworkDir = Path.GetDirectoryName(dylibPath);
                if (string.IsNullOrEmpty(frameworkDir))
                    return fallback;

                var infoPlistPath = Path.Combine(frameworkDir, "Info.plist");
                if (!File.Exists(infoPlistPath))
                {
                    logger.LogDebug("No Info.plist found at {Path}, using default min OS {Version}", infoPlistPath, fallback);
                    return fallback;
                }

                var doc = new XmlDocument();
                doc.Load(infoPlistPath);

                var rootDict = doc.SelectSingleNode("/plist/dict");
                if (rootDict == null)
                    return fallback;

                var data = XCFrameworkResolver.ParsePlistDict(rootDict);
                if (data.TryGetValue("MinimumOSVersion", out var minOS) && minOS is string minOSStr)
                {
                    logger.LogInformation("Resolved deployment target {Version} from source framework.", minOSStr);
                    return minOSStr;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to read MinimumOSVersion from framework Info.plist, using default {Version}", fallback);
            }

            return fallback;
        }

        /// <summary>
        /// Resolves the iOS Simulator SDK path via xcrun.
        /// </summary>
        internal static string ResolveSdkPath(ICommandRunner commandRunner)
        {
            var (exitCode, sdkPath, stderr) = commandRunner.Run("xcrun", "--sdk iphonesimulator --show-sdk-path");
            if (exitCode != 0 || string.IsNullOrWhiteSpace(sdkPath))
            {
                throw new InvalidOperationException(
                    $"Failed to resolve iOS Simulator SDK path. Ensure Xcode and iOS SDK are installed. " +
                    $"Error: {stderr}");
            }
            return sdkPath;
        }

        /// <summary>
        /// Creates the xcframework directory structure with both Info.plists.
        /// </summary>
        internal static void CreateXCFrameworkStructure(
            string xcframeworkPath, string frameworkDir, string wrapperModuleName, string minOS)
        {
            // Remove previous build
            if (Directory.Exists(xcframeworkPath))
                Directory.Delete(xcframeworkPath, true);

            Directory.CreateDirectory(frameworkDir);

            // Framework Info.plist
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
                        <string>iPhoneSimulator</string>
                    </array>
                </dict>
                </plist>
                """;
            File.WriteAllText(Path.Combine(frameworkDir, "Info.plist"), frameworkPlist);

            // XCFramework Info.plist
            var xcframeworkPlist = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>AvailableLibraries</key>
                    <array>
                        <dict>
                            <key>LibraryIdentifier</key>
                            <string>ios-arm64-simulator</string>
                            <key>LibraryPath</key>
                            <string>{wrapperModuleName}.framework</string>
                            <key>SupportedArchitectures</key>
                            <array>
                                <string>arm64</string>
                            </array>
                            <key>SupportedPlatform</key>
                            <string>ios</string>
                            <key>SupportedPlatformVariant</key>
                            <string>simulator</string>
                        </dict>
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
        internal static void InvokeSwiftCompiler(
            List<string> swiftFiles,
            string outputBinaryPath,
            string wrapperModuleName,
            string minOS,
            string sdkPath,
            string frameworkSearchPath,
            ICommandRunner commandRunner,
            ILogger logger)
        {
            var fileArgs = string.Join(" ", swiftFiles.Select(f => $"\"{f}\""));

            var args = $"swiftc -emit-library -target arm64-apple-ios{minOS}-simulator " +
                       $"-sdk \"{sdkPath}\" " +
                       $"-F \"{frameworkSearchPath}\" " +
                       $"-module-name {wrapperModuleName} " +
                       $"-Xlinker -install_name -Xlinker @rpath/{wrapperModuleName}.framework/{wrapperModuleName} " +
                       $"-o \"{outputBinaryPath}\" " +
                       fileArgs;

            logger.LogDebug("Invoking: xcrun {Args}", args);

            var (exitCode, stdout, stderr) = commandRunner.Run("xcrun", args, timeoutMs: 120000);

            if (exitCode != 0)
            {
                var errorPreview = stderr.Length > 500 ? stderr.Substring(0, 500) + "..." : stderr;
                throw new InvalidOperationException(
                    $"Swift wrapper compilation failed (exit code {exitCode}): {errorPreview}");
            }
        }
    }
}
