// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// A framework dependency detected from binary linkage analysis (otool -L).
    /// </summary>
    public sealed record DetectedDependency
    {
        /// <summary>
        /// The framework name extracted from the install name (e.g., "Nuke").
        /// </summary>
        public required string FrameworkName { get; init; }

        /// <summary>
        /// The full install name from otool output (e.g., "@rpath/Nuke.framework/Nuke").
        /// </summary>
        public required string InstallName { get; init; }

        /// <summary>
        /// Source of this dependency: "binary-linkage" for auto-detected, "manual" for --framework-dependency.
        /// </summary>
        public string Source { get; init; } = "binary-linkage";

        /// <summary>
        /// Why this dependency is unresolved (only set for entries in UnresolvedDependencies).
        /// "no-xcframework" = no sibling xcframework found.
        /// "missing-slice" = xcframework found but lacks required platform slice.
        /// Null for resolved dependencies or newly detected entries.
        /// </summary>
        public string? UnresolvedReason { get; init; }
    }

    /// <summary>
    /// Result of binary dependency analysis.
    /// </summary>
    public sealed class DependencyAnalysisResult
    {
        /// <summary>
        /// Dependencies that were detected and successfully resolved to xcframeworks.
        /// </summary>
        public required List<FrameworkDependencyInfo> ResolvedDependencies { get; init; }

        /// <summary>
        /// Dependencies that were detected but no matching xcframework was found.
        /// </summary>
        public required List<DetectedDependency> UnresolvedDependencies { get; init; }

        /// <summary>
        /// All detected dependencies (before resolution), deduplicated by framework name.
        /// </summary>
        public required List<DetectedDependency> AllDetected { get; init; }
    }

    /// <summary>
    /// Analyzes Mach-O binary linkage to detect framework dependencies automatically.
    /// Uses otool -L to inspect LC_LOAD_DYLIB / LC_LOAD_WEAK_DYLIB load commands.
    /// </summary>
    public static class BinaryDependencyAnalyzer
    {
        private static readonly Regex FrameworkNameRegex = new(
            @"@rpath/([^/]+)\.framework/", RegexOptions.Compiled);

        /// <summary>
        /// Extracts the framework name from an install name path.
        /// </summary>
        /// <param name="installName">Install name from otool output (e.g., "@rpath/Nuke.framework/Nuke").</param>
        /// <returns>The framework name (e.g., "Nuke"), or null if not a framework path.</returns>
        public static string? ExtractFrameworkName(string installName)
        {
            if (string.IsNullOrEmpty(installName))
                return null;

            var match = FrameworkNameRegex.Match(installName);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Parses otool -L output to extract framework dependencies.
        /// Filters system dylibs, Swift runtime, and self-references.
        /// Deduplicates by framework name (same framework can appear as both normal and weak linkage).
        /// </summary>
        /// <param name="output">Raw stdout from otool -L.</param>
        /// <param name="primaryModuleName">The primary module name to filter self-references.</param>
        /// <returns>Deduplicated list of detected dependencies.</returns>
        public static List<DetectedDependency> ParseOtoolOutput(string output, string primaryModuleName)
        {
            if (string.IsNullOrWhiteSpace(output))
                return new List<DetectedDependency>();

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<DetectedDependency>();

            // Skip first line (header: path of the binary)
            foreach (var rawLine in lines.Skip(1))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                // Extract install name (everything before " (compatibility")
                var compatIdx = line.IndexOf(" (compatibility", StringComparison.Ordinal);
                var installName = compatIdx >= 0 ? line[..compatIdx].Trim() : line;

                // Only keep @rpath entries (companion framework dependencies)
                if (!installName.StartsWith("@rpath/", StringComparison.Ordinal))
                    continue;

                // Filter Swift runtime libs
                if (installName.Contains("/usr/lib/swift/", StringComparison.Ordinal))
                    continue;

                // Extract framework name
                var frameworkName = ExtractFrameworkName(installName);
                if (frameworkName == null)
                    continue;

                // Filter self-references
                if (string.Equals(frameworkName, primaryModuleName, StringComparison.Ordinal))
                    continue;

                // Deduplicate by framework name
                if (!seen.Add(frameworkName))
                    continue;

                result.Add(new DetectedDependency
                {
                    FrameworkName = frameworkName,
                    InstallName = installName
                });
            }

            return result;
        }

        /// <summary>
        /// Searches for a sibling xcframework matching the given framework name.
        /// Looks in the same directory and parent directory of the primary xcframework.
        /// </summary>
        /// <param name="primaryXCFrameworkPath">Path to the primary xcframework.</param>
        /// <param name="frameworkName">Framework name to search for.</param>
        /// <returns>Path to the found xcframework, or null if not found.</returns>
        public static string? FindSiblingXCFramework(string primaryXCFrameworkPath, string frameworkName)
        {
            var parentDir = Path.GetDirectoryName(primaryXCFrameworkPath);
            if (parentDir == null)
                return null;

            // Same directory
            var sameDirPath = Path.Combine(parentDir, $"{frameworkName}.xcframework");
            if (Directory.Exists(sameDirPath))
                return Path.GetFullPath(sameDirPath);

            // Parent directory
            var grandparentDir = Path.GetDirectoryName(parentDir);
            if (grandparentDir != null)
            {
                var parentDirPath = Path.Combine(grandparentDir, $"{frameworkName}.xcframework");
                if (Directory.Exists(parentDirPath))
                    return Path.GetFullPath(parentDirPath);
            }

            return null;
        }

        /// <summary>
        /// Analyzes binary dependencies of a dylib and resolves matching sibling xcframeworks.
        /// </summary>
        /// <param name="dylibPath">Path to the primary dylib.</param>
        /// <param name="xcframeworkPath">Path to the primary xcframework (for sibling search).</param>
        /// <param name="primaryModuleName">Primary module name (to filter self-references).</param>
        /// <param name="platformTarget">Platform target for xcframework slice selection.</param>
        /// <param name="wrapperArchitectures">Wrapper architectures scope.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        /// <returns>Analysis result, or null if otool fails.</returns>
        public static DependencyAnalysisResult? Analyze(
            string dylibPath,
            string xcframeworkPath,
            string primaryModuleName,
            XCFrameworkPlatformTarget platformTarget,
            string wrapperArchitectures,
            ILogger logger,
            ICommandRunner? commandRunner = null)
        {
            var runner = commandRunner ?? new SystemCommandRunner();

            // Run otool -L on the primary dylib
            var (exitCode, stdout, stderr) = runner.Run("otool", $"-L \"{dylibPath}\"");
            if (exitCode != 0)
            {
                logger.LogWarning("otool -L failed (exit code {ExitCode}): {StdErr}. " +
                    "Automatic dependency detection skipped.", exitCode, stderr);
                return null;
            }

            // Parse output
            var detected = ParseOtoolOutput(stdout, primaryModuleName);
            if (detected.Count == 0)
            {
                return new DependencyAnalysisResult
                {
                    ResolvedDependencies = new List<FrameworkDependencyInfo>(),
                    UnresolvedDependencies = new List<DetectedDependency>(),
                    AllDetected = new List<DetectedDependency>()
                };
            }

            var resolved = new List<FrameworkDependencyInfo>();
            var unresolved = new List<DetectedDependency>();

            foreach (var dep in detected)
            {
                // Search for sibling xcframework
                var siblingPath = FindSiblingXCFramework(xcframeworkPath, dep.FrameworkName);
                if (siblingPath == null)
                {
                    unresolved.Add(dep with { UnresolvedReason = "no-xcframework" });
                    continue;
                }

                // Resolve the xcframework
                try
                {
                    var depResolution = XCFrameworkResolver.Resolve(
                        siblingPath, Path.GetTempPath(), platformTarget, logger, runner);

                    // Resolve both slices if needed
                    string? simSearchPath = null;
                    string? deviceSearchPath = null;

                    if (depResolution.IsSimulatorSlice)
                        simSearchPath = depResolution.FrameworkSearchPath;
                    else
                        deviceSearchPath = depResolution.FrameworkSearchPath;

                    // Resolve opposite/required slice — mirrors manual ResolveFrameworkDependencies logic.
                    // If a required slice is missing, demote to unresolved rather than keeping
                    // incomplete search paths that cause confusing wrapper compile failures.
                    // IMPORTANT: Derive opposite target from actual resolved slice (depResolution.IsSimulatorSlice),
                    // not the requested platformTarget, because XCFrameworkResolver.Resolve can fall back
                    // to a different slice. Also verify returned slices match expectations.
                    bool sliceMissing = false;
                    if (wrapperArchitectures == "all")
                    {
                        var oppositeTarget = depResolution.IsSimulatorSlice
                            ? XCFrameworkPlatformTarget.Device
                            : XCFrameworkPlatformTarget.Simulator;
                        try
                        {
                            var oppositeResolution = XCFrameworkResolver.Resolve(
                                siblingPath, Path.GetTempPath(), oppositeTarget, logger, runner);
                            var expectSimulator = oppositeTarget == XCFrameworkPlatformTarget.Simulator;
                            if (oppositeResolution.IsSimulatorSlice == expectSimulator)
                            {
                                if (oppositeResolution.IsSimulatorSlice)
                                    simSearchPath = oppositeResolution.FrameworkSearchPath;
                                else
                                    deviceSearchPath = oppositeResolution.FrameworkSearchPath;
                            }
                            else
                            {
                                // Resolver fell back to same slice type we already have
                                logger.LogWarning(
                                    "Auto-detected dependency '{Name}' lacks required {Target} slice. " +
                                    "Use --framework-dependency to specify its location manually.",
                                    dep.FrameworkName,
                                    oppositeTarget.ToString().ToLowerInvariant());
                                sliceMissing = true;
                            }
                        }
                        catch
                        {
                            logger.LogWarning(
                                "Auto-detected dependency '{Name}' lacks required {Target} slice. " +
                                "Use --framework-dependency to specify its location manually.",
                                dep.FrameworkName,
                                oppositeTarget.ToString().ToLowerInvariant());
                            sliceMissing = true;
                        }
                    }
                    else if (wrapperArchitectures == "device" && simSearchPath != null && deviceSearchPath == null)
                    {
                        // Primary resolved simulator but we need device
                        try
                        {
                            var deviceResolution = XCFrameworkResolver.Resolve(
                                siblingPath, Path.GetTempPath(),
                                XCFrameworkPlatformTarget.Device, logger, runner);
                            if (!deviceResolution.IsSimulatorSlice)
                            {
                                deviceSearchPath = deviceResolution.FrameworkSearchPath;
                            }
                            else
                            {
                                // Resolver fell back to simulator — no device slice available
                                logger.LogWarning(
                                    "Auto-detected dependency '{Name}' lacks required device slice. " +
                                    "Use --framework-dependency to specify its location manually.",
                                    dep.FrameworkName);
                                sliceMissing = true;
                            }
                        }
                        catch
                        {
                            logger.LogWarning(
                                "Auto-detected dependency '{Name}' lacks required device slice. " +
                                "Use --framework-dependency to specify its location manually.",
                                dep.FrameworkName);
                            sliceMissing = true;
                        }
                    }
                    else if (wrapperArchitectures == "simulator" && deviceSearchPath != null && simSearchPath == null)
                    {
                        // Primary resolved device but we need simulator
                        try
                        {
                            var simResolution = XCFrameworkResolver.Resolve(
                                siblingPath, Path.GetTempPath(),
                                XCFrameworkPlatformTarget.Simulator, logger, runner);
                            if (simResolution.IsSimulatorSlice)
                            {
                                simSearchPath = simResolution.FrameworkSearchPath;
                            }
                            else
                            {
                                // Resolver fell back to device — no simulator slice available
                                logger.LogWarning(
                                    "Auto-detected dependency '{Name}' lacks required simulator slice. " +
                                    "Use --framework-dependency to specify its location manually.",
                                    dep.FrameworkName);
                                sliceMissing = true;
                            }
                        }
                        catch
                        {
                            logger.LogWarning(
                                "Auto-detected dependency '{Name}' lacks required simulator slice. " +
                                "Use --framework-dependency to specify its location manually.",
                                dep.FrameworkName);
                            sliceMissing = true;
                        }
                    }

                    if (sliceMissing)
                    {
                        unresolved.Add(dep with { UnresolvedReason = "missing-slice" });
                        continue;
                    }

                    // Extract version
                    string? packageVersion = null;
                    try
                    {
                        var metadata = XCFrameworkMetadataExtractor.Extract(
                            depResolution.DylibPath, siblingPath,
                            depResolution.ModuleName, logger, runner);
                        packageVersion = metadata.IsVersionPlaceholder ? null : metadata.PackageVersion;
                    }
                    catch
                    {
                        // Version extraction failure is non-fatal
                    }

                    resolved.Add(new FrameworkDependencyInfo
                    {
                        XCFrameworkPath = siblingPath,
                        ModuleName = depResolution.ModuleName,
                        PackageVersion = packageVersion,
                        SimulatorFrameworkSearchPath = simSearchPath,
                        DeviceFrameworkSearchPath = deviceSearchPath,
                        DylibPath = depResolution.DylibPath
                    });
                }
                catch (SwiftModuleNotFoundException)
                {
                    // Try ObjC-only framework fallback
                    var objcResolution = XCFrameworkResolver.ResolveObjCFramework(
                        siblingPath, platformTarget, logger);
                    if (objcResolution == null)
                    {
                        unresolved.Add(dep with { UnresolvedReason = "no-xcframework" });
                        continue;
                    }

                    string? simPath = null, devicePath = null;
                    if (objcResolution.IsSimulatorSlice)
                        simPath = objcResolution.FrameworkSearchPath;
                    else
                        devicePath = objcResolution.FrameworkSearchPath;

                    // Resolve required slices for ObjC deps — same logic as Swift deps
                    bool objcSliceMissing = false;
                    if (wrapperArchitectures == "all")
                    {
                        var oppositeTarget = objcResolution.IsSimulatorSlice
                            ? XCFrameworkPlatformTarget.Device
                            : XCFrameworkPlatformTarget.Simulator;
                        var oppositeObjc = XCFrameworkResolver.ResolveObjCFramework(
                            siblingPath, oppositeTarget, logger);
                        var expectSim = oppositeTarget == XCFrameworkPlatformTarget.Simulator;
                        if (oppositeObjc != null && oppositeObjc.IsSimulatorSlice == expectSim)
                        {
                            if (oppositeObjc.IsSimulatorSlice)
                                simPath = oppositeObjc.FrameworkSearchPath;
                            else
                                devicePath = oppositeObjc.FrameworkSearchPath;
                        }
                        else
                        {
                            logger.LogWarning(
                                "Auto-detected ObjC dependency '{Name}' lacks required {Target} slice. " +
                                "Use --framework-dependency to specify its location manually.",
                                dep.FrameworkName,
                                oppositeTarget.ToString().ToLowerInvariant());
                            objcSliceMissing = true;
                        }
                    }
                    else if (wrapperArchitectures == "device" && simPath != null && devicePath == null)
                    {
                        var deviceObjc = XCFrameworkResolver.ResolveObjCFramework(
                            siblingPath, XCFrameworkPlatformTarget.Device, logger);
                        if (deviceObjc != null && !deviceObjc.IsSimulatorSlice)
                            devicePath = deviceObjc.FrameworkSearchPath;
                        else
                        {
                            logger.LogWarning(
                                "Auto-detected ObjC dependency '{Name}' lacks required device slice. " +
                                "Use --framework-dependency to specify its location manually.",
                                dep.FrameworkName);
                            objcSliceMissing = true;
                        }
                    }
                    else if (wrapperArchitectures == "simulator" && devicePath != null && simPath == null)
                    {
                        var simObjc = XCFrameworkResolver.ResolveObjCFramework(
                            siblingPath, XCFrameworkPlatformTarget.Simulator, logger);
                        if (simObjc != null && simObjc.IsSimulatorSlice)
                            simPath = simObjc.FrameworkSearchPath;
                        else
                        {
                            logger.LogWarning(
                                "Auto-detected ObjC dependency '{Name}' lacks required simulator slice. " +
                                "Use --framework-dependency to specify its location manually.",
                                dep.FrameworkName);
                            objcSliceMissing = true;
                        }
                    }

                    if (objcSliceMissing)
                    {
                        unresolved.Add(dep with { UnresolvedReason = "missing-slice" });
                        continue;
                    }

                    resolved.Add(new FrameworkDependencyInfo
                    {
                        XCFrameworkPath = siblingPath,
                        ModuleName = objcResolution.ModuleName,
                        SimulatorFrameworkSearchPath = simPath,
                        DeviceFrameworkSearchPath = devicePath,
                        IsObjCOnly = true
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        "Failed to resolve auto-detected dependency '{Name}': {Message}",
                        dep.FrameworkName, ex.Message);
                    unresolved.Add(dep with { UnresolvedReason = "no-xcframework" });
                }
            }

            return new DependencyAnalysisResult
            {
                ResolvedDependencies = resolved,
                UnresolvedDependencies = unresolved,
                AllDetected = detected
            };
        }

        /// <summary>
        /// Builds a dependency graph by running otool -L on each dependency's dylib.
        /// The graph maps each module to the list of modules it depends on.
        /// </summary>
        /// <param name="primaryModuleName">The primary module name.</param>
        /// <param name="primaryDylibPath">Path to the primary module's dylib.</param>
        /// <param name="effectiveDeps">All effective dependencies (auto + manual merged).</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        /// <returns>Adjacency list and list of warning strings.</returns>
        public static (Dictionary<string, List<string>> Graph, List<string> Warnings) BuildDependencyGraph(
            string primaryModuleName,
            string primaryDylibPath,
            List<FrameworkDependencyInfo>? effectiveDeps,
            ICommandRunner? commandRunner = null,
            string? primaryXCFrameworkPath = null)
        {
            var runner = commandRunner ?? new SystemCommandRunner();
            var graph = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var warnings = new List<string>();

            // Build framework-name → module-name mapping.
            // Framework binary name (from otool install name) may differ from Swift module name
            // (e.g., framework binary "StripeCore" but module "StripePayments").
            // Register both module name and xcframework-derived framework name for each.
            var frameworkToModule = new Dictionary<string, string>(StringComparer.Ordinal);
            frameworkToModule[primaryModuleName] = primaryModuleName;
            if (!string.IsNullOrEmpty(primaryXCFrameworkPath))
            {
                var primaryFwName = Path.GetFileNameWithoutExtension(primaryXCFrameworkPath);
                if (!string.IsNullOrEmpty(primaryFwName))
                    frameworkToModule[primaryFwName] = primaryModuleName;
            }
            if (effectiveDeps != null)
            {
                foreach (var dep in effectiveDeps)
                {
                    // Module name is authoritative; also register framework name from xcframework path
                    frameworkToModule[dep.ModuleName] = dep.ModuleName;
                    var fwName = Path.GetFileNameWithoutExtension(dep.XCFrameworkPath);
                    if (!string.IsNullOrEmpty(fwName))
                        frameworkToModule[fwName] = dep.ModuleName;
                }
            }

            // Analyze primary
            var primaryDeps = AnalyzeDylibDeps(primaryDylibPath, primaryModuleName, frameworkToModule, runner, warnings);
            graph[primaryModuleName] = primaryDeps;

            // Analyze each dependency
            if (effectiveDeps != null)
            {
                foreach (var dep in effectiveDeps)
                {
                    if (dep.DylibPath == null)
                    {
                        // ObjC-only or unresolved — no dylib to analyze
                        graph[dep.ModuleName] = new List<string>();
                        continue;
                    }

                    var depDeps = AnalyzeDylibDeps(dep.DylibPath, dep.ModuleName, frameworkToModule, runner, warnings);
                    graph[dep.ModuleName] = depDeps;
                }
            }

            return (graph, warnings);
        }

        private static List<string> AnalyzeDylibDeps(
            string dylibPath, string moduleName,
            Dictionary<string, string> frameworkToModule,
            ICommandRunner runner,
            List<string> warnings)
        {
            var (exitCode, stdout, _) = runner.Run("otool", $"-L \"{dylibPath}\"");
            if (exitCode != 0)
            {
                warnings.Add($"Could not analyze '{moduleName}': otool exit code {exitCode}");
                return new List<string>();
            }

            var detected = ParseOtoolOutput(stdout, moduleName);
            // Map framework names to module names, filtering to known deps only
            var result = new List<string>();
            foreach (var d in detected)
            {
                if (frameworkToModule.TryGetValue(d.FrameworkName, out var mappedModule))
                    result.Add(mappedModule);
            }
            return result;
        }
    }
}
