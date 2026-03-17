// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using BindingsGeneration.ObjC;

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
            Option<string> platformOption = new(
                aliases: new[] { "--platform" },
                description: "Apple platform target: 'ios' (default), 'macos', 'tvos', 'maccatalyst'.",
                getDefaultValue: () => "ios");
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
                             "Only needed in manual mode (-a/-d/-t) when the wrapper is compiled as a separate dylib.");
            Option<string> namespacePatternOption = new(
                aliases: new[] { "--namespace-pattern" },
                description: "C# namespace pattern for generated modules and types. Supports {Module} and {Framework}. Default: {Module}");
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
            Option<string[]> moduleDatabaseOption = new(
                aliases: new[] { "--module-database" },
                description: "Path to a dependency module database XML file. Repeatable. " +
                             "Loads type records from previously generated modules for cross-module resolution.")
            { AllowMultipleArgumentsPerToken = false };
            Option<bool> noAutoDetectOption = new(
                aliases: new[] { "--no-auto-detect" },
                description: "Disable automatic dependency detection from binary linkage.",
                getDefaultValue: () => false);
            Option<bool> objcOption = new(
                aliases: new[] { "--objc" },
                description: "Force ObjC binding pipeline (auto-detected if not specified).",
                getDefaultValue: () => false);
            Option<bool> skipWrapperCompilationOption = new(
                aliases: new[] { "--skip-wrapper-compilation" },
                description: "Skip Swift wrapper compilation. Generates C# bindings and Swift wrapper source but does not compile the wrapper. " +
                             "Used by the SDK to defer wrapper compilation until after dependencies are built.",
                getDefaultValue: () => false);
            Option<bool> compileWrapperOnlyOption = new(
                aliases: new[] { "--compile-wrapper-only" },
                description: "Compile-wrapper-only mode: skips all parsing and C# generation, compiles existing .swift wrapper files " +
                             "from the output directory, and updates binding-metadata.props. Requires --xcframework and -o.",
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
                platformOption,
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
                moduleDatabaseOption,
                noAutoDetectOption,
                objcOption,
                skipWrapperCompilationOption,
                compileWrapperOnlyOption,
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
                var platformStr = parseResult.GetValueForOption(platformOption);
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
                var moduleDatabases = parseResult.GetValueForOption(moduleDatabaseOption);
                var noAutoDetect = parseResult.GetValueForOption(noAutoDetectOption);
                var objcForced = parseResult.GetValueForOption(objcOption);
                var skipWrapperCompilation = parseResult.GetValueForOption(skipWrapperCompilationOption);
                var compileWrapperOnly = parseResult.GetValueForOption(compileWrapperOnlyOption);
                var configPath = parseResult.GetValueForOption(configOption);
                var verbose = parseResult.GetValueForOption(verboseOption);
                var help = parseResult.GetValueForOption(helpOption);

                if (help)
                {
                    Console.WriteLine("Usage:");
                    Console.WriteLine("  --xcframework        Path to xcframework directory. Replaces -a, -d, -t.");
                    Console.WriteLine("  --platform           Apple platform: 'ios' (default), 'macos', 'tvos', 'maccatalyst'.");
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
                    Console.WriteLine("  --module-database    Optional. Repeatable. Path to dependency module database XML for cross-module type resolution.");
                    Console.WriteLine("  --no-auto-detect     Optional. Disable automatic dependency detection from binary linkage.");
                    Console.WriteLine("  --objc               Optional. Force ObjC binding pipeline (auto-detected if not specified).");
                    Console.WriteLine("  --skip-wrapper-compilation  Optional. Skip wrapper compilation (SDK defers to _CompileSwiftWrapper target).");
                    Console.WriteLine("  --compile-wrapper-only      Optional. Compile existing .swift wrapper files only (no parsing/generation).");
                    Console.WriteLine($"  --config             Optional. Path to config file. Default: {DefaultConfigFileName}");
                    Console.WriteLine("  -v, --verbose        Verbosity level. 0 = No logging, 1 = General information, 2 = Debugging information. (default: 1)");
                    return;
                }

                ILoggerFactory loggerFactory = CreateLoggerFactory(verbose);
                ILogger logger = loggerFactory.CreateLogger<BindingsGenerator>();

                // Parse and validate --platform
                var parsedPlatform = PlatformInfoFactory.ParsePlatform(platformStr);
                if (parsedPlatform == null)
                {
                    logger.LogError("Error: Invalid --platform '{Value}'. Valid values: 'ios', 'macos', 'tvos', 'maccatalyst'.", platformStr);
                    context.ExitCode = 1;
                    return;
                }
                var platformInfo = PlatformInfoFactory.Create(parsedPlatform.Value);

                // Validate --platform + --platform-target combinations
                if (!platformInfo.HasSimulatorVariant &&
                    string.Equals(platformTargetStr, "simulator", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "{Platform} has no simulator variant. Falling back to device slice.",
                        platformInfo.Platform);
                    platformTargetStr = "device";
                }

                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    logger.LogError("Error: Output directory (-o) is required.");
                    context.ExitCode = 1;
                    return;
                }

                // Validate mutual exclusivity: --skip-wrapper-compilation vs --compile-wrapper-only
                if (skipWrapperCompilation && compileWrapperOnly)
                {
                    logger.LogError("Error: --skip-wrapper-compilation and --compile-wrapper-only are mutually exclusive.");
                    context.ExitCode = 1;
                    return;
                }

                // Handle --compile-wrapper-only: fast path that skips all parsing/generation
                if (compileWrapperOnly)
                {
                    if (string.IsNullOrWhiteSpace(xcframeworkPath))
                    {
                        logger.LogError("Error: --compile-wrapper-only requires --xcframework.");
                        context.ExitCode = 1;
                        return;
                    }
                    context.ExitCode = RunCompileWrapperOnly(
                        xcframeworkPath!, outputDirectory, platformStr, platformTargetStr,
                        wrapperArchitectures, frameworkDependencies, logger, platformInfo);
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
                XCFrameworkResolver.ObjCFrameworkResolution? mixedObjcResolution = null;
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

                    // If --objc forced, skip Swift resolution entirely
                    if (objcForced)
                    {
                        var objcResolution = XCFrameworkResolver.ResolveObjCFramework(
                            xcframeworkPath!, platformTarget, logger, platformInfo: platformInfo);
                        if (objcResolution == null)
                        {
                            logger.LogError("Failed to resolve ObjC framework from '{Path}'.", xcframeworkPath);
                            context.ExitCode = 1;
                            return;
                        }
                        var siblingSearchPaths = XCFrameworkResolver.ResolveSiblingFrameworkSearchPaths(
                            xcframeworkPath!, platformTarget, logger, platformInfo: platformInfo);
                        var objcResult = ObjCPipeline.Run(
                            objcResolution, xcframeworkPath!, outputDirectory, platformTarget, logger,
                            namespacePattern: namespacePattern, packageId: packageId,
                            sdkMode: sdkMode, isMixed: false,
                            additionalFrameworkSearchPaths: siblingSearchPaths,
                            platformInfo: platformInfo);
                        context.ExitCode = objcResult.ExitCode;
                        if (objcResult.ErrorMessage != null)
                            logger.LogError("{Message}", objcResult.ErrorMessage);
                        return;
                    }

                    try
                    {
                        resolution = XCFrameworkResolver.Resolve(
                            xcframeworkPath!, outputDirectory, platformTarget, logger, platformInfo: platformInfo);
                        swiftAbiPath = resolution.AbiJsonPath;
                        dylibPath = resolution.DylibPath;
                        tbdPath = resolution.TbdPath;
                        swiftInterface ??= resolution.SwiftInterfacePath;
                        libraryName ??= resolution.ModuleName;

                        // Mixed framework detection: check for ObjC API alongside Swift
                        mixedObjcResolution = XCFrameworkResolver.DetectMixedFrameworkObjC(
                            resolution, platformTarget, logger, platformInfo: platformInfo);
                    }
                    catch (Exception ex) when (ex is SwiftModuleNotFoundException or StaticLibraryException)
                    {
                        // Auto-detect ObjC fallback (covers pure ObjC frameworks and static libraries like Firebase)
                        var reason = ex is StaticLibraryException ? "Static library" : "No Swift module found";
                        logger.LogInformation("{Reason} — attempting ObjC framework detection...", reason);
                        var objcResolution = XCFrameworkResolver.ResolveObjCFramework(
                            xcframeworkPath!, platformTarget, logger, platformInfo: platformInfo);
                        if (objcResolution == null)
                        {
                            logger.LogError("Framework has no ObjC module.modulemap and no Swift module.");
                            context.ExitCode = 1;
                            return;
                        }
                        var siblingSearchPaths = XCFrameworkResolver.ResolveSiblingFrameworkSearchPaths(
                            xcframeworkPath!, platformTarget, logger, platformInfo: platformInfo);
                        var objcResult = ObjCPipeline.Run(
                            objcResolution, xcframeworkPath!, outputDirectory, platformTarget, logger,
                            namespacePattern: namespacePattern, packageId: packageId,
                            sdkMode: sdkMode, isMixed: false,
                            additionalFrameworkSearchPaths: siblingSearchPaths,
                            platformInfo: platformInfo);
                        context.ExitCode = objcResult.ExitCode;
                        if (objcResult.ErrorMessage != null)
                            logger.LogError("{Message}", objcResult.ErrorMessage);
                        return;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Error resolving xcframework: {Message}", ex.Message);
                        context.ExitCode = 1;
                        return;
                    }

                    // Gate wrapper compilation:
                    // - --skip-wrapper-compilation: always skip (SDK defers to _CompileSwiftWrapper target)
                    // - simulator/all: needs a simulator slice (always present as primary when --platform-target simulator)
                    // - device: can compile with just a device slice
                    // - If the primary resolution is device-only and architectures is 'simulator', skip
                    var wrapperArchEarly = wrapperArchitectures?.ToLowerInvariant() ?? "simulator";
                    shouldCompileWrapper = !skipWrapperCompilation &&
                        ShouldCompileWrapper(resolution.IsSimulatorSlice, wrapperArchEarly, platformInfo);
                    if (!shouldCompileWrapper)
                    {
                        logger.LogInformation(
                            "Swift wrapper compilation requires a simulator slice or --wrapper-architectures device/all. " +
                            "Pass --async-library manually for device-only builds without wrapper compilation.");
                    }

                    // Auto-set --async-library whenever wrapper will be compiled (now or deferred).
                    // When --skip-wrapper-compilation is used, the wrapper is compiled later by
                    // _CompileSwiftWrapper, but C# generation still needs the module name for DllImport.
                    var wouldCompileWrapper = ShouldCompileWrapper(resolution.IsSimulatorSlice, wrapperArchEarly, platformInfo);
                    if (wouldCompileWrapper && string.IsNullOrWhiteSpace(asyncLibrary))
                    {
                        var wrapperModuleName = $"{resolution.ModuleName}SwiftBindings";
                        asyncLibrary = wrapperModuleName;
                        asyncLibraryAutoWired = !skipWrapperCompilation; // Only true if actually compiling now
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
                        logger, platformInfo: platformInfo);
                    if (analysisResult != null)
                    {
                        autoDetectedDeps = analysisResult.ResolvedDependencies;
                        foreach (var dep in autoDetectedDeps)
                            logger.LogInformation("Auto-detected dependency: {Module} ({Path})",
                                dep.ModuleName, dep.XCFrameworkPath);
                        foreach (var unresolved in analysisResult.UnresolvedDependencies)
                        {
                            logger.LogWarning("{Message}",
                                FormatDependencyWarning(unresolved.FrameworkName, unresolved.UnresolvedReason ?? "missing-xcframework"));
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
                        platformTarget, logger, platformInfo: platformInfo);
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

                // Validate --module-database paths upfront (fail-fast for missing/invalid files)
                if (moduleDatabases?.Length > 0)
                {
                    foreach (var dbPath in moduleDatabases)
                    {
                        if (!File.Exists(dbPath))
                        {
                            logger.LogError("SWIFTBIND070: Module database not found: '{Path}'.", dbPath);
                            context.ExitCode = 1;
                            return;
                        }
                    }
                }

                // Use the provided library name, or fall back to the dylib path
                var runtimeLibraryName = string.IsNullOrWhiteSpace(libraryName) ? dylibPath : libraryName;
                var effectiveNamespacePattern = ResolveNamespacePattern(namespacePattern, configPath, logger);

                // Auto-extract symbol graph for doc comments (xcframework mode only)
                symbolGraph = ResolveSymbolGraphPath(symbolGraph, noDocs, resolution, outputDirectory, logger, platformInfo: platformInfo);

                var depModuleNames = resolvedDependencies?
                    .Where(d => !d.IsObjCOnly)
                    .Select(d => d.ModuleName)
                    .ToList();
                var success = GenerateBindings(swiftAbiPath, dylibPath, tbdPath, outputDirectory, runtimeLibraryName, asyncLibrary, swiftInterface, symbolGraph, bridgeHints, effectiveNamespacePattern, logger, loggerFactory, out var internalTypeNames, out var moduleNameForCollision, out var nestedTypesInCollidingClass, dependencyModuleNames: depModuleNames, moduleDatabasePaths: moduleDatabases, resolvedDependencies: resolvedDependencies, platform: platformInfo.Platform);
                if (!success)
                {
                    context.ExitCode = 1;
                    return;
                }

                // Persist wrapper compilation context for --compile-wrapper-only mode.
                // These values are computed during generation and needed by the deferred
                // wrapper compilation pass (SDK two-pass build).
                if (hasXcframework)
                {
                    SaveWrapperContext(outputDirectory, internalTypeNames, moduleNameForCollision, nestedTypesInCollidingClass, logger);
                }

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
                                xcframeworkPath!, outputDirectory, logger, platformInfo: platformInfo);

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
                                deviceAdditionalSearchPaths: deviceDepPaths,
                                platformInfo: platformInfo,
                                moduleNameForCollision: moduleNameForCollision,
                                nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                                swiftInterfacePath: resolution.SwiftInterfacePath);
                        }
                        else if (wrapperArchNormalized == "device")
                        {
                            // Device-only: resolve device slice and compile for iphoneos
                            XCFrameworkResolution deviceOnlyResolution;
                            try
                            {
                                deviceOnlyResolution = XCFrameworkResolver.Resolve(
                                    xcframeworkPath!, outputDirectory,
                                    XCFrameworkPlatformTarget.Device, logger, platformInfo: platformInfo);
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
                                additionalFrameworkSearchPaths: deviceDepPaths,
                                platformInfo: platformInfo,
                                moduleNameForCollision: moduleNameForCollision,
                                nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                                swiftInterfacePath: deviceOnlyResolution.SwiftInterfacePath);
                        }
                        else
                        {
                            // Simulator-only (default)
                            compilationResult = SwiftWrapperCompiler.Compile(
                                outputDirectory, resolution.ModuleName,
                                resolution.FrameworkSearchPath, resolution.DylibPath, logger,
                                internalTypeNames: internalTypeNames,
                                additionalFrameworkSearchPaths: simDepPaths,
                                platformInfo: platformInfo,
                                moduleNameForCollision: moduleNameForCollision,
                                nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                                swiftInterfacePath: resolution.SwiftInterfacePath);
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

                // Run mixed framework ObjC pipeline (after Swift bindings generated, before project emission)
                ObjCPipelineResult? mixedObjcResult = null;
                if (hasXcframework && resolution != null && mixedObjcResolution != null)
                {
                    var swiftTypeNames = CollectSwiftEmittedTypeNames(outputDirectory);
                    var mixedSiblingPaths = XCFrameworkResolver.ResolveSiblingFrameworkSearchPaths(
                        xcframeworkPath!, platformTarget, logger, platformInfo: platformInfo);
                    mixedObjcResult = ObjCPipeline.Run(
                        mixedObjcResolution, xcframeworkPath!, outputDirectory, platformTarget, logger,
                        namespacePattern: namespacePattern, packageId: null,
                        sdkMode: sdkMode, isMixed: true, excludeTypeNames: swiftTypeNames,
                        additionalFrameworkSearchPaths: mixedSiblingPaths,
                        platformInfo: platformInfo);
                    if (mixedObjcResult.ExitCode != 0 && mixedObjcResult.ErrorMessage != null)
                        logger.LogWarning("ObjC pipeline for mixed framework: {Msg}", mixedObjcResult.ErrorMessage);
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
                        var effectivePackageId = packageId ?? platformInfo.GetDefaultSwiftPackageId(resolution.ModuleName);
                        var wrapperModuleName = $"{resolution.ModuleName}SwiftBindings";

                        // Mixed requires at least one ObjC class, protocol, or category.
                        // Enums/structs/functions without classes are typically C internals.
                        bool isMixed = mixedObjcResult?.ExitCode == 0
                            && (mixedObjcResult.Module?.Classes.Count > 0
                                || mixedObjcResult.Module?.Protocols.Count > 0
                                || mixedObjcResult.Module?.Categories.Count > 0);
                        string? objcProjFileName = isMixed
                            ? Path.GetFileName(mixedObjcResult!.ProjectPath!)
                            : null;

                        // Always emit metadata props (used by SDK and standalone)
                        XCFrameworkMetadataExtractor.EmitMetadataProps(
                            metadata, outputDirectory, hasWrapperXcfw,
                            wrapperModuleName,
                            compilationResult?.SliceCount ?? 0, logger,
                            resolvedDependencies,
                            frameworkType: isMixed ? "Mixed" : "Swift",
                            objcProjectName: objcProjFileName,
                            platformInfo: platformInfo);

                        // Only emit .csproj in non-SDK mode
                        if (!sdkMode)
                        {
                            var projectFrameworkName = InferFrameworkName(resolution.DylibPath, resolution.ModuleName);
                            var projectResolver = new NamespacePatternResolver(effectiveNamespacePattern, projectFrameworkName);
                            BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                            {
                                OutputDirectory = outputDirectory,
                                ModuleName = resolution.ModuleName,
                                Metadata = metadata,
                                SourceXCFrameworkPath = resolution.XCFrameworkPath,
                                WrapperXCFrameworkPath = hasWrapperXcfw ? wrapperXcfwPath : null,
                                Dependencies = resolvedDependencies,
                                ResolvedNamespace = projectResolver.ResolveNamespace(resolution.ModuleName),
                                ObjCProjectFileName = objcProjFileName,
                                PlatformInfo = platformInfo,
                            }, logger);
                        }

                        ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                        {
                            OutputDirectory = outputDirectory,
                            ModuleName = resolution.ModuleName,
                            PackageId = effectivePackageId,
                            EffectiveMinimumOSVersion = metadata.EffectiveMinimumOSVersion,
                            HasWrapperXCFramework = hasWrapperXcfw,
                            XcframeworkPath = xcframeworkPath,
                            PlatformInfo = platformInfo,
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
            GenerateBindings(swiftAbiPath, dylibPath, tbdPath, outputDirectory, runtimeLibraryName, asyncLibraryName, swiftInterfacePath, symbolGraphPath, bridgeHintsPath, namespacePattern, logger, loggerFactory, out _, out _, out _, dependencyModuleNames: null, moduleDatabasePaths: null);
        }

        private static bool GenerateBindings(string swiftAbiPath, string dylibPath, string tbdPath, string outputDirectory, string runtimeLibraryName, string? asyncLibraryName, string? swiftInterfacePath, string? symbolGraphPath, string? bridgeHintsPath, string namespacePattern, ILogger logger, ILoggerFactory loggerFactory, out HashSet<string>? internalTypeNames, out string? moduleNameForCollision, out HashSet<string>? nestedTypesInCollidingClass, List<string>? dependencyModuleNames = null, string[]? moduleDatabasePaths = null, List<FrameworkDependencyInfo>? resolvedDependencies = null, ApplePlatform? platform = null)
        {
            internalTypeNames = null;
            moduleNameForCollision = null;
            nestedTypesInCollidingClass = null;
            try
            {
            var typeDatabase = new TypeDatabase();
            typeDatabase.AsyncLibraryName = asyncLibraryName;

            // Platform-aware database loading: skip databases for frameworks that are
            // entirely absent on the target platform. Unused entries are harmless (lookup-based),
            // but skipping them avoids spurious type resolution for unavailable frameworks.
            string[] builtInDatabases = { "FoundationDatabase.xml", "SwiftDatabase.xml", "CoreGraphicsDatabase.xml", "DispatchDatabase.xml", "CoreImageDatabase.xml", "SwiftUIDatabase.xml", "AVFoundationDatabase.xml", "CoreTextDatabase.xml", "SecurityDatabase.xml", "QuartzCoreDatabase.xml", "PhotosDatabase.xml", "CoreBluetoothDatabase.xml", "CoreLocationDatabase.xml", "MapKitDatabase.xml", "MetalDatabase.xml", "CoreMLDatabase.xml", "StoreKitDatabase.xml", "SceneKitDatabase.xml", "NaturalLanguageDatabase.xml" };
            foreach (var database in builtInDatabases)
            {
                typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", database)).Wait();
            }
            // UIKit: available on all platforms except macOS (Catalyst has UIKit)
            if (platform != ApplePlatform.macOS)
            {
                typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "UIKitDatabase.xml")).Wait();
            }
            // AppKit: macOS and Catalyst only (Catalyst has AppKit compatibility layer)
            if (platform == null || platform == ApplePlatform.macOS || platform == ApplePlatform.MacCatalyst)
            {
                typeDatabase.LoadModuleDatabaseFromFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift", "AppKitDatabase.xml")).Wait();
            }

            // Peek at current module name once for self-reference checks in both
            // --module-database and --framework-dependency loading below.
            string? currentModuleName = null;
            try
            {
                currentModuleName = PeekModuleNameFromAbiJson(swiftAbiPath);
            }
            catch
            {
                // Non-fatal: self-reference checks will be skipped
            }

            // Load dependency module databases for cross-module type resolution
            if (moduleDatabasePaths != null)
            {
                foreach (var dbPath in moduleDatabasePaths)
                {
                    var dbModuleName = PeekModuleNameFromXml(dbPath);
                    if (dbModuleName == null)
                    {
                        logger.LogError("SWIFTBIND072: Invalid module database XML: '{Path}'.", dbPath);
                        return false;
                    }

                    if (currentModuleName != null && dbModuleName == currentModuleName)
                    {
                        logger.LogError("SWIFTBIND071: Module database '{Path}' targets current module '{Module}'. " +
                            "Do not pass the current module's own database as a dependency.", dbPath, dbModuleName);
                        return false;
                    }

                    if (typeDatabase.IsModuleLoaded(dbModuleName))
                    {
                        logger.LogInformation("Module '{Module}' already loaded (built-in), skipping.", dbModuleName);
                        continue;
                    }

                    try
                    {
                        typeDatabase.LoadModuleDatabaseFromFile(dbPath).Wait();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("SWIFTBIND072: Failed to load module database '{Path}': {Message}", dbPath, ex.InnerException?.Message ?? ex.Message);
                        return false;
                    }
                    logger.LogInformation("Loaded dependency module database: {Path} (module: {Module})", dbPath, dbModuleName);
                }
            }

            // Load dependency type databases from framework dependency ABI JSON files.
            // This enables cross-module type resolution: dependency types resolve to concrete
            // projections instead of falling back to AnyType.
            if (resolvedDependencies != null)
            {
                foreach (var dep in resolvedDependencies)
                {
                    // Skip ObjC-only deps (no Swift ABI) and deps without ABI JSON
                    if (dep.IsObjCOnly || string.IsNullOrEmpty(dep.AbiJsonPath) || string.IsNullOrEmpty(dep.TbdPath))
                        continue;

                    // Skip self-reference
                    if (currentModuleName != null && dep.ModuleName == currentModuleName)
                        continue;

                    // Skip if already loaded (built-in XML or --module-database)
                    if (typeDatabase.IsModuleLoaded(dep.ModuleName))
                    {
                        logger.LogInformation("Dependency module '{Module}' already loaded, skipping ABI parse.", dep.ModuleName);
                        continue;
                    }

                    try
                    {
                        var depDemangledTbd = Demangling.DemanglingResults.FromTbd(dep.TbdPath, loggerFactory);
                        var depParser = new SwiftABIParser(
                            dep.AbiJsonPath, typeDatabase, depDemangledTbd,
                            loggerFactory.CreateLogger<SwiftABIParser>());
                        var depModuleName = depParser.GetModuleName();
                        var depParseResult = depParser.ParseModule();

                        var depProcessor = new ModuleProcessor(
                            depModuleName, dep.DylibPath ?? dep.AbiJsonPath, dep.DylibPath ?? dep.AbiJsonPath,
                            depParseResult.TypeDecls, typeDatabase,
                            loggerFactory.CreateLogger<ModuleProcessor>());
                        var depModuleDb = depProcessor.FinalizeTypeProcessingAndCreateModuleDatabase().ModuleDatabase;
                        typeDatabase.AddModuleDatabase(depModuleDb);
                        logger.LogInformation("Loaded dependency types from ABI JSON: {Module}", depModuleName);
                    }
                    catch (Exception ex)
                    {
                        if (dep.IsAutoDetected)
                        {
                            // Auto-detected dependencies are best-effort — warn and continue
                            logger.LogWarning(
                                "Could not load dependency types for auto-detected module '{Module}': {Message}. " +
                                "Dependency types will resolve to AnyType.",
                                dep.ModuleName, ex.InnerException?.Message ?? ex.Message);
                        }
                        else
                        {
                            // Explicit --framework-dependency — fail hard (matches existing fail-fast behavior)
                            logger.LogError(
                                "SWIFTBIND073: Failed to parse dependency ABI for '{Module}': {Message}",
                                dep.ModuleName, ex.InnerException?.Message ?? ex.Message);
                            return false;
                        }
                    }
                }
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
            HashSet<string>? publicTypeNames = null;
            HashSet<string>? mainActorTypes = null;
            HashSet<string>? customActorTypes = null;
            HashSet<string>? actorIsolatedMembers = null;
            HashSet<string>? mainActorIsolatedMembers = null;
            HashSet<string>? nonisolatedMembers = null;
            Dictionary<string, List<string>>? markerProtocolConformances = null;
            Dictionary<string, List<AvailabilityAnnotation>>? availabilityAnnotations = null;
            Dictionary<string, List<string?>>? defaultParameterValues = null;
            Dictionary<string, List<bool>>? autoclosureParameters = null;
            Dictionary<string, List<string>>? subscriptLabels = null;
            HashSet<string>? variadicMembers = null;
            HashSet<string>? publicMemberNames = null;
            if (!string.IsNullOrWhiteSpace(swiftInterfacePath) && File.Exists(swiftInterfacePath))
            {
                internalMemberKeys = SwiftInterfaceAccessParser.GetInternalMembers(swiftInterfacePath, out var parsedPublicMembers);
                publicMemberNames = parsedPublicMembers;
                logger.LogInformation("Loaded {Count} internal member keys and {PublicCount} public member names from swiftinterface", internalMemberKeys.Count, publicMemberNames.Count);
                parameterNames = SwiftInterfaceAccessParser.GetParameterNames(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} parameter name entries from swiftinterface", parameterNames.Count);
                typedThrowsErrors = SwiftInterfaceAccessParser.GetTypedThrowsErrors(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} typed throws entries from swiftinterface", typedThrowsErrors.Count);
                enumCaseLabels = SwiftInterfaceAccessParser.GetEnumCaseLabels(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} enum case label entries from swiftinterface", enumCaseLabels.Count);
                publicTypeNames = SwiftInterfaceAccessParser.GetPublicTypeNames(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} public type names from swiftinterface", publicTypeNames.Count);
                mainActorTypes = SwiftInterfaceAccessParser.GetMainActorTypes(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} @MainActor type names from swiftinterface", mainActorTypes.Count);
                customActorTypes = SwiftInterfaceAccessParser.GetCustomActorTypes(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} custom actor type names from swiftinterface", customActorTypes.Count);
                actorIsolatedMembers = SwiftInterfaceAccessParser.GetActorIsolatedMembers(swiftInterfacePath, customActorTypes, out var mainActorMembersOut);
                mainActorIsolatedMembers = mainActorMembersOut;
                logger.LogInformation("Loaded {Count} actor-isolated member keys ({MainActorCount} @MainActor) from swiftinterface", actorIsolatedMembers.Count, mainActorIsolatedMembers.Count);
                nonisolatedMembers = SwiftInterfaceAccessParser.GetNonisolatedMembers(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} nonisolated member keys from swiftinterface", nonisolatedMembers.Count);
                markerProtocolConformances = SwiftInterfaceAccessParser.GetMarkerProtocolConformances(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} marker protocol conformance entries from swiftinterface", markerProtocolConformances.Count);
                availabilityAnnotations = SwiftInterfaceAccessParser.GetAvailabilityAnnotations(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} availability annotation entries from swiftinterface", availabilityAnnotations.Count);
                defaultParameterValues = SwiftInterfaceAccessParser.GetDefaultParameterValues(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} default parameter value entries from swiftinterface", defaultParameterValues.Count);
                autoclosureParameters = SwiftInterfaceAccessParser.GetAutoclosureParameters(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} @autoclosure parameter entries from swiftinterface", autoclosureParameters.Count);
                subscriptLabels = SwiftInterfaceAccessParser.GetSubscriptLabels(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} subscript label entries from swiftinterface", subscriptLabels.Count);
                variadicMembers = SwiftInterfaceAccessParser.GetVariadicMembers(swiftInterfacePath);
                logger.LogInformation("Loaded {Count} variadic member keys from swiftinterface", variadicMembers.Count);
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
            var swiftParser = new SwiftABIParser(swiftAbiPath, typeDatabase, demangledTbdFile, loggerFactory.CreateLogger<SwiftABIParser>(), internalMemberKeys, parameterNames, docComments, typedThrowsErrors, enumCaseLabels, publicTypeNames, mainActorTypes, customActorTypes, actorIsolatedMembers, nonisolatedMembers, availabilityAnnotations, defaultParameterValues, autoclosureParameters, publicMemberNames, subscriptLabels, mainActorIsolatedMembers, variadicMembers);
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
                if (dependencyModuleNames != null)
                    decl.DependencyModuleNames = dependencyModuleNames;
                internalTypeNames = CollectInternalTypeNames(decl);

                // Detect module/type name collision: a public type whose name matches the module name.
                // When this occurs, Swift resolves bare "ModuleName" as the type, not the module,
                // causing "ModuleName.X" to fail (looks for X nested in the class, not in the module).
                var collidingType = decl.Types.FirstOrDefault(t => !t.IsModuleInternal && t.Name == moduleName);
                if (collidingType != null)
                {
                    moduleNameForCollision = moduleName;
                    logger.LogInformation("Detected module/type name collision: module '{Module}' has a public type with the same name. Will strip module prefixes in Swift wrapper.", moduleName);

                    // EC-18: Collect types nested inside the colliding class to prevent
                    // over-stripping. E.g., SwiftyBeaver.Level should stay qualified
                    // because Level is nested in class SwiftyBeaver, not a module-level type.
                    var nestedNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var innerType in collidingType.Types)
                    {
                        nestedNames.Add(innerType.Name);
                    }
                    if (nestedNames.Count > 0)
                    {
                        nestedTypesInCollidingClass = nestedNames;
                        logger.LogInformation("Found {Count} nested type(s) in colliding class '{Module}': {Types}",
                            nestedNames.Count, moduleName, string.Join(", ", nestedNames));
                    }
                }

                // Wire publicTypeNames from swiftinterface as keep-override for underscore suppression.
                // publicTypeNames are dot-qualified (e.g., "_InternalType"); underscore suppression
                // uses module-qualified names (e.g., "Module._InternalType"). Normalize by prepending module.
                HashSet<string>? keepUnderscoreTypes = null;
                if (publicTypeNames != null)
                {
                    keepUnderscoreTypes = new HashSet<string>();
                    foreach (var name in publicTypeNames)
                    {
                        if (name.StartsWith("_") || name.Contains("._"))
                            keepUnderscoreTypes.Add($"{moduleName}.{name}");
                    }
                    if (keepUnderscoreTypes.Count == 0)
                        keepUnderscoreTypes = null;
                }
                var underscoreSuppressedNames = CollectUnderscoreSuppressedTypeNames(decl, keepUnderscoreTypes);
                // Merge underscore-suppressed names into internalTypeNames for wrapper post-processing
                if (underscoreSuppressedNames.Count > 0)
                {
                    internalTypeNames ??= new HashSet<string>();
                    internalTypeNames.UnionWith(underscoreSuppressedNames);
                    logger.LogInformation("Suppressing {Count} underscore-prefixed types from C# output", underscoreSuppressedNames.Count);
                }
                ReportCollector.Start(decl);

                // dylibPath is used for metadata extraction, runtimeLibraryName is used in generated DllImport
                var moduleProcessor = new ModuleProcessor(moduleName, dylibPath, runtimeLibraryName, moduleTypes, typeDatabase, loggerFactory.CreateLogger<ModuleProcessor>(), namespaceResolver);
                var moduleDatabase = moduleProcessor.FinalizeTypeProcessingAndCreateModuleDatabase().ModuleDatabase;
                typeDatabase.AddModuleDatabase(moduleDatabase);

                logger.LogDebug("Parsed Swift ABI file successfully.");

                // Create per-module emission context (replaces static mutable state + ResetForModule)
                var emissionContext = new ModuleEmissionContext();
                emissionContext.SetUnderscoreSuppressedNames(underscoreSuppressedNames);

                // Parse protocol names first — needed by both protocol and foreign extension paths
                var protocolNames = !string.IsNullOrWhiteSpace(swiftInterfacePath) && File.Exists(swiftInterfacePath)
                    ? SwiftInterfaceAccessParser.GetProtocolNames(swiftInterfacePath)
                    : new HashSet<string>();

                // Parse protocol extension methods from swiftinterface and inject onto conforming types
                if (protocolNames.Count > 0)
                {
                    var extensionMethods = SwiftInterfaceAccessParser.GetProtocolExtensionMethods(swiftInterfacePath!, protocolNames);
                    if (extensionMethods.Count > 0)
                    {
                        // Build extension defaults index BEFORE injection — used by validator to allow
                        // conformance when types rely on protocol extension default implementations.
                        var extensionDefaultsIndex = new ProtocolExtensionDefaultsIndex(extensionMethods, decl.Protocols);
                        emissionContext.ExtensionDefaultsIndex = extensionDefaultsIndex;

                        ProtocolExtensionEmitter.InjectExtensionMethods(decl, extensionMethods, typeDatabase, logger, emissionContext);
                    }
                }

                // Parse foreign type extension members and process them
                if (!string.IsNullOrWhiteSpace(swiftInterfacePath) && File.Exists(swiftInterfacePath))
                {
                    var moduleTypeNames = new HashSet<string>(decl.Types.Select(t => t.Name));
                    var foreignExtensions = SwiftInterfaceAccessParser.GetForeignTypeExtensionMembers(
                        swiftInterfacePath, protocolNames, moduleTypeNames, moduleName);
                    if (foreignExtensions.Count > 0)
                    {
                        ForeignTypeExtensionEmitter.ProcessForeignTypeExtensions(
                            decl, foreignExtensions, typeDatabase, logger, emissionContext);
                    }
                }

                // Emit the C# bindings
                var stringEmitter = new StringEmitter(outputDirectory, typeDatabase, loggerFactory, namespaceResolver, bridgeHintsPath, markerProtocolConformances);
                stringEmitter.EmitModule(decl, emissionContext);

                var report = ReportCollector.Complete();
                if (report != null)
                {
                    ReportEmitter.Emit(report, outputDirectory, logger);
                }
                ReportCollector.Reset();

                // Emit emission-level metrics (wrapper strategies, conformance decisions)
                EmissionReportEmitter.Emit(emissionContext, moduleName, outputDirectory, logger);

                // Fixup protocol EmittedMemberCount to include inherited requirements.
                // Must run after EmitModule (all direct counts set) and before database serialization.
                ProtocolHandler.FixupProtocolInheritedRequirements(decl, typeDatabase);

                // Emit module database XML for cross-module resolution by downstream modules
                ModuleDatabaseEmitter.Emit(moduleDatabase, outputDirectory, logger);

                logger.LogInformation("Bindings generation completed for {SwiftAbiPath}.", swiftAbiPath);

            }
            else
                logger.LogWarning("Bindings generation already completed for {SwiftAbiPath}.", swiftAbiPath);

            return true;
            }
            catch (Exception ex)
            {
                logger.LogError("Binding generation failed: {Message}", ex.Message);
                logger.LogDebug("Stack trace:\n{StackTrace}", ex.ToString());
                ReportCollector.Reset();
                return false;
            }
        }

        /// <summary>
        /// Compile-wrapper-only mode: resolves the xcframework, compiles existing .swift wrapper files,
        /// and updates binding-metadata.props. Skips all parsing and C# generation.
        /// </summary>
        internal static int RunCompileWrapperOnly(
            string xcframeworkPath, string outputDirectory,
            string? platformStr, string? platformTargetStr,
            string? wrapperArchitectures, string[]? frameworkDependencies,
            ILogger logger, PlatformInfo platformInfo)
        {
            var wrapperArchNormalized = wrapperArchitectures?.ToLowerInvariant() ?? "simulator";
            if (wrapperArchNormalized != "simulator" && wrapperArchNormalized != "device" && wrapperArchNormalized != "all")
            {
                logger.LogError("Error: Invalid --wrapper-architectures '{Value}'. Valid values: 'simulator', 'device', 'all'.", wrapperArchitectures);
                return 1;
            }

            var platformTarget = XCFrameworkPlatformTarget.Simulator;
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
                    logger.LogError("Error: Invalid --platform-target '{Value}'.", platformTargetStr);
                    return 1;
            }

            // Resolve xcframework to get module name and search paths
            XCFrameworkResolution resolution;
            try
            {
                resolution = XCFrameworkResolver.Resolve(
                    xcframeworkPath, outputDirectory, platformTarget, logger, platformInfo: platformInfo);
            }
            catch (Exception ex)
            {
                logger.LogError("Error resolving xcframework: {Message}", ex.Message);
                return 1;
            }

            var moduleName = resolution.ModuleName;

            // Resolve framework dependency search paths
            List<FrameworkDependencyInfo>? resolvedDeps = null;
            if (frameworkDependencies != null && frameworkDependencies.Length > 0)
            {
                resolvedDeps = ResolveFrameworkDependencies(
                    frameworkDependencies, resolution, xcframeworkPath,
                    wrapperArchNormalized, platformTarget, logger, platformInfo: platformInfo);
                if (resolvedDeps == null)
                    return 1;
            }

            var simDepPaths = resolvedDeps?
                .Where(d => d.SimulatorFrameworkSearchPath != null)
                .Select(d => d.SimulatorFrameworkSearchPath!)
                .ToList();
            var deviceDepPaths = resolvedDeps?
                .Where(d => d.DeviceFrameworkSearchPath != null)
                .Select(d => d.DeviceFrameworkSearchPath!)
                .ToList();

            // Load wrapper compilation context saved by the generation pass
            var (internalTypeNames, moduleNameForCollision, nestedTypesInCollidingClass) =
                LoadWrapperContext(outputDirectory, logger);

            // Compile the wrapper using existing .swift files in the output directory
            SwiftWrapperCompilationResult? compilationResult = null;
            Exception? compilationException = null;

            try
            {
                if (wrapperArchNormalized == "all")
                {
                    var (simResolution, deviceResolution) = XCFrameworkResolver.ResolveAll(
                        xcframeworkPath, outputDirectory, logger, platformInfo: platformInfo);

                    compilationResult = SwiftWrapperCompiler.CompileAll(
                        outputDirectory, moduleName,
                        simResolution, deviceResolution, logger,
                        internalTypeNames: internalTypeNames,
                        simAdditionalSearchPaths: simDepPaths,
                        deviceAdditionalSearchPaths: deviceDepPaths,
                        platformInfo: platformInfo,
                        moduleNameForCollision: moduleNameForCollision,
                        nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                        swiftInterfacePath: resolution.SwiftInterfacePath);
                }
                else if (wrapperArchNormalized == "device")
                {
                    XCFrameworkResolution deviceResolution;
                    try
                    {
                        deviceResolution = XCFrameworkResolver.Resolve(
                            xcframeworkPath, outputDirectory,
                            XCFrameworkPlatformTarget.Device, logger, platformInfo: platformInfo);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Cannot compile device wrapper: {Message}", ex.Message);
                        return 1;
                    }

                    compilationResult = SwiftWrapperCompiler.CompileSlice(
                        outputDirectory, moduleName,
                        deviceResolution.FrameworkSearchPath,
                        deviceResolution.DylibPath,
                        "device", "iphoneos", logger,
                        internalTypeNames: internalTypeNames,
                        additionalFrameworkSearchPaths: deviceDepPaths,
                        platformInfo: platformInfo,
                        moduleNameForCollision: moduleNameForCollision,
                        nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                        swiftInterfacePath: deviceResolution.SwiftInterfacePath);
                }
                else
                {
                    compilationResult = SwiftWrapperCompiler.Compile(
                        outputDirectory, moduleName,
                        resolution.FrameworkSearchPath, resolution.DylibPath, logger,
                        internalTypeNames: internalTypeNames,
                        additionalFrameworkSearchPaths: simDepPaths,
                        platformInfo: platformInfo,
                        moduleNameForCollision: moduleNameForCollision,
                        nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                        swiftInterfacePath: resolution.SwiftInterfacePath);
                }
            }
            catch (Exception ex)
            {
                compilationException = ex;
            }

            var rawOutcome = SwiftWrapperCompiler.EvaluateResult(
                compilationResult, false, compilationException);
            // In compile-wrapper-only mode, always use SDK-mode outcome handling
            // (downgrade fatal to warning) since this target runs within SDK builds
            var (outcomeExitCode, diagnosticCode, outcomeMessage) =
                HandleWrapperCompilationOutcome(rawOutcome, sdkMode: true, compilationException, compilationResult);

            if (outcomeExitCode != 0)
            {
                logger.LogError("{Message}", outcomeMessage);
            }
            else if (diagnosticCode == "SWIFTBIND050" || rawOutcome == WrapperCompilationOutcome.Warning)
            {
                logger.LogWarning("{Message}", outcomeMessage);
            }

            // Update binding-metadata.props with wrapper compilation result
            var hasWrapperXcfw = compilationResult?.XCFrameworkPath != null
                && Directory.Exists(compilationResult.XCFrameworkPath);
            var wrapperModuleName = $"{moduleName}SwiftBindings";

            XCFrameworkMetadataExtractor.UpdateMetadataPropsWrapperStatus(
                outputDirectory, hasWrapperXcfw, wrapperModuleName,
                compilationResult?.SliceCount ?? 0, logger);

            return outcomeExitCode;
        }

        /// <summary>
        /// Determines whether wrapper compilation should proceed based on the resolved
        /// slice type and the requested wrapper architecture scope.
        /// </summary>
        /// <param name="isSimulatorSlice">True when the primary resolution is a simulator slice.</param>
        /// <param name="wrapperArchitectures">Normalized value of --wrapper-architectures (simulator/device/all).</param>
        internal static bool ShouldCompileWrapper(bool isSimulatorSlice, string wrapperArchitectures, PlatformInfo? platformInfo = null)
        {
            // Platforms without simulator variants (macOS, Mac Catalyst) should always compile
            // with device slice when wrapperArchitectures is "simulator" (the default).
            if (platformInfo != null && !platformInfo.HasSimulatorVariant)
                return true;

            return isSimulatorSlice
                || wrapperArchitectures == "device"
                || wrapperArchitectures == "all";
        }

        private const string WrapperContextFileName = "wrapper-context.json";

        /// <summary>
        /// Persists wrapper compilation context (computed during generation) to a JSON file
        /// so that --compile-wrapper-only can read it back for the deferred compilation pass.
        /// </summary>
        internal static void SaveWrapperContext(
            string outputDirectory,
            HashSet<string>? internalTypeNames,
            string? moduleNameForCollision,
            HashSet<string>? nestedTypesInCollidingClass,
            ILogger logger)
        {
            var contextPath = Path.Combine(outputDirectory, WrapperContextFileName);
            var context = new JObject
            {
                ["internalTypeNames"] = internalTypeNames != null
                    ? new JArray(internalTypeNames.OrderBy(n => n).ToArray())
                    : new JArray(),
                ["moduleNameForCollision"] = moduleNameForCollision,
                ["nestedTypesInCollidingClass"] = nestedTypesInCollidingClass != null
                    ? new JArray(nestedTypesInCollidingClass.OrderBy(n => n).ToArray())
                    : new JArray(),
            };
            File.WriteAllText(contextPath, context.ToString(Newtonsoft.Json.Formatting.Indented));
            logger.LogInformation("Saved wrapper context to {Path}", contextPath);
        }

        /// <summary>
        /// Loads wrapper compilation context saved by a prior generation pass.
        /// Returns null values if the context file doesn't exist (backward compatible).
        /// </summary>
        internal static (HashSet<string>? internalTypeNames, string? moduleNameForCollision, HashSet<string>? nestedTypesInCollidingClass)
            LoadWrapperContext(string outputDirectory, ILogger logger)
        {
            var contextPath = Path.Combine(outputDirectory, WrapperContextFileName);
            if (!File.Exists(contextPath))
            {
                logger.LogInformation("No wrapper context file at {Path} — using defaults.", contextPath);
                return (null, null, null);
            }

            try
            {
                var json = JObject.Parse(File.ReadAllText(contextPath));
                var internalTypeNames = json["internalTypeNames"]?.Values<string>()
                    .Where(n => n != null).Select(n => n!).ToHashSet();
                var moduleNameForCollision = json["moduleNameForCollision"]?.Value<string>();
                var nestedTypes = json["nestedTypesInCollidingClass"]?.Values<string>()
                    .Where(n => n != null).Select(n => n!).ToHashSet();
                logger.LogInformation("Loaded wrapper context from {Path}", contextPath);
                return (internalTypeNames, moduleNameForCollision, nestedTypes);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to load wrapper context: {Message}", ex.Message);
                return (null, null, null);
            }
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
            ILogger logger, ICommandRunner? commandRunner = null,
            PlatformInfo? platformInfo = null)
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

            return SymbolGraphExtractor.Extract(resolution, outputDirectory, logger, commandRunner, platformInfo);
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
        /// Collects underscore-prefixed type names to suppress from C# output.
        /// Types with a leading underscore are considered internal implementation details
        /// unless they are structurally required (e.g., as a superclass of a non-underscore type)
        /// or explicitly kept via the override set.
        /// </summary>
        /// <param name="module">The parsed module declaration.</param>
        /// <param name="keepUnderscoreTypes">
        /// Optional set of module-qualified names to exempt from suppression.
        /// When non-null, any underscore-prefixed type in this set is preserved.
        /// </param>
        /// <returns>Set of module-qualified type names to suppress.</returns>
        internal static HashSet<string> CollectUnderscoreSuppressedTypeNames(
            ModuleDecl module, HashSet<string>? keepUnderscoreTypes = null)
        {
            var underscoreTypes = new HashSet<string>();
            var structurallyRequired = new HashSet<string>();

            CollectUnderscoreTypeNames(module.Types, underscoreTypes, structurallyRequired);

            // Remove structurally required types (superclasses/protocols of non-underscore types)
            underscoreTypes.ExceptWith(structurallyRequired);

            // Remove explicitly kept types
            if (keepUnderscoreTypes != null)
                underscoreTypes.ExceptWith(keepUnderscoreTypes);

            return underscoreTypes;
        }

        private static void CollectUnderscoreTypeNames(
            IEnumerable<TypeDecl> types,
            HashSet<string> underscoreTypes,
            HashSet<string> structurallyRequired)
        {
            foreach (var t in types)
            {
                var qualifiedName = t.SwiftTypeName?.ToString();
                if (qualifiedName != null && t.Name.StartsWith("_"))
                {
                    underscoreTypes.Add(qualifiedName);
                }

                // Check if this non-underscore type references underscore-prefixed types
                if (!t.Name.StartsWith("_"))
                {
                    // Superclass references (ClassDecl only)
                    if (t is ClassDecl classDecl && classDecl.DirectSuperclassName != null
                        && classDecl.DirectSuperclassName.Contains("._"))
                    {
                        structurallyRequired.Add(classDecl.DirectSuperclassName);
                    }

                    // Protocol conformance references
                    var conformances = t switch
                    {
                        ClassDecl cd => cd.Conformances,
                        StructDecl sd => sd.Conformances,
                        EnumDecl ed => ed.Conformances,
                        _ => null
                    };
                    if (conformances != null)
                    {
                        foreach (var conf in conformances)
                        {
                            var protoName = conf.Protocol.ToString();
                            if (protoName.Contains("._"))
                            {
                                structurallyRequired.Add(protoName);
                            }
                        }
                    }
                }

                // Recurse into nested types
                CollectUnderscoreTypeNames(t.Types, underscoreTypes, structurallyRequired);
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
            ICommandRunner? commandRunner = null,
            PlatformInfo? platformInfo = null)
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
                string? depAbiJsonPath;
                string? depTbdPath;

                try
                {
                    var primaryDepResolution = XCFrameworkResolver.Resolve(
                        depPath, Path.GetTempPath(),
                        primaryPlatformTarget, logger, commandRunner, platformInfo: platformInfo);
                    moduleName = primaryDepResolution.ModuleName;
                    depDylibPath = primaryDepResolution.DylibPath;
                    depAbiJsonPath = primaryDepResolution.AbiJsonPath;
                    depTbdPath = primaryDepResolution.TbdPath;

                    if (primaryDepResolution.IsSimulatorSlice)
                        simSearchPath = primaryDepResolution.FrameworkSearchPath;
                    else
                        deviceSearchPath = primaryDepResolution.FrameworkSearchPath;
                }
                catch (Exception ex) when (ex is SwiftModuleNotFoundException or StaticLibraryException)
                {
                    // Attempt ObjC-only framework fallback — resolves search path + validates modulemap
                    var objcResolution = XCFrameworkResolver.ResolveObjCFramework(
                        depPath, primaryPlatformTarget, logger, platformInfo: platformInfo);

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
                            depPath, oppositeTarget, logger, platformInfo: platformInfo);
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
                            depPath, XCFrameworkPlatformTarget.Device, logger, platformInfo: platformInfo);
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
                            depPath, XCFrameworkPlatformTarget.Simulator, logger, platformInfo: platformInfo);
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
                            oppositeTarget, logger, commandRunner, platformInfo: platformInfo);

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
                            XCFrameworkPlatformTarget.Device, logger, commandRunner, platformInfo: platformInfo);
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
                            XCFrameworkPlatformTarget.Simulator, logger, commandRunner, platformInfo: platformInfo);
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
                    DylibPath = depDylibPath,
                    AbiJsonPath = depAbiJsonPath,
                    TbdPath = depTbdPath
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
                      "Generated C# references the wrapper library but no compiled wrapper exists. " +
                      "Common causes: missing dependency framework (use --framework-dependency or <SwiftFrameworkDependency>), " +
                      "or internal types in the library's API. See Troubleshooting docs for details."
                    : $"All Swift wrapper code was stripped as broken ({compilationResult?.StrippedBlockCount ?? 0} block(s)). " +
                      "Generated C# references the wrapper library but no compiled wrapper exists. " +
                      "Use --async-library explicitly or report this as a generator bug.";
                return (1, null, message);
            }

            if (rawOutcome == WrapperCompilationOutcome.Fatal && sdkMode)
            {
                // Downgraded from Fatal → Warning in SDK mode
                const string actionableHint =
                    " Common causes: missing dependency framework (use --framework-dependency or <SwiftFrameworkDependency>), " +
                    "or internal types in the library's API. See Troubleshooting docs for details.";
                var message = compilationException != null
                    ? $"SWIFTBIND050: Swift wrapper compilation failed: {compilationException.Message}. " +
                      "C# bindings are still valid — wrapper-dependent methods will throw DllNotFoundException at runtime." +
                      actionableHint
                    : $"SWIFTBIND050: All Swift wrapper code was stripped as broken ({compilationResult?.StrippedBlockCount ?? 0} block(s)). " +
                      "C# bindings are still valid — wrapper-dependent methods will throw DllNotFoundException at runtime." +
                      actionableHint;
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

        /// <summary>
        /// Formats a SWIFTBIND060 dependency warning message with actionable guidance.
        /// </summary>
        /// <param name="frameworkName">The dependency framework name.</param>
        /// <param name="unresolvedReason">The reason: "missing-slice" or "missing-xcframework".</param>
        internal static string FormatDependencyWarning(string frameworkName, string unresolvedReason)
        {
            if (unresolvedReason == "missing-slice")
            {
                return $"SWIFTBIND060: Detected dependency '{frameworkName}' but its xcframework " +
                    "lacks the required platform slice. " +
                    "Use --framework-dependency to specify a complete xcframework. " +
                    "Verify the dependency xcframework contains both device and simulator slices.";
            }
            else
            {
                return $"SWIFTBIND060: Detected dependency '{frameworkName}' but no matching " +
                    $"{frameworkName}.xcframework found. " +
                    "Use --framework-dependency to specify its location. " +
                    "You may need to build the dependency separately or obtain it from the library author.";
            }
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
        /// Scans generated C# files for public type declarations to support mixed framework dedup.
        /// Returns the set of type names emitted by the Swift pipeline.
        /// </summary>
        internal static HashSet<string> CollectSwiftEmittedTypeNames(string outputDirectory)
        {
            var typeNames = new HashSet<string>(StringComparer.Ordinal);
            if (!Directory.Exists(outputDirectory))
                return typeNames;

            // Match: public [unsafe] class|struct|enum|interface NAME
            var pattern = new System.Text.RegularExpressions.Regex(
                @"^\s*public\s+(?:unsafe\s+)?(?:partial\s+)?(?:class|struct|enum|interface)\s+(\w+)",
                System.Text.RegularExpressions.RegexOptions.Multiline);

            foreach (var csFile in Directory.GetFiles(outputDirectory, "*.cs"))
            {
                var content = File.ReadAllText(csFile);
                foreach (System.Text.RegularExpressions.Match match in pattern.Matches(content))
                {
                    typeNames.Add(match.Groups[1].Value);
                }
            }

            return typeNames;
        }

        /// <summary>
        /// Lightweight peek at the moduleName attribute of a module database XML file.
        /// Returns null if the file is malformed or missing the expected structure.
        /// </summary>
        internal static string? PeekModuleNameFromXml(string path)
        {
            try
            {
                using var reader = System.Xml.XmlReader.Create(path);
                while (reader.Read())
                {
                    if (reader.NodeType == System.Xml.XmlNodeType.Element && reader.Name == "swifttypedatabase")
                    {
                        var moduleName = reader.GetAttribute("moduleName");
                        return string.IsNullOrWhiteSpace(moduleName) ? null : moduleName;
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Lightweight peek at the module name from an ABI JSON file.
        /// Returns null if the file cannot be parsed.
        /// </summary>
        private static string? PeekModuleNameFromAbiJson(string abiPath)
        {
            try
            {
                var text = File.ReadAllText(abiPath);
                var json = JObject.Parse(text);
                var rootNode = json["ABIRoot"];
                if (rootNode == null) return null;
                var children = rootNode["children"] as JArray;
                if (children == null || children.Count == 0) return null;
                var moduleName = children[0]?.Value<string>("moduleName");
                return string.IsNullOrEmpty(moduleName) || moduleName == "NO_MODULE" ? null : moduleName;
            }
            catch
            {
                return null;
            }
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
