// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration
{
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
            ICommandRunner? commandRunner = null)
        {
            commandRunner ??= new SystemCommandRunner();
            xcframeworkPath = Path.GetFullPath(xcframeworkPath);

            // 1. Validate xcframework
            ValidateXCFramework(xcframeworkPath);

            // 2. Parse Info.plist
            var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
            var slices = ParseInfoPlist(plistPath);

            // 3. Select platform slice
            var slice = SelectSlice(slices, platformTarget, logger);

            // 4. Detect static xcframework (LibraryPath without .framework)
            if (!slice.LibraryPath.Contains(".framework"))
            {
                throw new InvalidOperationException(
                    "Static xcframeworks (.a archives) are not supported. Provide a dynamic xcframework (.framework bundle with dylib).");
            }

            // 5. Find dylib and verify it's dynamic
            var dylibPath = Path.Combine(xcframeworkPath, slice.LibraryIdentifier, slice.BinaryPath);
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
                IsSimulatorSlice = string.Equals(slice.SupportedPlatformVariant, "simulator", StringComparison.OrdinalIgnoreCase)
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
            ICommandRunner? commandRunner = null)
        {
            commandRunner ??= new SystemCommandRunner();
            xcframeworkPath = Path.GetFullPath(xcframeworkPath);

            ValidateXCFramework(xcframeworkPath);

            var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
            var slices = ParseInfoPlist(plistPath);

            // Always resolve simulator (primary)
            var simSlice = slices.FirstOrDefault(s =>
                s.SupportedPlatform.Equals("ios", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.SupportedPlatformVariant, "simulator", StringComparison.OrdinalIgnoreCase));

            if (simSlice == null)
            {
                throw new InvalidOperationException(
                    "No iOS simulator slice found. The xcframework must contain a simulator slice for binding generation.");
            }

            var simResolution = Resolve(xcframeworkPath, outputDirectory,
                XCFrameworkPlatformTarget.Simulator, logger, commandRunner);

            // Try to resolve device slice
            XCFrameworkResolution? deviceResolution = null;
            var deviceSlice = slices.FirstOrDefault(s =>
                s.SupportedPlatform.Equals("ios", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(s.SupportedPlatformVariant, "simulator", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(s.SupportedPlatformVariant, "maccatalyst", StringComparison.OrdinalIgnoreCase));

            if (deviceSlice != null)
            {
                try
                {
                    deviceResolution = Resolve(xcframeworkPath, outputDirectory,
                        XCFrameworkPlatformTarget.Device, logger, commandRunner);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Could not resolve device slice: {Message}", ex.Message);
                }
            }

            return (simResolution, deviceResolution);
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
            ILogger logger)
        {
            var iosSlices = slices.Where(s =>
                s.SupportedPlatform.Equals("ios", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(s.SupportedPlatformVariant, "maccatalyst", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (iosSlices.Count == 0)
            {
                var platforms = slices.Select(s =>
                {
                    var p = s.SupportedPlatform;
                    if (!string.IsNullOrEmpty(s.SupportedPlatformVariant))
                        p += $" ({s.SupportedPlatformVariant})";
                    return p;
                }).Distinct();
                throw new InvalidOperationException(
                    $"No iOS platform slices found. Available platforms: {string.Join(", ", platforms)}.");
            }

            var preferSimulator = platformTarget == XCFrameworkPlatformTarget.Simulator;
            var preferred = iosSlices.Where(s =>
                preferSimulator
                    ? string.Equals(s.SupportedPlatformVariant, "simulator", StringComparison.OrdinalIgnoreCase)
                    : string.IsNullOrEmpty(s.SupportedPlatformVariant))
                .ToList();

            if (preferred.Count > 0)
                return preferred[0];

            // Fallback: use whatever iOS slice is available
            var fallback = iosSlices[0];
            var requestedKind = preferSimulator ? "simulator" : "device";
            var actualKind = string.IsNullOrEmpty(fallback.SupportedPlatformVariant)
                ? "device" : fallback.SupportedPlatformVariant;
            logger.LogWarning(
                "No iOS {Requested} slice found. Falling back to {Actual} slice '{Id}'.",
                requestedKind, actualKind, fallback.LibraryIdentifier);
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

            if (stdout.Contains("current ar archive") || stdout.Contains("object file"))
            {
                throw new InvalidOperationException(
                    $"Binary at '{binaryPath}' is a static library, not a dynamic library. Provide a dynamic xcframework.");
            }
        }

        private static (string swiftModuleDir, string moduleName) DiscoverSwiftModule(string modulesDir)
        {
            if (!Directory.Exists(modulesDir))
            {
                throw new InvalidOperationException(
                    "No Swift module found. This may be an Objective-C framework (use ObjC binding tools) or a Swift framework without library evolution.");
            }

            var swiftModules = Directory.GetDirectories(modulesDir, "*.swiftmodule");

            if (swiftModules.Length == 0)
            {
                throw new InvalidOperationException(
                    "No Swift module found. This may be an Objective-C framework (use ObjC binding tools) or a Swift framework without library evolution.");
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
            // Prefer arch-specific swiftinterface
            var archPattern = $"{selectedArch}-apple-ios*.swiftinterface";
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
            var archPattern = $"{selectedArch}-apple-ios*.abi.json";
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
            // Resolve SDK path
            var isSimulator = string.Equals(slice.SupportedPlatformVariant, "simulator", StringComparison.OrdinalIgnoreCase);
            var sdkName = isSimulator ? "iphonesimulator" : "iphoneos";

            var (sdkExit, sdkPath, sdkErr) = commandRunner.Run("xcrun", $"--sdk {sdkName} --show-sdk-path");
            if (sdkExit != 0 || string.IsNullOrWhiteSpace(sdkPath))
            {
                throw new InvalidOperationException(
                    "Failed to locate iOS SDK. Ensure Xcode and iOS SDK are installed.");
            }

            // Build target triple
            var targetTriple = isSimulator
                ? $"{selectedArch}-apple-ios-simulator"
                : $"{selectedArch}-apple-ios";

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
                    $"Failed to extract ABI from Swift interface: {stderr}. Ensure Xcode is installed.");
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
