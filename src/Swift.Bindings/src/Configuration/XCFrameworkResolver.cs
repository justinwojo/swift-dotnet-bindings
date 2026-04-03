// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
    /// <summary>
    /// Thrown when an xcframework has no .swiftmodule directory.
    /// Distinct from generic InvalidOperationException to allow typed catch filtering.
    /// </summary>
    public class SwiftModuleNotFoundException : InvalidOperationException
    {
        public SwiftModuleNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown when an xcframework contains a static library instead of a dynamic library.
    /// Static frameworks can still be valid ObjC frameworks — the caller should fall back
    /// to ObjC resolution.
    /// </summary>
    public class StaticLibraryException : InvalidOperationException
    {
        public StaticLibraryException(string message) : base(message) { }
    }

    /// <summary>
    /// Platform target for xcframework slice selection.
    /// </summary>
    public enum XCFrameworkPlatformTarget
    {
        Simulator,
        Device
    }

    /// <summary>
    /// Result of resolving an xcframework to generator inputs.
    /// </summary>
    public sealed class XCFrameworkResolution
    {
        public required string AbiJsonPath { get; init; }
        public required string DylibPath { get; init; }
        public required string TbdPath { get; init; }
        public string? SwiftInterfacePath { get; init; }
        public required string ModuleName { get; init; }
        public required string XCFrameworkPath { get; init; }
        /// <summary>
        /// The slice directory path used as the -F flag target for swiftc.
        /// E.g., "{xcframeworkPath}/{LibraryIdentifier}/".
        /// </summary>
        public required string FrameworkSearchPath { get; init; }
        /// <summary>
        /// The slice identifier from the xcframework (e.g., "ios-arm64_x86_64-simulator").
        /// </summary>
        public required string LibraryIdentifier { get; init; }
        /// <summary>
        /// True when the resolved slice has SupportedPlatformVariant == "simulator".
        /// </summary>
        public required bool IsSimulatorSlice { get; init; }
        /// <summary>
        /// The architecture selected for this resolution (e.g., "arm64", "x86_64").
        /// </summary>
        public required string SelectedArchitecture { get; init; }
    }

    /// <summary>
    /// Abstraction for running external commands, enabling unit testing.
    /// </summary>
    public interface ICommandRunner
    {
        (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000);
    }

    /// <summary>
    /// Default command runner that delegates to System.Diagnostics.Process.
    /// </summary>
    public sealed class SystemCommandRunner : ICommandRunner
    {
        public (int ExitCode, string StdOut, string StdErr) Run(string command, string arguments, int timeoutMs = 30000)
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start process: {command}");

            // Read stdout and stderr concurrently on background tasks, gated
            // by a shared CancellationToken tied to the timeout. This ensures
            // that if the process hangs (never closes pipes), ReadToEnd won't
            // block us past the deadline — the token cancels both reads and we
            // kill the process.
            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
            var ct = cts.Token;

            var stdoutTask = System.Threading.Tasks.Task.Run(() => process.StandardOutput.ReadToEnd(), ct);
            var stderrTask = System.Threading.Tasks.Task.Run(() => process.StandardError.ReadToEnd(), ct);

            try
            {
                System.Threading.Tasks.Task.WaitAll(new[] { stdoutTask, stderrTask, process.WaitForExitAsync(ct) }, ct);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill();
                throw new TimeoutException($"Command timed out after {timeoutMs}ms: {command} {arguments}");
            }

            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : "";

            return (process.ExitCode, stdout.Trim(), stderr.Trim());
        }
    }

    /// <summary>
    /// Parsed representation of an xcframework library slice from Info.plist.
    /// </summary>
    internal sealed class XCFrameworkSlice
    {
        public required string BinaryPath { get; init; }
        public required string LibraryIdentifier { get; init; }
        public required string LibraryPath { get; init; }
        public required List<string> SupportedArchitectures { get; init; }
        public required string SupportedPlatform { get; init; }
        public string? SupportedPlatformVariant { get; init; }
    }

    /// <summary>
    /// Resolves an xcframework directory to all generator inputs (ABI JSON, dylib, TBD, swiftinterface).
    /// </summary>
    public sealed class XCFrameworkResolver
    {
        /// <summary>
        /// Resolves an xcframework to generator inputs for the specified platform target.
        /// </summary>
        public static XCFrameworkResolution Resolve(
            string xcframeworkPath,
            string outputDirectory,
            XCFrameworkPlatformTarget platformTarget,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            PlatformInfo? platformInfo = null)
        {
            commandRunner ??= new SystemCommandRunner();
            xcframeworkPath = Path.GetFullPath(xcframeworkPath);

            // 1. Validate xcframework
            ValidateXCFramework(xcframeworkPath);

            // 2. Parse Info.plist
            var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
            var slices = ParseInfoPlist(plistPath);

            // 3. Select platform slice
            var slice = SelectSlice(slices, platformTarget, logger, platformInfo);

            // 4. Detect static xcframework (LibraryPath without .framework)
            if (!slice.LibraryPath.Contains(".framework"))
            {
                throw new StaticLibraryException(
                    "SWIFTBIND101: Static xcframeworks (.a archives) are not supported for Swift binding. " +
                    "This may be an ObjC framework distributed as a static library.");
            }

            // 5. Find dylib and verify it's dynamic
            // Infer BinaryPath from LibraryPath if missing (wrapper xcframeworks may omit it)
            var binaryPath = slice.BinaryPath;
            if (string.IsNullOrEmpty(binaryPath) && slice.LibraryPath.EndsWith(".framework", StringComparison.Ordinal))
            {
                var frameworkName = Path.GetFileNameWithoutExtension(slice.LibraryPath);
                binaryPath = $"{slice.LibraryPath}/{frameworkName}";
            }
            var dylibPath = Path.Combine(xcframeworkPath, slice.LibraryIdentifier, binaryPath);
            if (!File.Exists(dylibPath))
            {
                throw new FileNotFoundException($"Dylib not found at expected path: '{dylibPath}'.");
            }
            VerifyDynamicLibrary(dylibPath, commandRunner);

            // 6. Discover Swift module
            var modulesDir = Path.Combine(xcframeworkPath, slice.LibraryIdentifier, slice.LibraryPath, "Modules");
            var (swiftModuleDir, moduleName) = DiscoverSwiftModule(modulesDir);

            // 7. Select architecture
            var selectedArch = slice.SupportedArchitectures.Contains("arm64")
                ? "arm64"
                : slice.SupportedArchitectures[0];

            // 8. Find swiftinterface
            var swiftInterfacePath = FindSwiftInterface(swiftModuleDir, selectedArch);
            if (swiftInterfacePath != null)
            {
                logger.LogInformation("Found Swift interface: {Path}", swiftInterfacePath);
            }
            else
            {
                logger.LogInformation("No swiftinterface found; internal member detection will be limited.");
            }

            // 9. Find or generate ABI JSON
            var abiJsonPath = FindOrGenerateAbiJson(
                swiftModuleDir, selectedArch, swiftInterfacePath, slice,
                moduleName, outputDirectory, commandRunner, logger);

            // 10. Find or generate TBD
            var tbdPath = FindOrGenerateTbd(
                swiftModuleDir, dylibPath, moduleName, outputDirectory, commandRunner, logger);

            logger.LogInformation("Resolved xcframework '{Module}': ABI={Abi}, Dylib={Dylib}, TBD={Tbd}",
                moduleName, abiJsonPath, dylibPath, tbdPath);

            return new XCFrameworkResolution
            {
                AbiJsonPath = abiJsonPath,
                DylibPath = dylibPath,
                TbdPath = tbdPath,
                SwiftInterfacePath = swiftInterfacePath,
                ModuleName = moduleName,
                XCFrameworkPath = xcframeworkPath,
                FrameworkSearchPath = Path.Combine(xcframeworkPath, slice.LibraryIdentifier),
                LibraryIdentifier = slice.LibraryIdentifier,
                IsSimulatorSlice = string.Equals(slice.SupportedPlatformVariant, "simulator", StringComparison.OrdinalIgnoreCase),
                SelectedArchitecture = selectedArch
            };
        }

        /// <summary>
        /// Resolves an xcframework to generator inputs for all available iOS slices (simulator + device).
        /// Returns the primary (simulator) resolution plus an optional device resolution.
        /// </summary>
        public static (XCFrameworkResolution Simulator, XCFrameworkResolution? Device) ResolveAll(
            string xcframeworkPath,
            string outputDirectory,
            ILogger logger,
            ICommandRunner? commandRunner = null,
            PlatformInfo? platformInfo = null)
        {
            commandRunner ??= new SystemCommandRunner();
            xcframeworkPath = Path.GetFullPath(xcframeworkPath);

            ValidateXCFramework(xcframeworkPath);

            var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
            var slices = ParseInfoPlist(plistPath);

            var plistPlatform = platformInfo?.PlistPlatformString ?? "ios";
            var isCatalyst = platformInfo?.Platform == ApplePlatform.MacCatalyst;

            // For platforms without simulator (macOS, Catalyst), resolve device only
            if (platformInfo != null && !platformInfo.HasSimulatorVariant)
            {
                var deviceResolution = Resolve(xcframeworkPath, outputDirectory,
                    XCFrameworkPlatformTarget.Device, logger, commandRunner, platformInfo);
                return (deviceResolution, null);
            }

            // Always resolve simulator (primary)
            var simSlice = isCatalyst ? null : slices.FirstOrDefault(s =>
                s.SupportedPlatform.Equals(plistPlatform, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.SupportedPlatformVariant, "simulator", StringComparison.OrdinalIgnoreCase));

            if (simSlice == null)
            {
                var platformName = platformInfo?.Platform.ToString() ?? "iOS";
                throw new InvalidOperationException(
                    $"No {platformName} simulator slice found. The xcframework must contain a simulator slice for binding generation.");
            }

            var simResolution = Resolve(xcframeworkPath, outputDirectory,
                XCFrameworkPlatformTarget.Simulator, logger, commandRunner, platformInfo);

            // Try to resolve device slice
            XCFrameworkResolution? deviceResolution2 = null;
            var deviceSlice = slices.FirstOrDefault(s =>
                s.SupportedPlatform.Equals(plistPlatform, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(s.SupportedPlatformVariant, "simulator", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(s.SupportedPlatformVariant, "maccatalyst", StringComparison.OrdinalIgnoreCase));

            if (deviceSlice != null)
            {
                try
                {
                    deviceResolution2 = Resolve(xcframeworkPath, outputDirectory,
                        XCFrameworkPlatformTarget.Device, logger, commandRunner, platformInfo);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Could not resolve device slice: {Message}", ex.Message);
                }
            }

            return (simResolution, deviceResolution2);
        }

        /// <summary>
        /// Result of resolving an ObjC-only xcframework.
        /// </summary>
        public sealed record ObjCFrameworkResolution(
            string FrameworkSearchPath,
            bool IsSimulatorSlice,
            string ModuleName,
            string FrameworkDirectoryName);

        /// <summary>
        /// Resolves an ObjC-only xcframework to its framework search path and module name.
        /// Validates the framework has a module.modulemap (confirming it's genuinely ObjC,
        /// not a broken Swift framework). Returns null if resolution fails.
        /// </summary>
        public static ObjCFrameworkResolution? ResolveObjCFramework(
            string xcframeworkPath,
            XCFrameworkPlatformTarget platformTarget,
            ILogger logger,
            PlatformInfo? platformInfo = null)
        {
            try
            {
                xcframeworkPath = Path.GetFullPath(xcframeworkPath);
                ValidateXCFramework(xcframeworkPath);
                var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
                var slices = ParseInfoPlist(plistPath);
                var slice = SelectSlice(slices, platformTarget, logger, platformInfo);

                var sliceDir = Path.Combine(xcframeworkPath, slice.LibraryIdentifier);

                // Verify this is genuinely an ObjC framework (has module.modulemap)
                // — not a broken Swift framework missing library evolution.
                var modulesDir = Path.Combine(sliceDir, slice.LibraryPath, "Modules");
                var modulemapPath = Path.Combine(modulesDir, "module.modulemap");
                if (!File.Exists(modulemapPath))
                {
                    logger.LogWarning(
                        "Framework at '{Path}' has no module.modulemap — " +
                        "may be a Swift framework without library evolution, not an ObjC framework.",
                        xcframeworkPath);
                    return null;
                }

                // Extract module name from modulemap. Falls back to xcframework filename.
                var moduleName = ParseModuleNameFromModulemap(modulemapPath)
                    ?? Path.GetFileNameWithoutExtension(xcframeworkPath);

                var isSimulator = string.Equals(
                    slice.SupportedPlatformVariant, "simulator",
                    StringComparison.OrdinalIgnoreCase);

                // Framework directory name from LibraryPath (e.g., "Foo.framework" → "Foo")
                var fwDirName = Path.GetFileNameWithoutExtension(slice.LibraryPath);

                return new ObjCFrameworkResolution(sliceDir, isSimulator, moduleName, fwDirName);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not resolve ObjC framework: {Message}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Resolves only framework search paths from an xcframework, without requiring
        /// a Swift module or ObjC modulemap. Used for wrapper xcframeworks (compiled binding
        /// wrappers) that only need -F search paths for linking during wrapper compilation.
        /// Returns a FrameworkDependencyInfo with search paths, or null if resolution fails.
        /// </summary>
        public static FrameworkDependencyInfo? ResolveSearchPathsOnly(
            string xcframeworkPath,
            string wrapperArchitectures,
            ILogger logger,
            PlatformInfo? platformInfo = null)
        {
            try
            {
                xcframeworkPath = Path.GetFullPath(xcframeworkPath);
                ValidateXCFramework(xcframeworkPath);
                var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
                var slices = ParseInfoPlist(plistPath);
                var moduleName = Path.GetFileNameWithoutExtension(xcframeworkPath);

                string? simSearchPath = null;
                string? deviceSearchPath = null;

                // Resolve simulator slice search path
                try
                {
                    var simSlice = SelectSlice(slices, XCFrameworkPlatformTarget.Simulator, logger, platformInfo);
                    simSearchPath = Path.Combine(xcframeworkPath, simSlice.LibraryIdentifier);
                }
                catch { /* No simulator slice — acceptable for device-only builds */ }

                // Resolve device slice search path
                if (wrapperArchitectures == "all" || wrapperArchitectures == "device")
                {
                    try
                    {
                        var devSlice = SelectSlice(slices, XCFrameworkPlatformTarget.Device, logger, platformInfo);
                        deviceSearchPath = Path.Combine(xcframeworkPath, devSlice.LibraryIdentifier);
                    }
                    catch { /* No device slice — acceptable for simulator-only builds */ }
                }

                if (simSearchPath == null && deviceSearchPath == null)
                    return null;

                return new FrameworkDependencyInfo
                {
                    XCFrameworkPath = xcframeworkPath,
                    ModuleName = moduleName,
                    SimulatorFrameworkSearchPath = simSearchPath,
                    DeviceFrameworkSearchPath = deviceSearchPath,
                    IsObjCOnly = true, // Treated like ObjC: search path only, no binding/packaging
                };
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not resolve search paths for framework: {Message}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Detects whether a Swift framework also has ObjC API surface (mixed framework).
        /// Returns an ObjCFrameworkResolution if the framework has a module.modulemap with
        /// non-Swift headers, or null if it's Swift-only.
        /// The caller should run the ObjC pipeline and check if meaningful types exist (post-hoc validation).
        /// </summary>
        public static ObjCFrameworkResolution? DetectMixedFrameworkObjC(
            XCFrameworkResolution swiftResolution,
            XCFrameworkPlatformTarget platformTarget,
            ILogger logger,
            PlatformInfo? platformInfo = null)
        {
            try
            {
                var xcframeworkPath = swiftResolution.XCFrameworkPath;
                var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
                var slices = ParseInfoPlist(plistPath);
                var slice = SelectSlice(slices, platformTarget, logger, platformInfo);

                var sliceDir = Path.Combine(xcframeworkPath, slice.LibraryIdentifier);
                var modulesDir = Path.Combine(sliceDir, slice.LibraryPath, "Modules");
                var modulemapPath = Path.Combine(modulesDir, "module.modulemap");

                // No modulemap → Swift-only (e.g., Alamofire, Nuke)
                if (!File.Exists(modulemapPath))
                    return null;

                // Parse the actual ObjC module name from the modulemap — it may differ
                // from the Swift module name (e.g., different umbrella module declaration).
                var objcModuleName = ParseModuleNameFromModulemap(modulemapPath)
                    ?? swiftResolution.ModuleName;

                // Check Headers/ directory for files beyond {Module}-Swift.h
                var headersDir = Path.Combine(sliceDir, slice.LibraryPath, "Headers");
                if (!Directory.Exists(headersDir))
                    return null;

                // Filter out both Swift-module-name and ObjC-module-name Swift headers
                var swiftHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    $"{swiftResolution.ModuleName}-Swift.h",
                    $"{objcModuleName}-Swift.h"
                };
                var headers = Directory.GetFiles(headersDir, "*.h")
                    .Select(Path.GetFileName)
                    .Where(h => !swiftHeaders.Contains(h!))
                    .ToList();

                // If the ONLY headers are Swift-generated → not mixed (Kingfisher/RxSwift pattern)
                if (headers.Count == 0)
                {
                    logger.LogDebug("Framework '{Module}' has modulemap but only Swift header(s) — not mixed.",
                        objcModuleName);
                    return null;
                }

                var isSimulator = string.Equals(
                    slice.SupportedPlatformVariant, "simulator",
                    StringComparison.OrdinalIgnoreCase);

                logger.LogInformation(
                    "Detected potential mixed framework '{Module}': {Count} non-Swift header(s) found.",
                    objcModuleName, headers.Count);

                var fwDirName = Path.GetFileNameWithoutExtension(slice.LibraryPath);
                return new ObjCFrameworkResolution(sliceDir, isSimulator, objcModuleName, fwDirName);
            }
            catch (Exception ex)
            {
                logger.LogDebug("Mixed framework detection failed: {Message}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Resolves framework search paths for sibling xcframeworks in the same directory.
        /// This handles the Firebase/Google SDK distribution pattern where all dependency
        /// xcframeworks are co-located in the same parent directory.
        /// </summary>
        public static IReadOnlyList<string> ResolveSiblingFrameworkSearchPaths(
            string xcframeworkPath,
            XCFrameworkPlatformTarget platformTarget,
            ILogger logger,
            PlatformInfo? platformInfo = null)
        {
            var paths = new List<string>();
            var parentDir = Path.GetDirectoryName(Path.GetFullPath(xcframeworkPath));
            if (parentDir == null) return paths;

            var selfName = Path.GetFileName(xcframeworkPath);
            foreach (var siblingDir in Directory.GetDirectories(parentDir, "*.xcframework"))
            {
                if (Path.GetFileName(siblingDir) == selfName) continue;

                try
                {
                    var plistPath = Path.Combine(siblingDir, "Info.plist");
                    if (!File.Exists(plistPath)) continue;
                    var slices = ParseInfoPlist(plistPath);
                    var slice = SelectSlice(slices, platformTarget, logger, platformInfo);
                    var sliceDir = Path.Combine(siblingDir, slice.LibraryIdentifier);
                    if (Directory.Exists(sliceDir))
                        paths.Add(sliceDir);
                }
                catch
                {
                    // Skip unresolvable siblings silently
                }
            }

            if (paths.Count > 0)
                logger.LogInformation("Auto-detected {Count} sibling framework search path(s).", paths.Count);

            return paths;
        }

        /// <summary>
        /// Parses the module name from a module.modulemap file.
        /// Looks for "framework module NAME" or "module NAME" declarations.
        /// </summary>
        internal static string? ParseModuleNameFromModulemap(string modulemapPath)
        {
            foreach (var line in File.ReadLines(modulemapPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("framework module ", StringComparison.Ordinal))
                {
                    var rest = trimmed.Substring("framework module ".Length);
                    var nameEnd = rest.IndexOfAny(new[] { ' ', '{', '[' });
                    return nameEnd > 0 ? rest.Substring(0, nameEnd).Trim() : rest.Trim();
                }
                if (trimmed.StartsWith("module ", StringComparison.Ordinal) &&
                    !trimmed.StartsWith("module *", StringComparison.Ordinal))
                {
                    var rest = trimmed.Substring("module ".Length);
                    var nameEnd = rest.IndexOfAny(new[] { ' ', '{', '[' });
                    return nameEnd > 0 ? rest.Substring(0, nameEnd).Trim() : rest.Trim();
                }
            }
            return null;
        }

        internal static void ValidateXCFramework(string xcframeworkPath)
        {
            if (!Directory.Exists(xcframeworkPath))
            {
                throw new DirectoryNotFoundException($"xcframework not found at '{xcframeworkPath}'.");
            }

            if (!xcframeworkPath.EndsWith(".xcframework", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Path is not an xcframework directory: '{xcframeworkPath}'.");
            }

            var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
            if (!File.Exists(plistPath))
            {
                throw new InvalidOperationException(
                    "Info.plist not found in xcframework. The xcframework may be corrupted.");
            }
        }

        internal static List<XCFrameworkSlice> ParseInfoPlist(string plistPath)
        {
            var doc = new XmlDocument();
            doc.Load(plistPath);

            var rootDict = doc.SelectSingleNode("/plist/dict")
                ?? throw new InvalidOperationException("Invalid Info.plist: missing root dict.");

            var rootData = ParsePlistDict(rootDict);

            if (!rootData.TryGetValue("AvailableLibraries", out var librariesObj) || librariesObj is not List<object> libraries)
            {
                throw new InvalidOperationException(
                    "Invalid Info.plist: missing or empty AvailableLibraries.");
            }

            if (libraries.Count == 0)
            {
                throw new InvalidOperationException(
                    "Invalid Info.plist: missing or empty AvailableLibraries.");
            }

            var slices = new List<XCFrameworkSlice>();
            foreach (var lib in libraries)
            {
                if (lib is not Dictionary<string, object> dict)
                    continue;

                var architectures = new List<string>();
                if (dict.TryGetValue("SupportedArchitectures", out var archObj) && archObj is List<object> archList)
                {
                    architectures.AddRange(archList.OfType<string>());
                }

                slices.Add(new XCFrameworkSlice
                {
                    BinaryPath = dict.TryGetValue("BinaryPath", out var bp) ? bp as string ?? "" : "",
                    LibraryIdentifier = dict.TryGetValue("LibraryIdentifier", out var li) ? li as string ?? "" : "",
                    LibraryPath = dict.TryGetValue("LibraryPath", out var lp) ? lp as string ?? "" : "",
                    SupportedArchitectures = architectures,
                    SupportedPlatform = dict.TryGetValue("SupportedPlatform", out var sp) ? sp as string ?? "" : "",
                    SupportedPlatformVariant = dict.TryGetValue("SupportedPlatformVariant", out var spv) ? spv as string : null
                });
            }

            return slices;
        }

        internal static Dictionary<string, object> ParsePlistDict(XmlNode dictNode)
        {
            var result = new Dictionary<string, object>();
            var children = dictNode.ChildNodes;

            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i]!;
                if (child.Name != "key")
                    continue;

                var key = child.InnerText;
                // Next sibling that is an element (skip whitespace text nodes)
                XmlNode? valueNode = null;
                for (int j = i + 1; j < children.Count; j++)
                {
                    if (children[j]!.NodeType == XmlNodeType.Element && children[j]!.Name != "key")
                    {
                        valueNode = children[j];
                        i = j; // advance past value
                        break;
                    }
                }

                if (valueNode == null)
                    continue;

                result[key] = ParsePlistValue(valueNode);
            }

            return result;
        }

        private static object ParsePlistValue(XmlNode node)
        {
            switch (node.Name)
            {
                case "string":
                    return node.InnerText;
                case "integer":
                    return int.Parse(node.InnerText);
                case "true":
                    return true;
                case "false":
                    return false;
                case "dict":
                    return ParsePlistDict(node);
                case "array":
                    var list = new List<object>();
                    foreach (XmlNode child in node.ChildNodes)
                    {
                        if (child.NodeType == XmlNodeType.Element)
                        {
                            list.Add(ParsePlistValue(child));
                        }
                    }
                    return list;
                default:
                    return node.InnerText;
            }
        }

        internal static XCFrameworkSlice SelectSlice(
            List<XCFrameworkSlice> slices,
            XCFrameworkPlatformTarget platformTarget,
            ILogger logger,
            PlatformInfo? platformInfo = null)
        {
            var plistPlatform = platformInfo?.PlistPlatformString ?? "ios";
            var isCatalyst = platformInfo?.Platform == ApplePlatform.MacCatalyst;

            List<XCFrameworkSlice> platformSlices;
            if (isCatalyst)
            {
                // Mac Catalyst uses SupportedPlatform="ios" + SupportedPlatformVariant="maccatalyst"
                platformSlices = slices.Where(s =>
                    s.SupportedPlatform.Equals("ios", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.SupportedPlatformVariant, "maccatalyst", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            else
            {
                // Normal platforms: filter by PlistPlatformString, exclude maccatalyst
                platformSlices = slices.Where(s =>
                    s.SupportedPlatform.Equals(plistPlatform, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(s.SupportedPlatformVariant, "maccatalyst", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (platformSlices.Count == 0)
            {
                var platformName = platformInfo?.Platform.ToString() ?? "iOS";
                var platforms = slices.Select(s =>
                {
                    var p = s.SupportedPlatform;
                    if (!string.IsNullOrEmpty(s.SupportedPlatformVariant))
                        p += $" ({s.SupportedPlatformVariant})";
                    return p;
                }).Distinct();
                throw new InvalidOperationException(
                    $"No {platformName} platform slices found. Available platforms: {string.Join(", ", platforms)}.");
            }

            var preferSimulator = platformTarget == XCFrameworkPlatformTarget.Simulator;
            var preferred = platformSlices.Where(s =>
                preferSimulator
                    ? string.Equals(s.SupportedPlatformVariant, "simulator", StringComparison.OrdinalIgnoreCase)
                    : string.IsNullOrEmpty(s.SupportedPlatformVariant))
                .ToList();

            if (preferred.Count > 0)
                return preferred[0];

            // Fallback: use whatever platform slice is available
            var platformName2 = platformInfo?.Platform.ToString() ?? "iOS";
            var fallback = platformSlices[0];
            var requestedKind = preferSimulator ? "simulator" : "device";
            var actualKind = string.IsNullOrEmpty(fallback.SupportedPlatformVariant)
                ? "device" : fallback.SupportedPlatformVariant;
            logger.LogWarning(
                "No {Platform} {Requested} slice found. Falling back to {Actual} slice '{Id}'.",
                platformName2, requestedKind, actualKind, fallback.LibraryIdentifier);
            return fallback;
        }

        private static void VerifyDynamicLibrary(string binaryPath, ICommandRunner commandRunner)
        {
            var (exitCode, stdout, stderr) = commandRunner.Run("file", $"\"{binaryPath}\"");
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to verify binary type at '{binaryPath}'. " +
                    $"The 'file' command failed (exit {exitCode}): {stderr}. " +
                    "Ensure Xcode command-line tools are installed: xcode-select --install");
            }

            if (!stdout.Contains("dynamically linked shared library", StringComparison.OrdinalIgnoreCase))
            {
                throw new StaticLibraryException(
                    $"SWIFTBIND101: Binary at '{binaryPath}' is a static library, not a dynamic library. " +
                    "This may be an ObjC framework distributed as a static library.");
            }
        }

        private static (string swiftModuleDir, string moduleName) DiscoverSwiftModule(string modulesDir)
        {
            if (!Directory.Exists(modulesDir))
            {
                throw new SwiftModuleNotFoundException(
                    "SWIFTBIND102: No Swift module found. This may be an Objective-C framework (use ObjC binding tools) or a Swift framework without library evolution.");
            }

            var swiftModules = Directory.GetDirectories(modulesDir, "*.swiftmodule");

            if (swiftModules.Length == 0)
            {
                throw new SwiftModuleNotFoundException(
                    "SWIFTBIND102: No Swift module found. This may be an Objective-C framework (use ObjC binding tools) or a Swift framework without library evolution.");
            }

            if (swiftModules.Length > 1)
            {
                var names = swiftModules.Select(d => Path.GetFileName(d)).OrderBy(n => n);
                throw new InvalidOperationException(
                    $"Multiple Swift modules found: {string.Join(", ", names)}. Multi-module xcframeworks are not yet supported.");
            }

            var moduleDir = swiftModules[0];
            var moduleName = Path.GetFileNameWithoutExtension(moduleDir);
            return (moduleDir, moduleName);
        }

        internal static string? FindSwiftInterface(string swiftModuleDir, string selectedArch)
        {
            // Search for any arch-specific swiftinterface (works across platforms: ios, macos, tvos, etc.)
            var archPattern = $"{selectedArch}-apple-*.swiftinterface";
            var candidates = Directory.GetFiles(swiftModuleDir, archPattern)
                .Where(f => !f.EndsWith(".private.swiftinterface", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (candidates.Count > 0)
                return candidates[0];

            // Fallback: any non-private swiftinterface
            var allInterfaces = Directory.GetFiles(swiftModuleDir, "*.swiftinterface")
                .Where(f => !f.EndsWith(".private.swiftinterface", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            return allInterfaces.Count > 0 ? allInterfaces[0] : null;
        }

        private static string FindOrGenerateAbiJson(
            string swiftModuleDir,
            string selectedArch,
            string? swiftInterfacePath,
            XCFrameworkSlice slice,
            string moduleName,
            string outputDirectory,
            ICommandRunner commandRunner,
            ILogger logger)
        {
            // Try arch-specific ABI JSON first
            var archPattern = $"{selectedArch}-apple-*.abi.json";
            var candidates = Directory.GetFiles(swiftModuleDir, archPattern)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (candidates.Count > 0)
            {
                logger.LogInformation("Found ABI JSON: {Path}", candidates[0]);
                return candidates[0];
            }

            // Fallback: any ABI JSON
            var allAbi = Directory.GetFiles(swiftModuleDir, "*.abi.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (allAbi.Count > 0)
            {
                logger.LogInformation("Found ABI JSON (non-arch-specific): {Path}", allAbi[0]);
                return allAbi[0];
            }

            // Generate from swiftinterface
            if (swiftInterfacePath == null)
            {
                throw new InvalidOperationException(
                    "No ABI JSON or Swift interface found. The framework may not support library evolution.");
            }

            logger.LogInformation("No ABI JSON found. Generating from Swift interface...");
            return GenerateAbiJson(swiftInterfacePath, slice, selectedArch, moduleName, outputDirectory, commandRunner);
        }

        private static string GenerateAbiJson(
            string swiftInterfacePath,
            XCFrameworkSlice slice,
            string selectedArch,
            string moduleName,
            string outputDirectory,
            ICommandRunner commandRunner)
        {
            // Resolve SDK path using platform-aware lookup
            var isSimulator = string.Equals(slice.SupportedPlatformVariant, "simulator", StringComparison.OrdinalIgnoreCase);
            var detectedPlatform = PlatformInfoFactory.DetectFromPlistPlatform(
                slice.SupportedPlatform, slice.SupportedPlatformVariant);
            var detectedPlatformInfo = PlatformInfoFactory.Create(detectedPlatform);
            var sliceVariant = detectedPlatformInfo.GetSlice(isSimulator);
            var sdkName = sliceVariant.SdkName;

            var (sdkExit, sdkPath, sdkErr) = commandRunner.Run("xcrun", $"--sdk {sdkName} --show-sdk-path");
            if (sdkExit != 0 || string.IsNullOrWhiteSpace(sdkPath))
            {
                throw new InvalidOperationException(
                    $"Failed to locate {detectedPlatform} SDK. Ensure Xcode and the platform SDK are installed.");
            }

            // Build target triple from slice variant
            var targetTriple = isSimulator
                ? $"{selectedArch}-apple-{sliceVariant.XCFrameworkPlatformString}-simulator"
                : (detectedPlatform == ApplePlatform.MacCatalyst
                    ? $"{selectedArch}-apple-ios-macabi"
                    : $"{selectedArch}-apple-{sliceVariant.XCFrameworkPlatformString}");

            Directory.CreateDirectory(outputDirectory);
            var abiOutputPath = Path.Combine(outputDirectory, $"{moduleName}.abi.json");

            var args = $"swift-frontend -compile-module-from-interface " +
                       $"\"{swiftInterfacePath}\" " +
                       $"-target {targetTriple} " +
                       $"-module-name {moduleName} " +
                       $"-sdk \"{sdkPath}\" " +
                       $"-emit-abi-descriptor-path \"{abiOutputPath}\"";

            var (exitCode, _, stderr) = commandRunner.Run("xcrun", args, timeoutMs: 60000);
            if (exitCode != 0 || !File.Exists(abiOutputPath))
            {
                throw new InvalidOperationException(
                    $"SWIFTBIND103: swift-frontend failed to extract ABI from Swift interface: {stderr}. Ensure Xcode is installed.");
            }

            return abiOutputPath;
        }

        private static string FindOrGenerateTbd(
            string swiftModuleDir,
            string dylibPath,
            string moduleName,
            string outputDirectory,
            ICommandRunner commandRunner,
            ILogger logger)
        {
            // Search for existing TBD in swiftmodule directory
            var tbdFiles = Directory.GetFiles(swiftModuleDir, "*.tbd");
            if (tbdFiles.Length > 0)
            {
                logger.LogInformation("Found TBD: {Path}", tbdFiles[0]);
                return tbdFiles[0];
            }

            // Generate via tapi
            logger.LogInformation("No TBD found. Generating from dylib...");
            Directory.CreateDirectory(outputDirectory);
            var tbdOutputPath = Path.Combine(outputDirectory, $"{moduleName}.tbd");

            var (exitCode, _, stderr) = commandRunner.Run(
                "xcrun",
                $"tapi stubify --filetype=tbd-v4 \"{dylibPath}\" -o \"{tbdOutputPath}\"",
                timeoutMs: 30000);

            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to generate TBD file. Ensure Xcode command-line tools are installed: xcode-select --install");
            }

            return tbdOutputPath;
        }
    }
}
