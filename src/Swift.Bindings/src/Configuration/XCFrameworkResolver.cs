// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
        /// <summary>
        /// All Mach-O architectures the resolved source slice ships (e.g., ["arm64", "x86_64"]
        /// for a fat macOS slice). Lets the wrapper compile match the source's arch coverage
        /// — produce a fat (universal) wrapper when the source is fat, arm64-only otherwise —
        /// without re-parsing the xcframework.
        /// </summary>
        public required IReadOnlyList<string> SupportedArchitectures { get; init; }
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
                // Kill the WHOLE process tree, not just the driver. `xcrun swiftc` forks a
                // swift-frontend child (and clang/ld grandchildren); killing only the driver
                // orphans them — they keep holding CPU/RAM on an already-contended runner and
                // can keep writing to the half-built -o target. entireProcessTree reaps them all.
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch { /* race: child already exited between HasExited and Kill */ }
                }
                // Block (bounded) until the killed tree is actually reaped, so a forked
                // swift-frontend isn't still writing to the half-built -o target when the caller
                // begins cleanup/promotion. The output tree itself is removed by the caller
                // (SwiftWrapperCompiler deletes the unique staging tree); the runner only owns
                // process teardown.
                try { process.WaitForExit(10000); }
                catch { /* best effort */ }
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
            PlatformInfo? platformInfo = null,
            string? requestedArchitecture = null,
            IReadOnlyList<string>? companionFrameworkPaths = null)
        {
            commandRunner ??= new SystemCommandRunner();
            xcframeworkPath = Path.GetFullPath(xcframeworkPath);

            // 1. Validate xcframework
            ValidateXCFramework(xcframeworkPath);

            // 2. Parse Info.plist
            var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
            var slices = ParseInfoPlist(plistPath);

            // 3. Select platform slice. This is the PRIMARY generation target, so record its slice
            //    decision into the input-resolution report (Finding 50) — a fallback here genuinely
            //    shrinks the bound API and must be escalatable under --strict-inputs. Secondary
            //    callers (ObjC detection, search-paths-only, sibling search paths) leave
            //    recordResolution at its default of false.
            var slice = SelectSlice(slices, platformTarget, logger, platformInfo, recordResolution: true);

            // 4. Locate the Modules directory. Frameworks ("Foo.framework") wrap
            //    Modules/ under the bundle; bare-binary slices (libFoo.a /
            //    libFoo.dylib) expose Modules/ at the slice root. Both shapes
            //    are valid Swift framework distributions in the wild.
            var sliceRoot = Path.Combine(xcframeworkPath, slice.LibraryIdentifier);
            var modulesDir = ComputeModulesDir(sliceRoot, slice.LibraryPath);

            // 5. Detection-order rule: Swift evidence wins over binary kind.
            //    If a complete .swiftmodule (with abi.json or swiftinterface)
            //    sits in the slice, take the Swift binding path regardless of
            //    whether the binary is a Mach-O dylib or a static `ar archive`.
            //    Static Swift frameworks (e.g. a static xcframework distribution)
            //    ship an ar-archive binary alongside a complete .swiftmodule —
            //    binding generation only consumes the swiftmodule + abi.json,
            //    so the static-vs-dynamic distinction does not matter for us.
            //    Without this peek, the binary-kind probe at step 7 would
            //    misroute the slice into the ObjC fallback path.
            var selectedArch = SelectArchitecture(slice, requestedArchitecture);
            var swiftEvidence = TryDiscoverSwiftEvidence(modulesDir, selectedArch);

            // 6. If no Swift evidence, reject bare-static slices early so the
            //    caller can route to the ObjC fallback. .framework-wrapped
            //    slices fall through to the dynamic-binary check at step 7.
            if (swiftEvidence == null && !slice.LibraryPath.Contains(".framework"))
            {
                throw new StaticLibraryException(
                    "SWIFTBIND101: Static xcframeworks (.a archives) are not supported for Swift binding. " +
                    "This may be an ObjC framework distributed as a static library.");
            }

            // 7. Resolve the binary path. Wrapper xcframeworks may omit
            //    BinaryPath in Info.plist, so infer it from LibraryPath.
            var binaryPath = slice.BinaryPath;
            if (string.IsNullOrEmpty(binaryPath))
            {
                if (slice.LibraryPath.EndsWith(".framework", StringComparison.Ordinal))
                {
                    var frameworkName = Path.GetFileNameWithoutExtension(slice.LibraryPath);
                    binaryPath = $"{slice.LibraryPath}/{frameworkName}";
                }
                else
                {
                    // Bare-binary slice: LibraryPath itself is the binary file.
                    binaryPath = slice.LibraryPath;
                }
            }
            var dylibPath = Path.Combine(xcframeworkPath, slice.LibraryIdentifier, binaryPath);
            if (!File.Exists(dylibPath))
            {
                throw new FileNotFoundException($"Dylib not found at expected path: '{dylibPath}'.");
            }

            // 8. Verify the binary is dynamic — but only when Swift evidence is
            //    absent. With Swift evidence present we accept either dylib or
            //    static archive (see step 5). For ObjC-shaped slices missing
            //    Swift evidence, this throws StaticLibraryException → ObjC fallback.
            if (swiftEvidence == null)
            {
                VerifyDynamicLibrary(dylibPath, commandRunner);
            }

            // 9. Discover Swift module (or reuse the early-discovered one).
            string swiftModuleDir;
            string moduleName;
            if (swiftEvidence != null)
            {
                swiftModuleDir = swiftEvidence.SwiftModuleDir;
                moduleName = swiftEvidence.ModuleName;
            }
            else
            {
                (swiftModuleDir, moduleName) = DiscoverSwiftModule(modulesDir);
            }

            // 10. Find swiftinterface
            var swiftInterfacePath = swiftEvidence?.SwiftInterfacePath
                ?? FindSwiftInterface(swiftModuleDir, selectedArch);
            if (swiftInterfacePath != null)
            {
                InputResolutionReport.RecordInfo(
                    InputResolutionCategory.SwiftInterface, $"Found swiftinterface '{swiftInterfacePath}'.");
                logger.LogInformation("Found Swift interface: {Path}", swiftInterfacePath);
            }
            else
            {
                // Finding 50: a missing swiftinterface silently limits internal-member detection
                // (@usableFromInline internal members may not be filtered) — a degraded input.
                InputResolutionReport.RecordDegradation(
                    InputResolutionCategory.SwiftInterface,
                    "No swiftinterface found; internal-member detection will be limited.");
                logger.LogInformation("No swiftinterface found; internal member detection will be limited.");
            }

            // 11. Find or generate ABI JSON
            var abiJsonPath = FindOrGenerateAbiJson(
                swiftModuleDir, selectedArch, swiftInterfacePath, slice,
                moduleName, outputDirectory, commandRunner, logger,
                xcframeworkPath, platformTarget, platformInfo, companionFrameworkPaths);

            // 12. Find or generate TBD
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
                SelectedArchitecture = selectedArch,
                SupportedArchitectures = slice.SupportedArchitectures.ToList()
            };
        }

        /// <summary>
        /// Picks the Mach-O architecture to resolve from a slice. When
        /// <paramref name="requestedArchitecture"/> is null the historical preference is kept
        /// (arm64 if present, else the slice's first arch). When a specific architecture is
        /// requested — the Intel/x86_64 path — it must actually be present in the slice's fat
        /// binary, otherwise resolution fails loud rather than silently falling back to arm64.
        /// </summary>
        internal static string SelectArchitecture(XCFrameworkSlice slice, string? requestedArchitecture)
        {
            if (string.IsNullOrEmpty(requestedArchitecture))
            {
                return slice.SupportedArchitectures.Contains("arm64")
                    ? "arm64"
                    : slice.SupportedArchitectures[0];
            }

            var match = slice.SupportedArchitectures.FirstOrDefault(a =>
                string.Equals(a, requestedArchitecture, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;

            throw new InvalidOperationException(
                $"SWIFTBIND052: slice '{slice.LibraryIdentifier}' (platform '{slice.SupportedPlatform}'" +
                (string.IsNullOrEmpty(slice.SupportedPlatformVariant) ? "" : $"/{slice.SupportedPlatformVariant}") +
                $") does not contain the requested '{requestedArchitecture}' architecture — refusing to fall " +
                $"back to another arch. Available: [{string.Join("+", slice.SupportedArchitectures)}]. The " +
                $"source library must ship a '{requestedArchitecture}' slice for this platform.");
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
            PlatformInfo? platformInfo = null,
            string? requestedArchitecture = null,
            IReadOnlyList<string>? companionFrameworkPaths = null)
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
                    XCFrameworkPlatformTarget.Device, logger, commandRunner, platformInfo, requestedArchitecture,
                    companionFrameworkPaths);
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
                XCFrameworkPlatformTarget.Simulator, logger, commandRunner, platformInfo, requestedArchitecture,
                companionFrameworkPaths);

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
                        XCFrameworkPlatformTarget.Device, logger, commandRunner, platformInfo, requestedArchitecture,
                        companionFrameworkPaths);
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
            PlatformInfo? platformInfo = null,
            bool recordResolution = false)
        {
            try
            {
                xcframeworkPath = Path.GetFullPath(xcframeworkPath);
                ValidateXCFramework(xcframeworkPath);
                var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
                var slices = ParseInfoPlist(plistPath);
                // Finding 50: for a PURE-ObjC binding (the --objc-forced path and the
                // Swift-resolution-failed fallback) this slice choice is the PRIMARY input — record it
                // so a device→simulator fallback is visible and --strict-inputs can fail-close. The
                // mixed-framework secondary detection caller passes recordResolution at its default of
                // false (the Swift slice is primary there and already records).
                var slice = SelectSlice(slices, platformTarget, logger, platformInfo, recordResolution);

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

                // No modulemap → Swift-only framework (no ObjC bridging header)
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

                // If the ONLY headers are Swift-generated → not mixed (Swift-only framework pattern)
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
        /// Resolves a single xcframework to the slice directory that matches the given platform
        /// target, suitable for use as a <c>-F</c> framework search path. Returns <c>null</c> when
        /// the path is not a parseable xcframework or has no matching slice. Best-effort:
        /// never throws, so it can be folded into search-path collection without guarding callers.
        /// </summary>
        internal static string? TryResolveSliceSearchPath(
            string xcframeworkPath,
            XCFrameworkPlatformTarget platformTarget,
            ILogger logger,
            PlatformInfo? platformInfo = null)
        {
            try
            {
                var full = Path.GetFullPath(xcframeworkPath);
                var plistPath = Path.Combine(full, "Info.plist");
                if (!File.Exists(plistPath)) return null;
                var slices = ParseInfoPlist(plistPath);
                var slice = SelectSlice(slices, platformTarget, logger, platformInfo);
                var sliceDir = Path.Combine(full, slice.LibraryIdentifier);
                return Directory.Exists(sliceDir) ? sliceDir : null;
            }
            catch
            {
                // Unparseable / no matching slice — caller treats as "no path".
                return null;
            }
        }

        /// <summary>
        /// Resolves framework search paths for sibling xcframeworks in the same directory.
        /// This handles the multi-xcframework distribution pattern where all dependency
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

                var sliceDir = TryResolveSliceSearchPath(siblingDir, platformTarget, logger, platformInfo);
                if (sliceDir != null)
                    paths.Add(sliceDir);
            }

            if (paths.Count > 0)
                logger.LogInformation("Auto-detected {Count} sibling framework search path(s).", paths.Count);

            return paths;
        }

        /// <summary>
        /// Resolves additional <c>-F</c> framework search paths for frameworks EMBEDDED inside the
        /// slice — the nested-framework distribution shape where a framework ships helper frameworks
        /// under a <c>Frameworks/</c> directory (either at the slice root or inside the primary
        /// <c>.framework</c> bundle). Each returned path is a directory that CONTAINS one or more
        /// <c>.framework</c> bundles, so a public header's <c>#import &lt;Embedded/Header.h&gt;</c>
        /// framework-style import resolves. The slice directory itself is already a search path (it
        /// holds the primary framework and any peer frameworks in the same xcframework slice), so it
        /// is intentionally NOT re-added here. Best-effort: never throws; returns an empty list when
        /// no embedded <c>Frameworks/</c> directory exists.
        /// </summary>
        internal static IReadOnlyList<string> ResolveNestedFrameworkSearchPaths(
            string? sliceDir,
            string? primaryFrameworkPath)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Consider(string? dir)
            {
                if (string.IsNullOrEmpty(dir)) return;
                var frameworksDir = Path.Combine(dir, "Frameworks");
                try
                {
                    if (!Directory.Exists(frameworksDir)) return;
                    if (Directory.GetDirectories(frameworksDir, "*.framework").Length == 0) return;
                    var full = Path.GetFullPath(frameworksDir);
                    if (seen.Add(full)) paths.Add(full);
                }
                catch
                {
                    // Unreadable directory — treat as "no embedded frameworks here".
                }
            }

            Consider(sliceDir);
            Consider(primaryFrameworkPath);
            return paths;
        }

        /// <summary>
        /// Merges explicit dependency <c>-F</c> search paths with auto-detected co-located sibling
        /// xcframework slices for the WRAPPER COMPILE, so a companion xcframework dropped next to the
        /// source resolves its module for <c>swiftc</c> exactly as it already does for ABI extraction
        /// (<see cref="BuildAbiFrameworkSearchPaths"/>). Without this, ABI generation auto-detects a
        /// co-located companion but the wrapper compile fails <c>no such module</c> on the same layout.
        /// Explicit paths keep priority; siblings are appended and de-duplicated (case-sensitive,
        /// normalized via <see cref="Path.GetFullPath(string)"/>). Returns <c>null</c> when the merged
        /// set is empty so callers preserve the historical "no additional search paths" behavior.
        /// </summary>
        public static List<string>? MergeWrapperDependencySearchPaths(
            IReadOnlyList<string>? explicitPaths,
            string xcframeworkPath,
            XCFrameworkPlatformTarget platformTarget,
            ILogger logger,
            PlatformInfo? platformInfo = null)
        {
            var siblings = ResolveSiblingFrameworkSearchPaths(xcframeworkPath, platformTarget, logger, platformInfo);

            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(string? p)
            {
                if (string.IsNullOrEmpty(p)) return;
                var full = Path.GetFullPath(p);
                if (seen.Add(full)) ordered.Add(full);
            }

            if (explicitPaths != null)
                foreach (var p in explicitPaths) Add(p);
            foreach (var p in siblings) Add(p);

            return ordered.Count > 0 ? ordered : null;
        }

        /// <summary>
        /// Builds the ordered, de-duplicated <c>-F</c> framework search-path list for ABI
        /// extraction: the framework's own slice directory first, then explicit companion
        /// (<c>--framework-dependency</c>) slices, then auto-detected co-located sibling slices.
        /// User-provided companions are ordered before auto-detected siblings so an explicit
        /// path wins on a module-name collision. Paths are normalized via <see cref="Path.GetFullPath(string)"/>
        /// and de-duplicated case-sensitively.
        /// </summary>
        internal static IReadOnlyList<string> BuildAbiFrameworkSearchPaths(
            string selfSliceDir,
            IEnumerable<string>? explicitCompanionSlices,
            IEnumerable<string>? siblingSlices)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string? p)
            {
                if (string.IsNullOrEmpty(p)) return;
                var full = Path.GetFullPath(p);
                if (seen.Add(full)) ordered.Add(full);
            }

            Add(selfSliceDir);
            if (explicitCompanionSlices != null)
                foreach (var p in explicitCompanionSlices) Add(p);
            if (siblingSlices != null)
                foreach (var p in siblingSlices) Add(p);

            return ordered;
        }

        /// <summary>
        /// Builds the actionable portion of a SWIFTBIND103 message when ABI extraction failed
        /// because the Swift interface imports companion module(s) that could not be resolved on
        /// any framework search path. Returns an empty string when no missing modules were
        /// detected, so the caller does not claim a misleading cause for an unrelated failure.
        /// </summary>
        internal static string BuildMissingCompanionModuleHint(string moduleName, IReadOnlyList<string> missingModules)
        {
            if (missingModules.Count == 0) return string.Empty;

            var list = string.Join(", ", missingModules.Select(m => $"'{m}'"));
            return $"\n\nThe Swift interface for '{moduleName}' imports companion module(s) {list} that could not " +
                "be found on any framework search path. These modules ship in separate xcframework(s) that must " +
                "be provided so ABI extraction can resolve them:\n" +
                "  - Co-locate (simplest): place the companion .xcframework in the SAME directory as this " +
                "framework — it is auto-detected, or\n" +
                "  - CLI:  --framework-dependency /path/to/<Module>.xcframework  (repeat for each), or\n" +
                "  - SDK:  <SwiftFrameworkDependency Include=\"path/to/<Module>.xcframework\" PackageId=\"...\" " +
                "PackageVersion=\"...\" />\n" +
                "Note: any accompanying \"this SDK is not supported by the compiler\" line is a misleading " +
                "secondary diagnostic from the failed module rebuild — it does not indicate a real " +
                "toolchain-version mismatch.";
        }

        /// <summary>
        /// Returns the native binary path for every slice in the xcframework that exists on
        /// disk (simulator + device + catalyst). Used by the ObjC over-binding guard to union
        /// defined class symbols across all shipped slices, so a class present on only one
        /// slice is not false-dropped from the shared ApiDefinition. Best-effort: parse/IO
        /// failures yield an empty list (the guard then fails open).
        /// </summary>
        public static IReadOnlyList<string> EnumerateObjCSliceNativeBinaries(
            string xcframeworkPath, ILogger logger)
        {
            var result = new List<string>();
            try
            {
                xcframeworkPath = Path.GetFullPath(xcframeworkPath);
                var plistPath = Path.Combine(xcframeworkPath, "Info.plist");
                if (!File.Exists(plistPath))
                    return result;
                foreach (var slice in ParseInfoPlist(plistPath))
                {
                    var binaryPath = ComputeSliceBinaryPath(xcframeworkPath, slice);
                    if (binaryPath != null && File.Exists(binaryPath))
                        result.Add(binaryPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    "Could not enumerate ObjC slice binaries for '{Path}': {Message}",
                    xcframeworkPath, ex.Message);
            }
            return result;
        }

        /// <summary>
        /// Returns the binary path of every <c>*.framework</c> bundle found directly under the
        /// given search paths (slice directories of dependency/sibling xcframeworks). Folded
        /// into the over-binding guard's symbol union so a class declared in this framework's
        /// headers but <em>defined</em> in a linked dependency is not false-dropped — biasing
        /// the removal-only guard toward keeping classes.
        /// </summary>
        public static IReadOnlyList<string> EnumerateFrameworkBinariesUnder(
            IEnumerable<string>? searchPaths, ILogger logger)
        {
            var result = new List<string>();
            if (searchPaths == null)
                return result;
            foreach (var searchPath in searchPaths)
            {
                if (string.IsNullOrEmpty(searchPath) || !Directory.Exists(searchPath))
                    continue;
                try
                {
                    foreach (var fwDir in Directory.GetDirectories(searchPath, "*.framework"))
                    {
                        var fwName = Path.GetFileNameWithoutExtension(fwDir);
                        var binaryPath = Path.Combine(fwDir, fwName);
                        if (File.Exists(binaryPath))
                            result.Add(binaryPath);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        "Could not enumerate framework binaries under '{Path}': {Message}",
                        searchPath, ex.Message);
                }
            }
            return result;
        }

        /// <summary>
        /// Computes the on-disk native binary path for a slice, mirroring the Swift-side
        /// derivation: <c>Foo.framework</c> slices wrap the binary at
        /// <c>Foo.framework/Foo</c>; bare-binary slices expose the binary at LibraryPath.
        /// </summary>
        private static string? ComputeSliceBinaryPath(string xcframeworkPath, XCFrameworkSlice slice)
        {
            if (string.IsNullOrEmpty(slice.LibraryPath))
                return null;
            string binaryRel;
            if (slice.LibraryPath.EndsWith(".framework", StringComparison.Ordinal))
            {
                var frameworkName = Path.GetFileNameWithoutExtension(slice.LibraryPath);
                binaryRel = $"{slice.LibraryPath}/{frameworkName}";
            }
            else
            {
                binaryRel = slice.LibraryPath;
            }
            return Path.Combine(xcframeworkPath, slice.LibraryIdentifier, binaryRel);
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

        /// <summary>
        /// Loads an xcframework Info.plist (binary or XML) and returns its parsed
        /// AvailableLibraries entries as <see cref="XCFrameworkSlice"/> records.
        /// Convenience overload that uses a default command runner and a null logger.
        /// </summary>
        internal static List<XCFrameworkSlice> ParseInfoPlist(string plistPath)
            => ParseInfoPlist(plistPath, null, NullLogger.Instance);

        /// <summary>
        /// Loads an xcframework Info.plist (binary or XML) via <see cref="PlistReader"/>
        /// and returns its parsed AvailableLibraries entries.
        /// </summary>
        internal static List<XCFrameworkSlice> ParseInfoPlist(
            string plistPath, ICommandRunner? commandRunner, ILogger logger)
        {
            var rootDict = PlistReader.ReadPlistDict(plistPath, commandRunner, logger)
                ?? throw new InvalidOperationException(
                    $"Failed to read xcframework Info.plist at '{plistPath}'. The file may be missing or malformed.");

            return ParseAvailableLibraries(rootDict);
        }

        /// <summary>
        /// Extracts <see cref="XCFrameworkSlice"/> entries from an already-parsed
        /// xcframework root Info.plist dictionary. Shared by <see cref="XCFrameworkResolver"/>
        /// and <see cref="XCFrameworkSlicer"/> so both code paths agree on slice metadata.
        /// </summary>
        internal static List<XCFrameworkSlice> ParseAvailableLibraries(Dictionary<string, object> rootDict)
        {
            if (!rootDict.TryGetValue("AvailableLibraries", out var librariesObj) || librariesObj is not List<object> libraries)
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
                    // plist <integer> is a 64-bit signed value (CFNumber). Parse as long so a
                    // value wider than 32 bits (build metadata, a large UID, a timestamp) doesn't
                    // overflow and tear down the entire dict — which would silently degrade the
                    // version + MinimumOSVersion we actually read to a placeholder.
                    return long.Parse(node.InnerText);
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
            PlatformInfo? platformInfo = null,
            bool recordResolution = false)
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
            {
                if (recordResolution)
                    InputResolutionReport.RecordInfo(
                        InputResolutionCategory.SliceSelection,
                        $"Selected {(preferSimulator ? "simulator" : "device")} slice '{preferred[0].LibraryIdentifier}'.");
                return preferred[0];
            }

            // Fallback: use whatever platform slice is available
            var platformName2 = platformInfo?.Platform.ToString() ?? "iOS";
            var fallback = platformSlices[0];
            var requestedKind = preferSimulator ? "simulator" : "device";
            var actualKind = string.IsNullOrEmpty(fallback.SupportedPlatformVariant)
                ? "device" : fallback.SupportedPlatformVariant;
            // Finding 50: a requested slice that falls back to a different kind is an input
            // substitution — record it as a degradation so --strict-inputs can fail-close. Only the
            // PRIMARY generation target's resolution feeds the input-resolution report
            // (recordResolution: true at the Resolve call site); the ObjC-detection, search-paths-only,
            // and best-effort sibling-search-path callers pass it through transitively and must NOT
            // record — a sibling `-F` path's slice fallback is benign and would otherwise (a) trip a
            // false-positive SWIFTBIND027 under --strict-inputs and (b) pollute the manifest's
            // input-resolution snapshot with non-primary-target decisions, including ones recorded
            // after the strict gate already ran (mixed sibling resolution) or on a path that returns
            // before the gate (pure-ObjC).
            if (recordResolution)
                InputResolutionReport.RecordDegradation(
                    InputResolutionCategory.SliceSelection,
                    $"No {platformName2} {requestedKind} slice found; fell back to {actualKind} slice '{fallback.LibraryIdentifier}'.");
            logger.LogWarning(
                "No {Platform} {Requested} slice found. Falling back to {Actual} slice '{Id}'.",
                platformName2, requestedKind, actualKind, fallback.LibraryIdentifier);
            return fallback;
        }

        /// <summary>
        /// Modules directory location for a slice. Frameworks
        /// ("Foo.framework") wrap Modules under the bundle; bare-binary slices
        /// (libFoo.a / libFoo.dylib) expose Modules at the slice root.
        /// </summary>
        private static string ComputeModulesDir(string sliceRoot, string libraryPath)
        {
            if (libraryPath.EndsWith(".framework", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(sliceRoot, libraryPath, "Modules");
            return Path.Combine(sliceRoot, "Modules");
        }

        /// <summary>
        /// Early Swift-evidence peek used by the detection-order rule. If a
        /// .swiftmodule is present and contains either an arch-matching
        /// .swiftinterface or any .abi.json, returns its location so the
        /// resolver can take the Swift path regardless of binary kind.
        /// Returns null when no usable Swift module is found.
        /// </summary>
        private static SwiftEvidence? TryDiscoverSwiftEvidence(string modulesDir, string selectedArch)
        {
            if (!Directory.Exists(modulesDir))
                return null;
            var swiftModules = Directory.GetDirectories(modulesDir, "*.swiftmodule");
            if (swiftModules.Length != 1)
                return null;
            var moduleDir = swiftModules[0];
            var swiftInterface = FindSwiftInterface(moduleDir, selectedArch);
            var hasAbi = Directory.GetFiles(moduleDir, "*.abi.json").Length > 0;
            if (swiftInterface == null && !hasAbi)
                return null;
            return new SwiftEvidence(moduleDir, Path.GetFileNameWithoutExtension(moduleDir), swiftInterface);
        }

        private sealed record SwiftEvidence(string SwiftModuleDir, string ModuleName, string? SwiftInterfacePath);

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
            // Search for any arch-specific swiftinterface (works across platforms: ios, macos, tvos, etc.).
            // Only the public variant is a valid binding input: the `.package.`/`.private.`
            // interfaces expose declarations that aren't part of the bound surface, and their
            // extra dot segment poisons any sibling path derived from the file name (a shadow
            // module compiled from `<triple>.package.swiftinterface` lands at
            // `<triple>.package.swiftmodule`, which swiftc never looks for — the shadow is then
            // silently bypassed in favour of the real, unpatched interface).
            var archPattern = $"{selectedArch}-apple-*.swiftinterface";
            var candidates = Directory.GetFiles(swiftModuleDir, archPattern)
                .Where(SwiftInterfaceVariant.IsPublic)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (candidates.Count > 0)
                return candidates[0];

            // Fallback: any public swiftinterface
            var allInterfaces = Directory.GetFiles(swiftModuleDir, "*.swiftinterface")
                .Where(SwiftInterfaceVariant.IsPublic)
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
            ILogger logger,
            string xcframeworkPath,
            XCFrameworkPlatformTarget platformTarget,
            PlatformInfo? platformInfo,
            IReadOnlyList<string>? companionFrameworkPaths)
        {
            // Try arch-specific ABI JSON first
            var archPattern = $"{selectedArch}-apple-*.abi.json";
            var candidates = Directory.GetFiles(swiftModuleDir, archPattern)
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (candidates.Count > 0)
            {
                InputResolutionReport.RecordInfo(
                    InputResolutionCategory.AbiJson, $"Found arch-specific ABI JSON '{candidates[0]}' for '{selectedArch}'.");
                logger.LogInformation("Found ABI JSON: {Path}", candidates[0]);
                return candidates[0];
            }

            // Fallback: any ABI JSON
            var allAbi = Directory.GetFiles(swiftModuleDir, "*.abi.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            if (allAbi.Count > 0)
            {
                // Finding 50: no ABI JSON for the selected arch; using a non-arch-specific one is an
                // input substitution (the ABI may describe a different arch's layout).
                InputResolutionReport.RecordDegradation(
                    InputResolutionCategory.AbiJson,
                    $"No ABI JSON for arch '{selectedArch}'; fell back to non-arch-specific '{allAbi[0]}'.");
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

            // The Swift interface may `import` companion modules (e.g. a thin Swift wrapper over a
            // separate C/C++ engine). swift-frontend
            // must be able to resolve those modules or it aborts before writing any ABI. Build the `-F`
            // search-path list: the framework's own slice, then explicit --framework-dependency companions,
            // then auto-detected co-located siblings.
            var selfSliceDir = Path.Combine(xcframeworkPath, slice.LibraryIdentifier);
            var explicitSlices = new List<string>();
            foreach (var companionPath in companionFrameworkPaths ?? Array.Empty<string>())
            {
                var companionSlice = TryResolveSliceSearchPath(companionPath, platformTarget, logger, platformInfo);
                if (companionSlice != null)
                    explicitSlices.Add(companionSlice);
                else
                    // A provided companion that doesn't resolve to a slice would otherwise vanish silently and
                    // leave the user staring at a "missing companion module" error for a path they DID supply.
                    logger.LogWarning(
                        "Companion framework path '{Path}' could not be resolved to a {Target} slice and will not " +
                        "be used as an ABI-extraction search path. Verify the path points at a valid .xcframework " +
                        "with a matching slice.", companionPath, platformTarget);
            }
            var siblingSlices = ResolveSiblingFrameworkSearchPaths(xcframeworkPath, platformTarget, logger, platformInfo);
            var searchPaths = BuildAbiFrameworkSearchPaths(selfSliceDir, explicitSlices, siblingSlices);

            return GenerateAbiJson(
                swiftInterfacePath, slice, selectedArch, moduleName, outputDirectory,
                commandRunner, searchPaths, logger);
        }

        private static string GenerateAbiJson(
            string swiftInterfacePath,
            XCFrameworkSlice slice,
            string selectedArch,
            string moduleName,
            string outputDirectory,
            ICommandRunner commandRunner,
            IReadOnlyList<string> frameworkSearchPaths,
            ILogger logger)
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

            // -compile-module-from-interface's primary output is the compiled .swiftmodule itself;
            // without an explicit -o, swift-frontend writes it to the process working directory —
            // the repo root under nuke, or the consumer's project directory under the SDK. Only the
            // ABI descriptor is kept; the module byproduct is redirected here and deleted after the run.
            var moduleByproductPath = Path.Combine(outputDirectory, $"{moduleName}.swiftmodule");

            // `-F` lets swift-frontend resolve companion modules imported by the interface
            // (the framework's own slice plus any companion/sibling xcframework slices).
            var frameworkFlags = string.Concat(frameworkSearchPaths.Select(p => $"-F \"{p}\" "));
            if (frameworkSearchPaths.Count > 0)
                logger.LogInformation("ABI extraction framework search paths: {Paths}", string.Join(", ", frameworkSearchPaths));

            var args = $"swift-frontend -compile-module-from-interface " +
                       $"\"{swiftInterfacePath}\" " +
                       $"-target {targetTriple} " +
                       $"-module-name {moduleName} " +
                       $"-sdk \"{sdkPath}\" " +
                       $"{frameworkFlags}" +
                       $"-emit-abi-descriptor-path \"{abiOutputPath}\" " +
                       $"-o \"{moduleByproductPath}\"";

            var (exitCode, _, stderr) = commandRunner.Run("xcrun", args, timeoutMs: 60000);
            if (File.Exists(moduleByproductPath))
                File.Delete(moduleByproductPath);
            if (exitCode != 0 || !File.Exists(abiOutputPath))
            {
                // When the failure is a missing companion module, name it and explain how to supply
                // it — and call out the misleading "SDK is not supported by the compiler" cascade.
                var missing = SwiftWrapperCompiler.ExtractMissingModules(stderr);
                var hint = BuildMissingCompanionModuleHint(moduleName, missing);
                var stderrPreview = stderr.Length > 4000 ? stderr.Substring(0, 4000) + "…" : stderr;
                throw new InvalidOperationException(
                    $"SWIFTBIND103: swift-frontend failed to extract ABI from Swift interface for module " +
                    $"'{moduleName}'.{hint}\n\nUnderlying swift-frontend error:\n{stderrPreview}\n\n" +
                    "Ensure Xcode and the platform SDK are installed.");
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
            // Search for existing TBD in swiftmodule directory.
            // Finding 50: order deterministically — the previous unsorted GetFiles()[0] picked an
            // arbitrary file when more than one .tbd was present, so the same inputs could resolve
            // to different TBDs across runs.
            var tbdFiles = Directory.GetFiles(swiftModuleDir, "*.tbd")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();
            if (tbdFiles.Length > 0)
            {
                if (tbdFiles.Length > 1)
                {
                    // Ambiguous input: multiple TBDs, only one is consumed.
                    InputResolutionReport.RecordDegradation(
                        InputResolutionCategory.Tbd,
                        $"{tbdFiles.Length} TBD files present; deterministically picked '{tbdFiles[0]}'.");
                }
                else
                {
                    InputResolutionReport.RecordInfo(
                        InputResolutionCategory.Tbd, $"Found TBD '{tbdFiles[0]}'.");
                }
                logger.LogInformation("Found TBD: {Path}", tbdFiles[0]);
                return tbdFiles[0];
            }

            Directory.CreateDirectory(outputDirectory);
            var tbdOutputPath = Path.Combine(outputDirectory, $"{moduleName}.tbd");

            // tapi stubify only accepts Mach-O dynamic libraries. Static Swift
            // frameworks ship an `ar archive` binary
            // alongside a complete .swiftmodule; rarer distributions ship
            // bare Mach-O object files or universal-of-static binaries. The
            // binding generator only consumes mangled symbols from the TBD,
            // so we synthesize a minimal JSON TBD from `nm -gU` rather than
            // failing here. `nm` reads all three of those shapes.
            if (RequiresTbdSynthesis(dylibPath, commandRunner))
            {
                logger.LogInformation("Non-dylib binary detected. Synthesizing TBD from nm symbols...");
                SynthesizeTbdFromStaticArchive(dylibPath, moduleName, tbdOutputPath, commandRunner);
                return tbdOutputPath;
            }

            // Generate via tapi
            logger.LogInformation("No TBD found. Generating from dylib...");

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

        private static bool RequiresTbdSynthesis(string binaryPath, ICommandRunner commandRunner)
        {
            var (exitCode, stdout, _) = commandRunner.Run(
                "file",
                $"\"{binaryPath}\"",
                timeoutMs: 10000);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                // `file` couldn't classify the binary; let tapi stubify try
                // and surface its own error if it also rejects the input.
                return false;
            }
            // tapi stubify accepts only Mach-O dylibs. After we've confirmed
            // Swift evidence upstream, anything that isn't a dylib is a
            // non-dylib static distribution we need to synthesize a TBD for —
            // `current ar archive`, `Mach-O 64-bit object`,
            // universal binaries wrapping either, etc. Universal Mach-O dylibs
            // still report "dynamically linked shared library" inside the
            // slice listing, so the negative match is safe.
            return !stdout.Contains("dynamically linked shared library", StringComparison.OrdinalIgnoreCase);
        }

        private static void SynthesizeTbdFromStaticArchive(
            string archivePath,
            string moduleName,
            string tbdOutputPath,
            ICommandRunner commandRunner)
        {
            var (exitCode, stdout, stderr) = commandRunner.Run(
                "nm",
                $"-gU \"{archivePath}\"",
                timeoutMs: 60000);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"SWIFTBIND104: Failed to enumerate symbols from static archive '{archivePath}': {stderr}. " +
                    "Ensure Xcode command-line tools are installed: xcode-select --install");
            }

            var symbols = NativeSymbolProbe.ParseNmSymbols(stdout);

            // Minimal JSON TBD v5 — only the fields the in-process parser reads:
            //   tapi_tbd_version, main_library.{install_names, target_info,
            //   exported_symbols[].text.global[]}.
            // The binding generator demangles symbols by name; install-name and
            // target tuples are not consulted by downstream code paths.
            var sb = new System.Text.StringBuilder();
            sb.Append('{').Append('\n');
            sb.Append("  \"tapi_tbd_version\": 5,\n");
            sb.Append("  \"main_library\": {\n");
            sb.Append("    \"target_info\": [{ \"target\": \"arm64-ios\" }],\n");
            sb.Append("    \"install_names\": [{ \"name\": \"@rpath/")
              .Append(JsonEscape(moduleName))
              .Append("\" }],\n");
            sb.Append("    \"exported_symbols\": [{\n");
            sb.Append("      \"text\": {\n");
            sb.Append("        \"global\": [");
            var first = true;
            foreach (var sym in symbols)
            {
                if (!first) sb.Append(',');
                sb.Append('\n').Append("          \"").Append(JsonEscape(sym)).Append('"');
                first = false;
            }
            sb.Append(first ? "]" : "\n        ]").Append('\n');
            sb.Append("      }\n");
            sb.Append("    }]\n");
            sb.Append("  }\n");
            sb.Append("}\n");
            File.WriteAllText(tbdOutputPath, sb.ToString());
        }

        private static string JsonEscape(string value)
        {
            // Mangled Swift symbol names contain only ASCII alphanumerics, `$`,
            // and `_`, so plain quote/backslash escaping is sufficient. We
            // still guard for safety against module-name surprises.
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
