// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace BindingsGeneration
{
    /// <summary>
    /// Command-line tool for generating C# bindings from Swift ABI files.
    /// </summary>
    public class BindingsGenerator
    {
        private const string DefaultConfigFileName = ".swiftbindings.json";

        /// <summary>
        /// Main entry point of the bindings generator tool.
        /// </summary>
        public static int Main(string[] args)
        {
            Option<string> swiftAbiOption = new(aliases: new[] { "-a", "--swiftabi" }, "Path to the Swift ABI file.");
            Option<string> dylibOption = new(aliases: new[] { "-d", "--dylib" }, "Path to the dynamic library.");
            Option<string> tbdOption = new(aliases: new[] { "-t", "--tbd" }, "Path to the TBD file.");
            Option<string> outputDirectoryOption = new(aliases: new[] { "-o", "--output" }, "Output directory for generated bindings.");
            Option<string> xcframeworkOption = new(
                aliases: new[] { "--xcframework" },
                description: "Path to an xcframework directory. Automatically resolves ABI JSON, dylib, TBD, and swiftinterface. " +
                             "Mutually exclusive with -a, -d, -t.");
            Option<string> platformTargetOption = new(
                aliases: new[] { "--platform-target" },
                description: "Platform target for xcframework slice selection: 'simulator' (default) or 'device'. " +
                             "Only used with --xcframework.",
                getDefaultValue: () => "simulator");
            Option<string> libraryNameOption = new(
                aliases: new[] { "-l", "--library-name" },
                description: "Runtime library name for DllImport. If not specified, uses the dylib path. " +
                             "Note: If the name starts with '@' (e.g., @rpath/...), escape it with backslash: '\\@rpath/Nuke.framework/Nuke'");
            Option<string> asyncLibraryOption = new(
                aliases: new[] { "--async-library" },
                description: "Library name for async wrapper functions. If not specified, uses the module library. " +
                             "Typically 'SwiftBindings' when using a separate wrapper library.");
            Option<string> namespacePatternOption = new(
                aliases: new[] { "--namespace-pattern" },
                description: "C# namespace pattern for generated modules and types. Supports {Module} and {Framework}. Default: Swift.{Module}");
            Option<string> swiftInterfaceOption = new(
                aliases: new[] { "-s", "--swiftinterface" },
                description: "Path to the .swiftinterface file. Used to detect @inlinable internal members " +
                             "that can't be distinguished from public in the ABI JSON alone.");
            Option<string> symbolGraphOption = new(
                aliases: new[] { "--symbolgraph" },
                description: "Path to symbol graph JSON file or directory. Used to extract Swift doc comments for C# XML doc comment generation.");
            Option<bool> noDocsOption = new(
                aliases: new[] { "--no-docs" },
                description: "Disable automatic symbol graph extraction for doc comment generation. " +
                             "Does not affect explicit --symbolgraph paths.",
                getDefaultValue: () => false);
            Option<string> bridgeHintsOption = new(
                aliases: new[] { "--bridge-hints" },
                description: "Path to bridge hints JSON file for customizing SwiftUI bridge generation.");
            Option<string> configOption = new(
                aliases: new[] { "--config" },
                description: $"Path to config JSON file. Default: {DefaultConfigFileName} in current directory.");
            Option<bool> sdkModeOption = new(
                aliases: new[] { "--sdk-mode" },
                description: "SDK mode: skips .csproj emission (used when the SDK IS the project system).",
                getDefaultValue: () => false);
            Option<string?> packageIdOption = new(
                aliases: new[] { "--package-id" },
                description: "Package ID for NuGet packaging. Overrides the default '{Module}.Swift.iOS'.");
            Option<string> wrapperArchitecturesOption = new(
                aliases: new[] { "--wrapper-architectures" },
                description: "Wrapper compilation scope: 'simulator' (default), 'device', or 'all' (both slices).",
                getDefaultValue: () => "simulator");
            Option<string[]> frameworkDependencyOption = new(
                aliases: new[] { "--framework-dependency" },
                description: "Path to a dependency xcframework. Repeatable. Adds -F search paths for wrapper compilation " +
                             "and PackageReference entries in the emitted .csproj. Requires --xcframework.")
            { AllowMultipleArgumentsPerToken = false };
            Option<bool> noAutoDetectOption = new(
                aliases: new[] { "--no-auto-detect" },
                description: "Disable automatic dependency detection from binary linkage.",
                getDefaultValue: () => false);
            Option<int> verboseOption = new(
                aliases: new[] { "-v", "--verbose" },
                description: "Verbosity level. 0 = No logging, 1 = General information, 2 = Debugging information. (default: 1)",
                getDefaultValue: () => 1);
            Option<bool> helpOption = new(aliases: new[] { "-h", "--help" }, "Display a help message.");

            RootCommand rootCommand = new(description: "Swift bindings generator.")
            {
                swiftAbiOption,
                dylibOption,
                tbdOption,
                outputDirectoryOption,
                xcframeworkOption,
                platformTargetOption,
                libraryNameOption,
                asyncLibraryOption,
                swiftInterfaceOption,
                symbolGraphOption,
                noDocsOption,
                bridgeHintsOption,
                namespacePatternOption,
                sdkModeOption,
                packageIdOption,
                wrapperArchitecturesOption,
                frameworkDependencyOption,
                noAutoDetectOption,
                configOption,
                verboseOption,
                helpOption,
            };
            rootCommand.SetHandler((InvocationContext context) =>
            {
                var parseResult = context.ParseResult;
                var swiftAbiPath = parseResult.GetValueForOption(swiftAbiOption);
                var dylibPath = parseResult.GetValueForOption(dylibOption);
                var tbdPath = parseResult.GetValueForOption(tbdOption);
                var outputDirectory = parseResult.GetValueForOption(outputDirectoryOption);
                var xcframeworkPath = parseResult.GetValueForOption(xcframeworkOption);
                var platformTargetStr = parseResult.GetValueForOption(platformTargetOption);
                var libraryName = parseResult.GetValueForOption(libraryNameOption);
                var asyncLibrary = parseResult.GetValueForOption(asyncLibraryOption);
                var swiftInterface = parseResult.GetValueForOption(swiftInterfaceOption);
                var symbolGraph = parseResult.GetValueForOption(symbolGraphOption);
                var noDocs = parseResult.GetValueForOption(noDocsOption);
                var bridgeHints = parseResult.GetValueForOption(bridgeHintsOption);
                var namespacePattern = parseResult.GetValueForOption(namespacePatternOption);
                var sdkMode = parseResult.GetValueForOption(sdkModeOption);
                var packageId = parseResult.GetValueForOption(packageIdOption);
                var wrapperArchitectures = parseResult.GetValueForOption(wrapperArchitecturesOption);
                var frameworkDependencies = parseResult.GetValueForOption(frameworkDependencyOption);
                var noAutoDetect = parseResult.GetValueForOption(noAutoDetectOption);
                var configPath = parseResult.GetValueForOption(configOption);
                var verbose = parseResult.GetValueForOption(verboseOption);
                var help = parseResult.GetValueForOption(helpOption);

                if (help)
                {
                    Console.WriteLine("Usage:");
                    Console.WriteLine("  --xcframework        Path to xcframework directory. Replaces -a, -d, -t.");
                    Console.WriteLine("  --platform-target    Platform target: 'simulator' (default) or 'device'. Used with --xcframework.");
                    Console.WriteLine("  -a, --swiftabi       Path to the Swift ABI file. Required if --xcframework not used.");
                    Console.WriteLine("  -d, --dylib          Path to the dynamic library. Required if --xcframework not used.");
                    Console.WriteLine("  -t, --tbd            Path to the TBD file. Required if --xcframework not used.");
                    Console.WriteLine("  -o, --output         Required. Output directory for generated bindings.");
                    Console.WriteLine("  -l, --library-name   Optional. Runtime library name for DllImport. Escape @ with backslash: '\\@rpath/...'");
                    Console.WriteLine("  --async-library      Optional. Library name for async wrapper functions. Default uses module library.");
                    Console.WriteLine("  -s, --swiftinterface Optional. Path to .swiftinterface file for internal member detection.");
                    Console.WriteLine("  --symbolgraph        Optional. Path to symbol graph JSON file or directory for doc comments.");
                    Console.WriteLine("  --no-docs            Optional. Disable automatic symbol graph extraction. Does not affect explicit --symbolgraph.");
                    Console.WriteLine("  --bridge-hints       Optional. Path to bridge hints JSON file for customizing SwiftUI bridge generation.");
                    Console.WriteLine($"  --namespace-pattern  Optional. Namespace pattern using {{Module}} and {{Framework}}. Default: {NamespacePatternResolver.DefaultPattern}");
                    Console.WriteLine("  --sdk-mode           Optional. SDK mode: skips .csproj emission (used when the SDK IS the project system).");
                    Console.WriteLine("  --package-id         Optional. Package ID for NuGet packaging. Default: '{Module}.Swift.iOS'.");
                    Console.WriteLine("  --wrapper-architectures  Optional. Wrapper compilation scope: 'simulator' (default), 'device', or 'all'.");
                    Console.WriteLine("  --framework-dependency   Optional. Repeatable. Path to dependency xcframework for -F search paths. Requires --xcframework.");
                    Console.WriteLine("  --no-auto-detect     Optional. Disable automatic dependency detection from binary linkage.");
                    Console.WriteLine($"  --config             Optional. Path to config file. Default: {DefaultConfigFileName}");
                    Console.WriteLine("  -v, --verbose        Verbosity level. 0 = No logging, 1 = General information, 2 = Debugging information. (default: 1)");
                    return;
                }

                ILoggerFactory loggerFactory = CreateLoggerFactory(verbose);
                ILogger logger = loggerFactory.CreateLogger<BindingsGenerator>();

                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    logger.LogError("Error: Output directory (-o) is required.");
                    context.ExitCode = 1;
                    return;
                }

                // Validate mutual exclusivity: --xcframework vs -a/-d/-t
                var hasXcframework = !string.IsNullOrWhiteSpace(xcframeworkPath);
                var hasManualInputs = !string.IsNullOrWhiteSpace(swiftAbiPath) ||
                                      !string.IsNullOrWhiteSpace(dylibPath) ||
                                      !string.IsNullOrWhiteSpace(tbdPath);

                if (hasXcframework && hasManualInputs)
                {
                    logger.LogError("Error: --xcframework cannot be combined with -a, -d, or -t. Use one mode or the other.");
                    context.ExitCode = 1;
                    return;
                }

                if (!hasXcframework && !hasManualInputs)
                {
                    logger.LogError("Error: Either --xcframework or all of -a, -d, -t must be provided.");
                    context.ExitCode = 1;
                    return;
                }

                // Resolve xcframework mode
                XCFrameworkResolution? resolution = null;
                var shouldCompileWrapper = false;
                var asyncLibraryAutoWired = false;
                var platformTarget = XCFrameworkPlatformTarget.Simulator;

                if (hasXcframework)
                {
                    Directory.CreateDirectory(outputDirectory);
                    switch (platformTargetStr?.ToLowerInvariant())
                    {
                        case "simulator":
                        case null:
                            platformTarget = XCFrameworkPlatformTarget.Simulator;
                            break;
                        case "device":
                            platformTarget = XCFrameworkPlatformTarget.Device;
                            break;
                        default:
                            logger.LogError("Error: Invalid --platform-target '{Value}'. Valid values: 'simulator', 'device'.", platformTargetStr);
                            context.ExitCode = 1;
                            return;
                    }

                    try
                    {
                        resolution = XCFrameworkResolver.Resolve(
                            xcframeworkPath!, outputDirectory, platformTarget, logger);
                        swiftAbiPath = resolution.AbiJsonPath;
                        dylibPath = resolution.DylibPath;
                        tbdPath = resolution.TbdPath;
                        swiftInterface ??= resolution.SwiftInterfacePath;
                        libraryName ??= resolution.ModuleName;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Error resolving xcframework: {Message}", ex.Message);
                        context.ExitCode = 1;
                        return;
                    }

                    // Gate wrapper compilation:
                    // - simulator/all: needs a simulator slice (always present as primary when --platform-target simulator)
                    // - device: can compile with just a device slice
                    // - If the primary resolution is device-only and architectures is 'simulator', skip
                    var wrapperArchEarly = wrapperArchitectures?.ToLowerInvariant() ?? "simulator";
                    shouldCompileWrapper = ShouldCompileWrapper(resolution.IsSimulatorSlice, wrapperArchEarly);
                    if (!shouldCompileWrapper)
                    {
                        logger.LogInformation(
                            "Swift wrapper compilation requires a simulator slice or --wrapper-architectures device/all. " +
                            "Pass --async-library manually for device-only builds without wrapper compilation.");
                    }

                    // Auto-set --async-library whenever wrapper will be compiled
                    if (shouldCompileWrapper && string.IsNullOrWhiteSpace(asyncLibrary))
                    {
                        var wrapperModuleName = $"{resolution.ModuleName}SwiftBindings";
                        asyncLibrary = wrapperModuleName;
                        asyncLibraryAutoWired = true;
                        logger.LogInformation("Auto-setting --async-library to '{Module}'.", wrapperModuleName);
                    }
                }

                // Auto-detect dependencies from binary linkage (xcframework mode only)
                List<FrameworkDependencyInfo>? autoDetectedDeps = null;
                DependencyAnalysisResult? analysisResult = null;

                if (hasXcframework && !noAutoDetect)
                {
                    analysisResult = BinaryDependencyAnalyzer.Analyze(
                        resolution!.DylibPath, xcframeworkPath!, resolution.ModuleName,
                        platformTarget,
                        wrapperArchitectures?.ToLowerInvariant() ?? "simulator",
                        logger);
                    if (analysisResult != null)
                    {
                        autoDetectedDeps = analysisResult.ResolvedDependencies;
                        foreach (var dep in autoDetectedDeps)
                            logger.LogInformation("Auto-detected dependency: {Module} ({Path})",
                                dep.ModuleName, dep.XCFrameworkPath);
                        foreach (var unresolved in analysisResult.UnresolvedDependencies)
                        {
                            if (unresolved.UnresolvedReason == "missing-slice")
                            {
                                logger.LogWarning(
                                    "SWIFTBIND060: Detected dependency '{Name}' but its xcframework " +
                                    "lacks the required platform slice. " +
                                    "Use --framework-dependency to specify a complete xcframework.",
                                    unresolved.FrameworkName);
                            }
                            else
                            {
                                logger.LogWarning(
                                    "SWIFTBIND060: Detected dependency '{Name}' but no matching " +
                                    "{Name}.xcframework found. " +
                                    "Use --framework-dependency to specify its location.",
                                    unresolved.FrameworkName, unresolved.FrameworkName);
                            }
                        }
                    }
                }

                // Validate and resolve --framework-dependency options
                var hasFrameworkDeps = frameworkDependencies != null && frameworkDependencies.Length > 0;
                List<FrameworkDependencyInfo>? resolvedDependencies = null;

                if (hasFrameworkDeps)
                {
                    if (!hasXcframework)
                    {
                        logger.LogError("Error: --framework-dependency requires --xcframework mode.");
                        context.ExitCode = 1;
                        return;
                    }

                    resolvedDependencies = ResolveFrameworkDependencies(
                        frameworkDependencies!, resolution!, xcframeworkPath!,
                        wrapperArchitectures?.ToLowerInvariant() ?? "simulator",
                        platformTarget, logger);
                    if (resolvedDependencies == null)
                    {
                        context.ExitCode = 1;
                        return; // Validation failed — error already logged
                    }
                }

                // Merge auto-detected deps with manual deps (manual takes precedence)
                if (autoDetectedDeps?.Count > 0)
                {
                    var manualModules = new HashSet<string>(
                        resolvedDependencies?.Select(d => d.ModuleName) ?? Enumerable.Empty<string>(),
                        StringComparer.Ordinal);
                    foreach (var autoDep in autoDetectedDeps)
                    {
                        if (manualModules.Contains(autoDep.ModuleName))
                        {
                            logger.LogInformation(
                                "Skipping auto-detected '{Module}' — overridden by manual --framework-dependency.",
                                autoDep.ModuleName);
                        }
                        else
                        {
                            resolvedDependencies ??= new List<FrameworkDependencyInfo>();
                            resolvedDependencies.Add(autoDep);
                            logger.LogInformation("Using auto-detected dependency: {Module}", autoDep.ModuleName);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(swiftAbiPath) || !File.Exists(swiftAbiPath))
                {
                    logger.LogError("Error: Valid Swift ABI file is required.");
                    context.ExitCode = 1;
                    return;
                }

                if (string.IsNullOrWhiteSpace(dylibPath) || !File.Exists(dylibPath))
                {
                    logger.LogError("Error: Valid dynamic library is required.");
                    context.ExitCode = 1;
                    return;
                }

                if (string.IsNullOrWhiteSpace(tbdPath) || !File.Exists(tbdPath))
                {
                    logger.LogError("Error: Valid TBD file is required.");
                    context.ExitCode = 1;
                    return;
                }

                if (!Directory.Exists(outputDirectory))
                {
                    logger.LogError("Error: Valid output directory is required.");
                    context.ExitCode = 1;
                    return;
                }

                // Use the provided library name, or fall back to the dylib path
                var runtimeLibraryName = string.IsNullOrWhiteSpace(libraryName) ? dylibPath : libraryName;
                var effectiveNamespacePattern = ResolveNamespacePattern(namespacePattern, configPath, logger);

                // Auto-extract symbol graph for doc comments (xcframework mode only)
                symbolGraph = ResolveSymbolGraphPath(symbolGraph, noDocs, resolution, outputDirectory, logger);

                GenerateBindings(swiftAbiPath, dylibPath, tbdPath, outputDirectory, runtimeLibraryName, asyncLibrary, swiftInterface, symbolGraph, bridgeHints, effectiveNamespacePattern, logger, loggerFactory, out var internalTypeNames);

                // Validate --wrapper-architectures
                var wrapperArchNormalized = wrapperArchitectures?.ToLowerInvariant() ?? "simulator";
                if (wrapperArchNormalized != "simulator" && wrapperArchNormalized != "device" && wrapperArchNormalized != "all")
                {
                    logger.LogError("Error: Invalid --wrapper-architectures '{Value}'. Valid values: 'simulator', 'device', 'all'.", wrapperArchitectures);
                    context.ExitCode = 1;
                    return;
                }

                // Compile Swift wrapper (xcframework mode only)
                SwiftWrapperCompilationResult? compilationResult = null;
                if (shouldCompileWrapper && resolution != null)
                {
                    // Collect additional -F search paths from framework dependencies
                    var simDepPaths = resolvedDependencies?
                        .Where(d => d.SimulatorFrameworkSearchPath != null)
                        .Select(d => d.SimulatorFrameworkSearchPath!)
                        .ToList();
                    var deviceDepPaths = resolvedDependencies?
                        .Where(d => d.DeviceFrameworkSearchPath != null)
                        .Select(d => d.DeviceFrameworkSearchPath!)
                        .ToList();

                    Exception? compilationException = null;

                    try
                    {
                        if (wrapperArchNormalized == "all")
                        {
                            // Multi-arch: resolve both slices, compile wrapper for both
                            var (simResolution, deviceResolution) = XCFrameworkResolver.ResolveAll(
                                xcframeworkPath!, outputDirectory, logger);

                            if (deviceResolution == null)
                            {
                                logger.LogWarning(
                                    "Source xcframework has no device slice; wrapper will contain simulator slice only.");
                            }

                            compilationResult = SwiftWrapperCompiler.CompileAll(
                                outputDirectory, resolution.ModuleName,
                                simResolution, deviceResolution, logger,
                                internalTypeNames: internalTypeNames,
                                simAdditionalSearchPaths: simDepPaths,
                                deviceAdditionalSearchPaths: deviceDepPaths);
                        }
                        else if (wrapperArchNormalized == "device")
                        {
                            // Device-only: resolve device slice and compile for iphoneos
                            XCFrameworkResolution deviceOnlyResolution;
                            try
                            {
                                deviceOnlyResolution = XCFrameworkResolver.Resolve(
                                    xcframeworkPath!, outputDirectory,
                                    XCFrameworkPlatformTarget.Device, logger);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError("Cannot compile device wrapper: {Message}", ex.Message);
                                context.ExitCode = 1;
                                return;
                            }

                            compilationResult = SwiftWrapperCompiler.CompileSlice(
                                outputDirectory, resolution.ModuleName,
                                deviceOnlyResolution.FrameworkSearchPath,
                                deviceOnlyResolution.DylibPath,
                                "device", "iphoneos", logger,
                                internalTypeNames: internalTypeNames,
                                additionalFrameworkSearchPaths: deviceDepPaths);
                        }
                        else
                        {
                            // Simulator-only (default)
                            compilationResult = SwiftWrapperCompiler.Compile(
                                outputDirectory, resolution.ModuleName,
                                resolution.FrameworkSearchPath, resolution.DylibPath, logger,
                                internalTypeNames: internalTypeNames,
                                additionalFrameworkSearchPaths: simDepPaths);
                        }
                    }
                    catch (Exception ex)
                    {
                        compilationException = ex;
                    }

                    var rawOutcome = SwiftWrapperCompiler.EvaluateResult(
                        compilationResult, asyncLibraryAutoWired, compilationException);
                    var (outcomeExitCode, diagnosticCode, outcomeMessage) =
                        HandleWrapperCompilationOutcome(rawOutcome, sdkMode, compilationException, compilationResult);

                    if (outcomeExitCode != 0)
                    {
                        logger.LogError("{Message}", outcomeMessage);
                        context.ExitCode = outcomeExitCode;
                        return;
                    }
                    else if (diagnosticCode == "SWIFTBIND050")
                    {
                        logger.LogWarning("{Message}", outcomeMessage);
                    }
                    else if (rawOutcome == WrapperCompilationOutcome.Warning)
                    {
                        logger.LogWarning("{Message}", outcomeMessage);
                    }
                }

                // Emit binding project files (xcframework mode only)
                if (hasXcframework && resolution != null)
                {
                    try
                    {
                        var metadata = XCFrameworkMetadataExtractor.Extract(
                            resolution.DylibPath, resolution.XCFrameworkPath,
                            resolution.ModuleName, logger);

                        var wrapperXcfwPath = compilationResult?.XCFrameworkPath;
                        var hasWrapperXcfw = wrapperXcfwPath != null && Directory.Exists(wrapperXcfwPath);
                        var effectivePackageId = packageId ?? $"{resolution.ModuleName}.Swift.iOS";
                        var wrapperModuleName = $"{resolution.ModuleName}SwiftBindings";

                        // Always emit metadata props (used by SDK and standalone)
                        XCFrameworkMetadataExtractor.EmitMetadataProps(
                            metadata, outputDirectory, hasWrapperXcfw,
                            wrapperModuleName,
                            compilationResult?.SliceCount ?? 0, logger);

                        // Only emit .csproj in non-SDK mode
                        if (!sdkMode)
                        {
                            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                            {
                                OutputDirectory = outputDirectory,
                                ModuleName = resolution.ModuleName,
                                Metadata = metadata,
                                SourceXCFrameworkPath = resolution.XCFrameworkPath,
                                WrapperXCFrameworkPath = hasWrapperXcfw ? wrapperXcfwPath : null,
                                Dependencies = resolvedDependencies
                            }, logger);
                        }

                        ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                        {
                            OutputDirectory = outputDirectory,
                            ModuleName = resolution.ModuleName,
                            PackageId = effectivePackageId,
                            EffectiveMinimumOSVersion = metadata.EffectiveMinimumOSVersion,
                            HasWrapperXCFramework = hasWrapperXcfw
                        }, logger);

                        XCFrameworkMetadataExtractor.EmitMetadataJson(metadata, outputDirectory, logger);

                        // Emit dependency manifest (always in xcframework mode)
                        DependencyManifestEmitter.Emit(
                            outputDirectory,
                            resolution.ModuleName,
                            resolution.XCFrameworkPath,
                            resolution.DylibPath,
                            analysisResult,
                            resolvedDependencies,
                            frameworkDependencies,
                            logger);

                        logger.LogInformation("Binding project emitted successfully.");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Failed to emit binding project: {Message}", ex.Message);
                        context.ExitCode = 1;
                        return;
                    }
                }
            });

            return rootCommand.Invoke(args);
        }

        /// <summary>
        /// Generates C# bindings from Swift ABI files.
        /// </summary>
        /// <param name="swiftAbiPath">Path to the Swift ABI file.</param>
        /// <param name="dylibPath">Path to the dynamic library (used for metadata extraction).</param>
        /// <param name="tbdPath">Path to the TBD file.</param>
        /// <param name="outputDirectory">Output directory for generated bindings.</param>
        /// <param name="runtimeLibraryName">Library name for DllImport in generated code.</param>
        /// <param name="asyncLibraryName">Library name for async wrapper functions. If null, uses module library.</param>
        /// <param name="namespacePattern">Namespace pattern for generated modules and types.</param>
        /// <param name="logger">ILogger instance.</param>
        /// <param name="loggerFactory">ILoggerFactory instance.</param>
        public static void GenerateBindings(string swiftAbiPath, string dylibPath, string tbdPath, string outputDirectory, string runtimeLibraryName, string? asyncLibraryName, string? swiftInterfacePath, string? symbolGraphPath, string? bridgeHintsPath, string namespacePattern, ILogger logger, ILoggerFactory loggerFactory)
        {
            GenerateBindings(swiftAbiPath, dylibPath, tbdPath, outputDirectory, runtimeLibraryName, asyncLibraryName, swiftInterfacePath, symbolGraphPath, bridgeHintsPath, namespacePattern, logger, loggerFactory, out _);
        }

        private static void GenerateBindings(string swiftAbiPath, string dylibPath, string tbdPath, string outputDirectory, string runtimeLibraryName, string? asyncLibraryName, string? swiftInterfacePath, string? symbolGraphPath, string? bridgeHintsPath, string namespacePattern, ILogger logger, ILoggerFactory loggerFactory, out HashSet<string>? internalTypeNames)
        {
            internalTypeNames = null;
            var typeDatabase = new TypeDatabase();
            typeDatabase.AsyncLibraryName = asyncLibraryName;
            string[] moduleDatabases = { "FoundationDatabase.xml", "SwiftDatabase.xml", "CoreGraphicsDatabase.xml", "DispatchDatabase.xml", "AppKitDatabase.xml", "CoreImageDatabase.xml", "UIKitDatabase.xml", "SwiftUIDatabase.xml", "AVFoundationDatabase.xml", "CoreTextDatabase.xml" };
            foreach (var database in moduleDatabases)
            {
                typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", database)).Wait();
            }

            logger.LogInformation("Starting bindings generation for {SwiftAbiPath}...", swiftAbiPath);
            logger.LogInformation("Runtime library name: {LibraryName}", runtimeLibraryName);

            // Parse the TBD file
            Demangling.DemanglingResults demangledTbdFile = Demangling.DemanglingResults.FromTbd(tbdPath, loggerFactory);

            // Parse swiftinterface for internal member detection and parameter names (supplementary data)
            HashSet<string>? internalMemberKeys = null;
            Dictionary<string, List<string>>? parameterNames = null;
            Dictionary<string, string>? typedThrowsErrors = null;
            Dictionary<string, List<string?>>? enumCaseLabels = null;
            if (!string.IsNullOrWhiteSpace(swiftInterfacePath) && File.Exists(swiftInterfacePath))
            {
                internalMemberKeys = SwiftInterfaceAccessParser.GetInternalMembers(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} internal member keys from swiftinterface", internalMemberKeys.Count);
                parameterNames = SwiftInterfaceAccessParser.GetParameterNames(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} parameter name entries from swiftinterface", parameterNames.Count);
                typedThrowsErrors = SwiftInterfaceAccessParser.GetTypedThrowsErrors(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} typed throws entries from swiftinterface", typedThrowsErrors.Count);
                enumCaseLabels = SwiftInterfaceAccessParser.GetEnumCaseLabels(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} enum case label entries from swiftinterface", enumCaseLabels.Count);
            }

            // Parse symbol graph for doc comments (supplementary data)
            Dictionary<string, DocComment>? docComments = null;
            if (!string.IsNullOrWhiteSpace(symbolGraphPath))
            {
                if (File.Exists(symbolGraphPath) || Directory.Exists(symbolGraphPath))
                {
                    docComments = SymbolGraphDocParser.ParseSymbolGraphs(symbolGraphPath);
                    logger.LogInformation("Loaded {Count} doc comments from symbol graph", docComments.Count);
                }
                else
                {
                    logger.LogWarning("Symbol graph path not found: {Path}. Doc comments will not be generated.", symbolGraphPath);
                }
            }

            // Initialize the Swift ABI parser
            var swiftParser = new SwiftABIParser(swiftAbiPath, typeDatabase, demangledTbdFile, loggerFactory.CreateLogger<SwiftABIParser>(), internalMemberKeys, parameterNames, docComments, typedThrowsErrors, enumCaseLabels);
            var moduleName = swiftParser.GetModuleName();
            var frameworkName = InferFrameworkName(dylibPath, moduleName);
            var namespaceResolver = new NamespacePatternResolver(namespacePattern, frameworkName);

            // Skip if the module has already been processed
            // Modules will have to be processed in topological order
            if (!typeDatabase.IsModuleProcessed(moduleName))
            {
                // Parse the Swift ABI file and generate declarations
                var (decl, moduleTypes) = swiftParser.ParseModule();
                decl.ExportedSymbols = demangledTbdFile.AllSymbols;
                internalTypeNames = CollectInternalTypeNames(decl);
                ReportCollector.Start(decl);

                // dylibPath is used for metadata extraction, runtimeLibraryName is used in generated DllImport
                var moduleProcessor = new ModuleProcessor(moduleName, dylibPath, runtimeLibraryName, moduleTypes, typeDatabase, loggerFactory.CreateLogger<ModuleProcessor>(), namespaceResolver);
                var moduleDatabase = moduleProcessor.FinalizeTypeProcessingAndCreateModuleDatabase().ModuleDatabase;
                typeDatabase.AddModuleDatabase(moduleDatabase);

                logger.LogDebug("Parsed Swift ABI file successfully.");

                // Emit the C# bindings
                var stringEmitter = new StringEmitter(outputDirectory, typeDatabase, loggerFactory, namespaceResolver, bridgeHintsPath);
                stringEmitter.EmitModule(decl);

                var report = ReportCollector.Complete();
                if (report != null)
                {
                    ReportEmitter.Emit(report, outputDirectory, logger);
                }
                ReportCollector.Reset();

                logger.LogInformation("Bindings generation completed for {SwiftAbiPath}.", swiftAbiPath);

            }
            else
                logger.LogWarning("Bindings generation already completed for {SwiftAbiPath}.", swiftAbiPath);

        }

        /// <summary>
        /// Determines whether wrapper compilation should proceed based on the resolved
        /// slice type and the requested wrapper architecture scope.
        /// </summary>
        /// <param name="isSimulatorSlice">True when the primary resolution is a simulator slice.</param>
        /// <param name="wrapperArchitectures">Normalized value of --wrapper-architectures (simulator/device/all).</param>
        internal static bool ShouldCompileWrapper(bool isSimulatorSlice, string wrapperArchitectures)
        {
            return isSimulatorSlice
                || wrapperArchitectures == "device"
                || wrapperArchitectures == "all";
        }

        /// <summary>
        /// Resolves the symbol graph path for doc comment generation.
        /// Priority: explicit --symbolgraph > --no-docs suppression > auto-extraction.
        /// </summary>
        /// <param name="explicitSymbolGraph">Explicit --symbolgraph path from CLI.</param>
        /// <param name="noDocs">True if --no-docs was passed.</param>
        /// <param name="resolution">XCFramework resolution (null in manual mode).</param>
        /// <param name="outputDirectory">Output directory for auto-extracted files.</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        internal static string? ResolveSymbolGraphPath(
            string? explicitSymbolGraph, bool noDocs,
            XCFrameworkResolution? resolution, string outputDirectory,
            ILogger logger, ICommandRunner? commandRunner = null)
        {
            // 1. Explicit --symbolgraph always wins
            if (!string.IsNullOrWhiteSpace(explicitSymbolGraph))
                return explicitSymbolGraph;

            // 2. --no-docs disables auto-extraction
            if (noDocs)
                return null;

            // 3. Auto-extract (xcframework mode only)
            if (resolution == null)
                return null;

            return SymbolGraphExtractor.Extract(resolution, outputDirectory, logger, commandRunner);
        }

        /// <summary>
        /// Collects internal (non-public) type names from a parsed module.
        /// Returns both short names and module-qualified names for word-boundary matching.
        /// Short names that collide with public type names are removed to avoid over-stripping.
        /// </summary>
        internal static HashSet<string> CollectInternalTypeNames(ModuleDecl module)
        {
            var internalNames = new HashSet<string>();
            var publicNames = new HashSet<string>();
            CollectTypeNames(module.Types, internalNames, publicNames);
            // Remove short names that collide with public type names to avoid over-stripping
            internalNames.ExceptWith(publicNames);
            return internalNames;
        }

        private static void CollectTypeNames(IEnumerable<TypeDecl> types, HashSet<string> internalNames, HashSet<string> publicNames)
        {
            foreach (var t in types)
            {
                if (t.IsModuleInternal)
                {
                    // Always add qualified name (unique, no collision risk)
                    if (t.SwiftTypeName != null)
                        internalNames.Add(t.SwiftTypeName.ToString());
                    // Add short name tentatively (may be removed if it collides with a public name)
                    internalNames.Add(t.Name);
                }
                else
                {
                    publicNames.Add(t.Name);  // Track public short names for collision detection
                }
                CollectTypeNames(t.Types, internalNames, publicNames);  // Recurse ALL children
            }
        }

        /// <summary>
        /// Validates and resolves --framework-dependency paths into FrameworkDependencyInfo objects.
        /// Returns null if validation fails (error already logged).
        /// </summary>
        /// <param name="dependencyPaths">Paths from --framework-dependency CLI options.</param>
        /// <param name="primaryResolution">Primary xcframework resolution (for module name checks).</param>
        /// <param name="primaryXcframeworkPath">Path to the primary xcframework (for self-reference check).</param>
        /// <param name="wrapperArchitectures">Normalized wrapper-architectures value (simulator/device/all).</param>
        /// <param name="primaryPlatformTarget">The primary --platform-target (determines which slice to resolve first).</param>
        /// <param name="logger">Logger instance.</param>
        /// <param name="commandRunner">Optional command runner for testing.</param>
        internal static List<FrameworkDependencyInfo>? ResolveFrameworkDependencies(
            string[] dependencyPaths,
            XCFrameworkResolution primaryResolution,
            string primaryXcframeworkPath,
            string wrapperArchitectures,
            XCFrameworkPlatformTarget primaryPlatformTarget,
            ILogger logger,
            ICommandRunner? commandRunner = null)
        {
            var resolvedDeps = new List<FrameworkDependencyInfo>();
            var seenModules = new Dictionary<string, string>(StringComparer.Ordinal); // module → path

            foreach (var depPath in dependencyPaths)
            {
                // Validate path exists
                if (!Directory.Exists(depPath))
                {
                    logger.LogError("Error: --framework-dependency path does not exist: '{Path}'.", depPath);
                    return null;
                }

                // Validate it's an xcframework
                if (!depPath.EndsWith(".xcframework", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError("Error: --framework-dependency path is not an xcframework: '{Path}'.", depPath);
                    return null;
                }

                // Validate not the primary xcframework
                var depFullPath = Path.GetFullPath(depPath);
                var primaryFullPath = Path.GetFullPath(primaryXcframeworkPath);
                if (string.Equals(depFullPath, primaryFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError("Error: Primary xcframework cannot be listed as a dependency.");
                    return null;
                }

                // Resolve the primary-matching slice first (for module name + search path + dylib for version)
                // This matches the primary platform target so device-only workflows don't fail
                // when the dependency lacks a simulator slice.
                string? simSearchPath = null;
                string? deviceSearchPath = null;
                string moduleName;
                string depDylibPath;

                try
                {
                    var primaryDepResolution = XCFrameworkResolver.Resolve(
                        depPath, Path.GetTempPath(),
                        primaryPlatformTarget, logger, commandRunner);
                    moduleName = primaryDepResolution.ModuleName;
                    depDylibPath = primaryDepResolution.DylibPath;

                    if (primaryDepResolution.IsSimulatorSlice)
                        simSearchPath = primaryDepResolution.FrameworkSearchPath;
                    else
                        deviceSearchPath = primaryDepResolution.FrameworkSearchPath;
                }
                catch (SwiftModuleNotFoundException)
                {
                    // Attempt ObjC-only framework fallback — resolves search path + validates modulemap
                    var objcResolution = XCFrameworkResolver.ResolveObjCFramework(
                        depPath, primaryPlatformTarget, logger);

                    if (objcResolution == null)
                    {
                        // Not a valid ObjC framework (no modulemap) — treat as real error
                        logger.LogError(
                            "Error: Dependency '{Path}' has no Swift module and no ObjC module.modulemap. " +
                            "It may be a Swift framework without library evolution support.",
                            depPath);
                        return null;
                    }

                    logger.LogInformation(
                        "Dependency '{Name}' is ObjC-only — adding framework search path for module resolution.",
                        objcResolution.ModuleName);

                    // Map search path to correct sim/device bucket based on actual selected slice
                    string? simPath = null, devicePath = null;
                    if (objcResolution.IsSimulatorSlice)
                        simPath = objcResolution.FrameworkSearchPath;
                    else
                        devicePath = objcResolution.FrameworkSearchPath;

                    // Resolve opposite or required slice — mirrors Swift dep error logic.
                    // Derive oppositeTarget from actual resolved slice (not requested target)
                    // because SelectSlice can fall back to the other platform variant.
                    if (wrapperArchitectures == "all")
                    {
                        var oppositeTarget = objcResolution.IsSimulatorSlice
                            ? XCFrameworkPlatformTarget.Device
                            : XCFrameworkPlatformTarget.Simulator;
                        var oppositeResolution = XCFrameworkResolver.ResolveObjCFramework(
                            depPath, oppositeTarget, logger);
                        var expectSimulator = oppositeTarget == XCFrameworkPlatformTarget.Simulator;
                        if (oppositeResolution != null && oppositeResolution.IsSimulatorSlice == expectSimulator)
                        {
                            if (oppositeResolution.IsSimulatorSlice)
                                simPath = oppositeResolution.FrameworkSearchPath;
                            else
                                devicePath = oppositeResolution.FrameworkSearchPath;
                        }
                        else
                        {
                            logger.LogError(
                                "Error: ObjC dependency '{Path}' lacks required {Target} slice.",
                                depPath, oppositeTarget.ToString().ToLowerInvariant());
                            return null;
                        }
                    }
                    else if (wrapperArchitectures == "device" && simPath != null && devicePath == null)
                    {
                        // Primary resolved simulator but we need device
                        var deviceResolution = XCFrameworkResolver.ResolveObjCFramework(
                            depPath, XCFrameworkPlatformTarget.Device, logger);
                        if (deviceResolution != null && !deviceResolution.IsSimulatorSlice)
                            devicePath = deviceResolution.FrameworkSearchPath;
                        else
                        {
                            logger.LogError(
                                "Error: ObjC dependency '{Path}' lacks required device slice.",
                                depPath);
                            return null;
                        }
                    }
                    else if (wrapperArchitectures == "simulator" && devicePath != null && simPath == null)
                    {
                        // Primary resolved device but we need simulator
                        var simResolution = XCFrameworkResolver.ResolveObjCFramework(
                            depPath, XCFrameworkPlatformTarget.Simulator, logger);
                        if (simResolution != null && simResolution.IsSimulatorSlice)
                            simPath = simResolution.FrameworkSearchPath;
                        else
                        {
                            logger.LogError(
                                "Error: ObjC dependency '{Path}' lacks required simulator slice.",
                                depPath);
                            return null;
                        }
                    }

                    // Duplicate module check
                    if (seenModules.TryGetValue(objcResolution.ModuleName, out var existingObjCPath))
                    {
                        logger.LogError(
                            "Error: Duplicate dependency module '{Module}' from '{Path1}' and '{Path2}'.",
                            objcResolution.ModuleName, existingObjCPath, depPath);
                        return null;
                    }

                    // Primary module conflict check
                    if (string.Equals(objcResolution.ModuleName, primaryResolution.ModuleName,
                        StringComparison.Ordinal))
                    {
                        logger.LogError(
                            "Error: Primary module '{Module}' cannot be listed as a dependency.",
                            objcResolution.ModuleName);
                        return null;
                    }

                    seenModules[objcResolution.ModuleName] = depPath;
                    resolvedDeps.Add(new FrameworkDependencyInfo
                    {
                        XCFrameworkPath = depFullPath,
                        ModuleName = objcResolution.ModuleName,
                        SimulatorFrameworkSearchPath = simPath,
                        DeviceFrameworkSearchPath = devicePath,
                        IsObjCOnly = true
                    });
                    continue;
                }
                catch (Exception ex)
                {
                    logger.LogError("Error resolving dependency xcframework '{Path}': {Message}",
                        depPath, ex.Message);
                    return null;
                }

                // Check for duplicate module names
                if (seenModules.TryGetValue(moduleName, out var existingPath))
                {
                    logger.LogError(
                        "Error: Duplicate dependency module '{Module}' from '{Path1}' and '{Path2}'.",
                        moduleName, existingPath, depPath);
                    return null;
                }

                // Check primary module as dependency
                if (string.Equals(moduleName, primaryResolution.ModuleName, StringComparison.Ordinal))
                {
                    logger.LogError("Error: Primary module '{Module}' cannot be listed as a dependency.", moduleName);
                    return null;
                }

                seenModules[moduleName] = depPath;

                // Resolve the opposite slice if wrapper-architectures requires both
                if (wrapperArchitectures == "all")
                {
                    // Need both slices — resolve whichever the primary didn't give us
                    var oppositeTarget = primaryPlatformTarget == XCFrameworkPlatformTarget.Simulator
                        ? XCFrameworkPlatformTarget.Device
                        : XCFrameworkPlatformTarget.Simulator;
                    try
                    {
                        var oppositeResolution = XCFrameworkResolver.Resolve(
                            depPath, Path.GetTempPath(),
                            oppositeTarget, logger, commandRunner);

                        if (oppositeResolution.IsSimulatorSlice)
                            simSearchPath = oppositeResolution.FrameworkSearchPath;
                        else
                            deviceSearchPath = oppositeResolution.FrameworkSearchPath;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            "Error: Dependency xcframework '{Path}' lacks required {Target} slice: {Message}",
                            depPath, oppositeTarget.ToString().ToLowerInvariant(), ex.Message);
                        return null;
                    }
                }
                else if (wrapperArchitectures == "device" && simSearchPath != null && deviceSearchPath == null)
                {
                    // Primary resolved simulator but we need device for compilation
                    try
                    {
                        var deviceResolution = XCFrameworkResolver.Resolve(
                            depPath, Path.GetTempPath(),
                            XCFrameworkPlatformTarget.Device, logger, commandRunner);
                        deviceSearchPath = deviceResolution.FrameworkSearchPath;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            "Error: Dependency xcframework '{Path}' lacks required device slice: {Message}",
                            depPath, ex.Message);
                        return null;
                    }
                }
                else if (wrapperArchitectures == "simulator" && deviceSearchPath != null && simSearchPath == null)
                {
                    // Primary resolved device but we need simulator for compilation
                    try
                    {
                        var simResolution = XCFrameworkResolver.Resolve(
                            depPath, Path.GetTempPath(),
                            XCFrameworkPlatformTarget.Simulator, logger, commandRunner);
                        simSearchPath = simResolution.FrameworkSearchPath;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(
                            "Error: Dependency xcframework '{Path}' lacks required simulator slice: {Message}",
                            depPath, ex.Message);
                        return null;
                    }
                }

                // Extract version from dependency using the resolved dylib path
                string? packageVersion = null;
                try
                {
                    var metadata = XCFrameworkMetadataExtractor.Extract(
                        depDylibPath, depPath, moduleName, logger, commandRunner);
                    packageVersion = metadata.IsVersionPlaceholder ? null : metadata.PackageVersion;

                    if (metadata.IsVersionPlaceholder)
                    {
                        logger.LogWarning(
                            "SWIFTBIND021: Dependency '{Module}' has a placeholder version. " +
                            "The generated PackageReference will use '0.0.0'. Update before publishing.",
                            moduleName);
                    }
                }
                catch
                {
                    // Version extraction failure is non-fatal — use placeholder
                    logger.LogWarning(
                        "SWIFTBIND021: Could not extract version from dependency '{Module}'. " +
                        "Using placeholder '0.0.0'.", moduleName);
                }

                resolvedDeps.Add(new FrameworkDependencyInfo
                {
                    XCFrameworkPath = depFullPath,
                    ModuleName = moduleName,
                    PackageVersion = packageVersion,
                    SimulatorFrameworkSearchPath = simSearchPath,
                    DeviceFrameworkSearchPath = deviceSearchPath,
                    DylibPath = depDylibPath
                });
            }

            return resolvedDeps;
        }

        /// <summary>
        /// Evaluates wrapper compilation outcome with SDK-mode awareness.
        /// Returns exit code, optional diagnostic code, and message for logging.
        /// </summary>
        internal static (int exitCode, string? diagnosticCode, string message) HandleWrapperCompilationOutcome(
            WrapperCompilationOutcome rawOutcome, bool sdkMode,
            Exception? compilationException, SwiftWrapperCompilationResult? compilationResult)
        {
            var effective = SwiftWrapperCompiler.EffectiveOutcome(rawOutcome, sdkMode);

            if (effective == WrapperCompilationOutcome.Fatal)
            {
                var message = compilationException != null
                    ? $"Swift wrapper compilation failed: {compilationException.Message}. " +
                      "Generated C# references the wrapper library but no compiled wrapper exists."
                    : $"All Swift wrapper code was stripped as broken ({compilationResult?.StrippedBlockCount ?? 0} block(s)). " +
                      "Generated C# references the wrapper library but no compiled wrapper exists. " +
                      "Use --async-library explicitly or report this as a generator bug.";
                return (1, null, message);
            }

            if (rawOutcome == WrapperCompilationOutcome.Fatal && sdkMode)
            {
                // Downgraded from Fatal → Warning in SDK mode
                var message = compilationException != null
                    ? $"SWIFTBIND050: Swift wrapper compilation failed: {compilationException.Message}. " +
                      "C# bindings are still valid — wrapper-dependent methods will throw DllNotFoundException at runtime."
                    : $"SWIFTBIND050: All Swift wrapper code was stripped as broken ({compilationResult?.StrippedBlockCount ?? 0} block(s)). " +
                      "C# bindings are still valid — wrapper-dependent methods will throw DllNotFoundException at runtime.";
                return (0, "SWIFTBIND050", message);
            }

            if (effective == WrapperCompilationOutcome.Warning)
            {
                var message = compilationException != null
                    ? $"Swift wrapper compilation failed: {compilationException.Message}"
                    : $"All Swift wrapper code was stripped as broken ({compilationResult?.StrippedBlockCount ?? 0} block(s)).";
                return (0, null, message);
            }

            return (0, null, "");
        }

        private static string ResolveNamespacePattern(string? cliNamespacePattern, string? configPath, ILogger logger)
        {
            if (!string.IsNullOrWhiteSpace(cliNamespacePattern))
            {
                return cliNamespacePattern;
            }

            string resolvedConfigPath = string.IsNullOrWhiteSpace(configPath)
                ? Path.Combine(Environment.CurrentDirectory, DefaultConfigFileName)
                : configPath;

            if (!File.Exists(resolvedConfigPath))
            {
                return NamespacePatternResolver.DefaultPattern;
            }

            try
            {
                var configText = File.ReadAllText(resolvedConfigPath);
                var config = JObject.Parse(configText);
                var configNamespacePattern = config.Value<string>("namespacePattern");
                if (!string.IsNullOrWhiteSpace(configNamespacePattern))
                {
                    return configNamespacePattern;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse config file {ConfigPath}. Using default namespace pattern.", resolvedConfigPath);
            }

            return NamespacePatternResolver.DefaultPattern;
        }

        private static string InferFrameworkName(string dylibPath, string moduleName)
        {
            if (string.IsNullOrWhiteSpace(dylibPath))
            {
                return moduleName;
            }

            var pathSegments = dylibPath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in pathSegments)
            {
                if (segment.EndsWith(".framework", StringComparison.OrdinalIgnoreCase))
                {
                    var frameworkName = Path.GetFileNameWithoutExtension(segment);
                    if (!string.IsNullOrWhiteSpace(frameworkName))
                    {
                        return frameworkName;
                    }
                }
            }

            var fileName = Path.GetFileNameWithoutExtension(dylibPath);
            return string.IsNullOrWhiteSpace(fileName) ? moduleName : fileName;
        }

        /// <summary>
        /// Creates and configures a logger factory based on the verbosity level.
        /// </summary>
        /// <param name="verbosity">Verbosity level (0 = No logging, 1 = General information, 2 = Debugging information).</param>
        static ILoggerFactory CreateLoggerFactory(int verbosity)
        {
            return LoggerFactory.Create(builder =>
            {
                builder.AddConsole();

                builder.SetMinimumLevel(verbosity switch
                {
                    0 => LogLevel.None,  // No logging
                    1 => LogLevel.Information, // Info and above
                    2 => LogLevel.Debug,    // Debug and above
                    _ => throw new ArgumentOutOfRangeException(nameof(verbosity), "Invalid verbosity level.")
                });
            });
        }
    }
}
