// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CommandLine.Invocation;
using Microsoft.Extensions.Logging;
using BindingsGeneration.ObjC;

namespace BindingsGeneration;

/// <summary>
/// Handles the main CLI command execution for the bindings generator.
/// Extracted from Program.cs to separate handler logic from option definitions and utility methods.
/// </summary>
public static class BindingsGeneratorCommand
{
    /// <summary>
    /// Main command handler. Parses options, validates inputs, and orchestrates the binding generation pipeline.
    /// </summary>
    public static void Execute(InvocationContext context, CliOptions options)
    {
        var parseResult = context.ParseResult;
        var swiftAbiPath = parseResult.GetValueForOption(options.SwiftAbi);
        var dylibPath = parseResult.GetValueForOption(options.Dylib);
        var tbdPath = parseResult.GetValueForOption(options.Tbd);
        var outputDirectory = parseResult.GetValueForOption(options.OutputDirectory);
        var xcframeworkPath = parseResult.GetValueForOption(options.XCFramework);
        var platformStr = parseResult.GetValueForOption(options.Platform);
        var platformTargetStr = parseResult.GetValueForOption(options.PlatformTarget);
        var libraryName = parseResult.GetValueForOption(options.LibraryName);
        var asyncLibrary = parseResult.GetValueForOption(options.AsyncLibrary);
        var swiftInterface = parseResult.GetValueForOption(options.SwiftInterface);
        var symbolGraph = parseResult.GetValueForOption(options.SymbolGraph);
        var noDocs = parseResult.GetValueForOption(options.NoDocs);
        var bridgeHints = parseResult.GetValueForOption(options.BridgeHints);
        var namespacePattern = parseResult.GetValueForOption(options.NamespacePattern);
        var sdkMode = parseResult.GetValueForOption(options.SdkMode);
        var packageId = parseResult.GetValueForOption(options.PackageId);
        var wrapperArchitectures = parseResult.GetValueForOption(options.WrapperArchitectures);
        var frameworkDependencies = parseResult.GetValueForOption(options.FrameworkDependency);
        var moduleDatabases = parseResult.GetValueForOption(options.ModuleDatabase);
        var noAutoDetect = parseResult.GetValueForOption(options.NoAutoDetect);
        var objcForced = parseResult.GetValueForOption(options.ObjC);
        var skipWrapperCompilation = parseResult.GetValueForOption(options.SkipWrapperCompilation);
        var skipThunkCompilation = parseResult.GetValueForOption(options.SkipThunkCompilation);
        var compileWrapperOnly = parseResult.GetValueForOption(options.CompileWrapperOnly);
        var compileBridgeOnly = parseResult.GetValueForOption(options.CompileBridgeOnly);
        var configPath = parseResult.GetValueForOption(options.Config);
        var verbose = parseResult.GetValueForOption(options.Verbose);
        var help = parseResult.GetValueForOption(options.Help);

        if (help)
        {
            PrintHelp();
            return;
        }

        ILoggerFactory loggerFactory = BindingsGenerator.CreateLoggerFactory(verbose);
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
            context.ExitCode = BindingsGenerator.RunCompileWrapperOnly(
                xcframeworkPath!, outputDirectory, platformStr, platformTargetStr,
                wrapperArchitectures, frameworkDependencies, logger, platformInfo,
                skipThunkCompilation);
            return;
        }

        // Handle --compile-bridge-only: fast path that compiles only bridge .swift files
        if (compileBridgeOnly)
        {
            if (string.IsNullOrWhiteSpace(xcframeworkPath))
            {
                logger.LogError("Error: --compile-bridge-only requires --xcframework.");
                context.ExitCode = 1;
                return;
            }
            context.ExitCode = BindingsGenerator.RunCompileBridgeOnly(
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
                BindingsGenerator.ShouldCompileWrapper(resolution.IsSimulatorSlice, wrapperArchEarly, platformInfo);
            if (!shouldCompileWrapper)
            {
                logger.LogInformation(
                    "Swift wrapper compilation requires a simulator slice or --wrapper-architectures device/all. " +
                    "Pass --async-library manually for device-only builds without wrapper compilation.");
            }

            // Auto-set --async-library whenever wrapper will be compiled (now or deferred).
            // When --skip-wrapper-compilation is used, the wrapper is compiled later by
            // _CompileSwiftWrapper, but C# generation still needs the module name for DllImport.
            var wouldCompileWrapper = BindingsGenerator.ShouldCompileWrapper(resolution.IsSimulatorSlice, wrapperArchEarly, platformInfo);
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
                        BindingsGenerator.FormatDependencyWarning(unresolved.FrameworkName, unresolved.UnresolvedReason ?? "missing-xcframework"));
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

            resolvedDependencies = BindingsGenerator.ResolveFrameworkDependencies(
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
        var effectiveNamespacePattern = BindingsGenerator.ResolveNamespacePattern(namespacePattern, configPath, logger);

        // Auto-extract symbol graph for doc comments (xcframework mode only)
        symbolGraph = BindingsGenerator.ResolveSymbolGraphPath(symbolGraph, noDocs, resolution, outputDirectory, logger, platformInfo: platformInfo);

        var depModuleNames = resolvedDependencies?
            .Where(d => !d.IsObjCOnly)
            .Select(d => d.ModuleName)
            .ToList();
        var success = BindingsGenerator.GenerateBindings(swiftAbiPath, dylibPath, tbdPath, outputDirectory, runtimeLibraryName, asyncLibrary, swiftInterface, symbolGraph, bridgeHints, effectiveNamespacePattern, logger, loggerFactory, out var internalTypeNames, out var moduleNameForCollision, out var nestedTypesInCollidingClass, dependencyModuleNames: depModuleNames, moduleDatabasePaths: moduleDatabases, resolvedDependencies: resolvedDependencies, platform: platformInfo.Platform);
        if (!success)
        {
            context.ExitCode = 1;
            return;
        }

        // Persist wrapper compilation context for --compile-wrapper-only mode.
        if (hasXcframework)
        {
            BindingsGenerator.SaveWrapperContext(outputDirectory, internalTypeNames, moduleNameForCollision, nestedTypesInCollidingClass, logger);
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
                        skipThunkCompilation: skipThunkCompilation,
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
                        swiftInterfacePath: deviceOnlyResolution.SwiftInterfacePath,
                        skipThunkCompilation: skipThunkCompilation);
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
                        swiftInterfacePath: resolution.SwiftInterfacePath,
                        skipThunkCompilation: skipThunkCompilation,
                        resolvedArchitecture: resolution.SelectedArchitecture);
                }
            }
            catch (Exception ex)
            {
                compilationException = ex;
            }

            var rawOutcome = SwiftWrapperCompiler.EvaluateResult(
                compilationResult, asyncLibraryAutoWired, compilationException);
            var (outcomeExitCode, diagnosticCode, outcomeMessage) =
                BindingsGenerator.HandleWrapperCompilationOutcome(rawOutcome, sdkMode, compilationException, compilationResult);

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

            // Co-gate C# bindings: suppress members targeting stripped wrapper symbols
            if (compilationResult?.StrippedSymbols.Count > 0)
            {
                var coGated = CSharpWrapperCoGater.ProcessDirectory(
                    outputDirectory, compilationResult.StrippedSymbols, logger);
                if (coGated > 0)
                    logger.LogInformation("Suppressed {Count} C# member(s) targeting stripped wrapper symbols.", coGated);
            }
        }

        // Run mixed framework ObjC pipeline (after Swift bindings generated, before project emission)
        ObjCPipelineResult? mixedObjcResult = null;
        if (hasXcframework && resolution != null && mixedObjcResolution != null)
        {
            var swiftTypeNames = BindingsGenerator.CollectSwiftEmittedTypeNames(outputDirectory);
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
                bool isMixed = mixedObjcResult?.ExitCode == 0
                    && (mixedObjcResult.Module?.Classes.Count > 0
                        || mixedObjcResult.Module?.Protocols.Count > 0
                        || mixedObjcResult.Module?.Categories.Count > 0);
                string? objcProjFileName = isMixed
                    ? Path.GetFileName(mixedObjcResult!.ProjectPath!)
                    : null;

                // Detect bridge Swift files for metadata
                var bridgeFiles = SwiftWrapperCompiler.CollectBridgeSwiftFiles(outputDirectory);
                var hasBridgeSwift = bridgeFiles.Count > 0;
                var bridgeModuleName = $"{resolution.ModuleName}Bridge";

                // Always emit metadata props (used by SDK and standalone)
                XCFrameworkMetadataExtractor.EmitMetadataProps(
                    metadata, outputDirectory, hasWrapperXcfw,
                    wrapperModuleName,
                    compilationResult?.SliceCount ?? 0, logger,
                    resolvedDependencies,
                    frameworkType: isMixed ? "Mixed" : "Swift",
                    objcProjectName: objcProjFileName,
                    platformInfo: platformInfo,
                    hasBridgeSwift: hasBridgeSwift,
                    bridgeModuleName: bridgeModuleName);

                // Only emit .csproj in non-SDK mode
                if (!sdkMode)
                {
                    var projectFrameworkName = BindingsGenerator.InferFrameworkName(resolution.DylibPath, resolution.ModuleName);
                    var projectResolver = new NamespacePatternResolver(effectiveNamespacePattern, projectFrameworkName);
                    var bridgeXcfwPath = Path.Combine(outputDirectory, $"{bridgeModuleName}.xcframework");
                    var hasBridgeXcfw = Directory.Exists(bridgeXcfwPath);
                    BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                    {
                        OutputDirectory = outputDirectory,
                        ModuleName = resolution.ModuleName,
                        Metadata = metadata,
                        SourceXCFrameworkPath = resolution.XCFrameworkPath,
                        WrapperXCFrameworkPath = hasWrapperXcfw ? wrapperXcfwPath : null,
                        BridgeXCFrameworkPath = hasBridgeXcfw ? bridgeXcfwPath : null,
                        HasBridgeSwift = hasBridgeSwift,
                        Dependencies = resolvedDependencies,
                        ResolvedNamespace = projectResolver.ResolveNamespace(resolution.ModuleName),
                        ObjCProjectFileName = objcProjFileName,
                        PlatformInfo = platformInfo,
                    }, logger);
                }

                // Note: HasBridgeXCFramework is set to hasBridgeSwift (not hasBridgeXcfw)
                // because the bridge xcframework doesn't exist at generation time in SDK mode.
                // The consumer targets emit conditional NativeReference with Exists() checks,
                // so it's safe to include the reference even if compilation happens later.
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = outputDirectory,
                    ModuleName = resolution.ModuleName,
                    PackageId = effectivePackageId,
                    EffectiveMinimumOSVersion = metadata.EffectiveMinimumOSVersion,
                    HasWrapperXCFramework = hasWrapperXcfw,
                    HasBridgeXCFramework = hasBridgeSwift,
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
    }

    private static void PrintHelp()
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
        Console.WriteLine("  --skip-thunk-compilation    Optional. Skip native thunk assembly compilation.");
        Console.WriteLine("  --compile-wrapper-only      Optional. Compile existing .swift wrapper files only (no parsing/generation).");
        Console.WriteLine($"  --config             Optional. Path to config file. Default: {BindingsGenerator.DefaultConfigFileName}");
        Console.WriteLine("  -v, --verbose        Verbosity level. 0 = No logging, 1 = General information, 2 = Debugging information. (default: 1)");
    }
}
