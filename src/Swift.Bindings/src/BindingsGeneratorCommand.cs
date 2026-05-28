// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CommandLine.Invocation;
using System.Linq;
using Microsoft.Extensions.Logging;
using BindingsGeneration.ObjC;
using BindingsGeneration.Producers;

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
        var platformVersionOverride = parseResult.GetValueForOption(options.PlatformVersion);
        var platformTargetStr = parseResult.GetValueForOption(options.PlatformTarget);
        var libraryName = parseResult.GetValueForOption(options.LibraryName);
        var asyncLibrary = parseResult.GetValueForOption(options.AsyncLibrary);
        // Strip the documented "\@" CLI escape: System.CommandLine treats "@filename" as a
        // response-file reference, so users escape "@rpath/..." values as "\@rpath/...". The
        // backslash is a CLI-parsing artifact and must not survive into the C# string literal
        // emitted in DllImport (which would produce CS1009 — invalid escape sequence).
        libraryName = StripCliAtEscape(libraryName);
        asyncLibrary = StripCliAtEscape(asyncLibrary);
        var swiftInterface = parseResult.GetValueForOption(options.SwiftInterface);
        var symbolGraph = parseResult.GetValueForOption(options.SymbolGraph);
        var noDocs = parseResult.GetValueForOption(options.NoDocs);
        var bridgeHints = parseResult.GetValueForOption(options.BridgeHints);
        var namespacePattern = parseResult.GetValueForOption(options.NamespacePattern);
        var sdkMode = parseResult.GetValueForOption(options.SdkMode);
        var packageId = parseResult.GetValueForOption(options.PackageId);
        var swiftRuntimeVersion = parseResult.GetValueForOption(options.SwiftRuntimeVersion);
        var wrapperArchitectures = parseResult.GetValueForOption(options.WrapperArchitectures);
        var targetArchitectures = parseResult.GetValueForOption(options.TargetArchitectures);
        var frameworkDependencies = parseResult.GetValueForOption(options.FrameworkDependency);
        var moduleDatabases = parseResult.GetValueForOption(options.ModuleDatabase);
        var noAutoDetect = parseResult.GetValueForOption(options.NoAutoDetect);
        var keepBuiltinDatabase = parseResult.GetValueForOption(options.KeepBuiltinDatabase);
        var objcForced = parseResult.GetValueForOption(options.ObjC);
        var skipWrapperCompilation = parseResult.GetValueForOption(options.SkipWrapperCompilation);
        var skipThunkCompilation = parseResult.GetValueForOption(options.SkipThunkCompilation);
        var compileWrapperOnly = parseResult.GetValueForOption(options.CompileWrapperOnly);
        var compileBridgeOnly = parseResult.GetValueForOption(options.CompileBridgeOnly);
        var detectAppleCrossModuleDeps = parseResult.GetValueForOption(options.DetectAppleCrossModuleDeps);
        var sliceXcframework = parseResult.GetValueForOption(options.SliceXcframework);
        var rid = parseResult.GetValueForOption(options.Rid);
        var emitAppleTypesManifest = parseResult.GetValueForOption(options.EmitAppleTypesManifest);
        var appleAbiJsonPaths = parseResult.GetValueForOption(options.AppleAbiJson);
        var appleIncludeTypes = parseResult.GetValueForOption(options.AppleIncludeTypes);
        var appleVersion = parseResult.GetValueForOption(options.AppleVersion) ?? "26.0.0";
        var appleSdkTrainMajorExplicit = parseResult.GetValueForOption(options.AppleSdkTrainMajor);
        var appleSdkTrainMajor = appleSdkTrainMajorExplicit ?? ParseAppleVersionMajor(appleVersion);
        var appleSdkTrainLabel = parseResult.GetValueForOption(options.AppleSdkTrainLabel);
        var appleSdkMinIos = parseResult.GetValueForOption(options.AppleSdkMinIos);
        var appleSdkMinMaccatalyst = parseResult.GetValueForOption(options.AppleSdkMinMaccatalyst);
        var appleSdkMinTvos = parseResult.GetValueForOption(options.AppleSdkMinTvos);
        var appleSdkMinMacos = parseResult.GetValueForOption(options.AppleSdkMinMacos);
        var emitAppleTypesCs = parseResult.GetValueForOption(options.EmitAppleTypesCs);
        var appleTypesManifestPath = parseResult.GetValueForOption(options.AppleTypesManifest);
        var appleTypesSequentialLayoutWhitelistPath = parseResult.GetValueForOption(options.AppleTypesSequentialLayoutWhitelist);
        var allowPartialAppleTypesManifest = parseResult.GetValueForOption(options.AllowPartialAppleTypesManifest);
        var validateAppleTypesManifest = parseResult.GetValueForOption(options.ValidateAppleTypesManifest);
        var appleTypesManifestWriteBack = parseResult.GetValueForOption(options.AppleTypesManifestWriteBack);
        var appleSupplementPrototypeDir = parseResult.GetValueForOption(options.AppleSupplementPrototypeDir);
        var configPath = parseResult.GetValueForOption(options.Config);
        var interfaceFactsProducer = parseResult.GetValueForOption(options.InterfaceFactsProducer) ?? "auto";
        var verbose = parseResult.GetValueForOption(options.Verbose);
        var help = parseResult.GetValueForOption(options.Help);

        if (help)
        {
            PrintHelp();
            return;
        }

        ILoggerFactory loggerFactory = BindingsGenerator.CreateLoggerFactory(verbose);
        ILogger logger = loggerFactory.CreateLogger<BindingsGenerator>();

        // Handle --detect-apple-cross-module-deps: parse a .swiftinterface file's `import`
        // lines, resolve them against AppleFrameworkRegistry's packageId map, and write
        // pipe-delimited dep edges to stdout for the apple-framework-mode SDK target's
        // PackageReference auto-injection. Out-of-tree from the binding-generation
        // pipeline — never consumes dylib/TBD, so it must run BEFORE --platform validation.
        if (!string.IsNullOrWhiteSpace(detectAppleCrossModuleDeps))
        {
            try
            {
                var swiftInterfacePath = detectAppleCrossModuleDeps!;
                var currentModule = DeriveModuleNameFromSwiftInterfacePath(swiftInterfacePath);
                if (string.IsNullOrEmpty(currentModule))
                {
                    logger.LogError(
                        "Error: --detect-apple-cross-module-deps could not derive a module name from path '{Path}'. " +
                        "Expected a path under '<Module>.swiftmodule/'.",
                        swiftInterfacePath);
                    context.ExitCode = 1;
                    return;
                }
                var deps = AppleFrameworkImportDetector.Detect(swiftInterfacePath, currentModule, appleVersion);
                foreach (var dep in deps)
                {
                    Console.Out.WriteLine($"{dep.ModuleName}|{dep.PackageId}|{dep.VersionRange}");
                }
                context.ExitCode = 0;
            }
            catch (FileNotFoundException ex)
            {
                logger.LogError("Error: {Message}", ex.Message);
                context.ExitCode = 1;
            }
            catch (ArgumentException ex)
            {
                logger.LogError("Error: {Message}", ex.Message);
                context.ExitCode = 1;
            }
            return;
        }

        // Handle --slice-xcframework: stage a sliced copy of a source xcframework for a given
        // NuGet RID. Out-of-tree from the binding-generation pipeline — this mode never
        // consumes ABI JSON / dylib / TBD, so it must run BEFORE --platform validation.
        if (sliceXcframework)
        {
            if (string.IsNullOrWhiteSpace(xcframeworkPath))
            {
                logger.LogError("Error: --slice-xcframework requires --xcframework <source-xcframework>.");
                context.ExitCode = 1;
                return;
            }
            if (string.IsNullOrWhiteSpace(rid))
            {
                logger.LogError("Error: --slice-xcframework requires --rid <nuget-rid> (one of: {Rids}).",
                    string.Join(", ", XCFrameworkSlicer.SupportedRids));
                context.ExitCode = 1;
                return;
            }
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                logger.LogError("Error: --slice-xcframework requires -o <staged-xcframework-dir>.");
                context.ExitCode = 1;
                return;
            }
            try
            {
                XCFrameworkSlicer.Slice(xcframeworkPath!, rid!, outputDirectory!, logger);
                context.ExitCode = 0;
            }
            catch (Exception ex)
            {
                logger.LogError("{Message}", ex.Message);
                context.ExitCode = 1;
            }
            return;
        }

        // Handle --emit-apple-types-manifest: fast path that ingests Apple ABI JSON dumps
        // and writes the SwiftBindings.Apple type metadata manifest. Out-of-tree from the
        // binding-generation pipeline — this mode never consumes dylib/TBD/swiftinterface,
        // so it must run BEFORE --platform / --platform-version validation (those flags
        // are unrelated to manifest emission).
        if (emitAppleTypesManifest)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                logger.LogError("Error: --emit-apple-types-manifest requires -o <output-manifest.json>.");
                context.ExitCode = 1;
                return;
            }
            var platforms = new AppleTypesManifest.Availability
            {
                Ios = appleSdkMinIos,
                Maccatalyst = appleSdkMinMaccatalyst,
                Tvos = appleSdkMinTvos,
                Macos = appleSdkMinMacos,
            };
            context.ExitCode = AppleTypesManifest.AppleTypesManifestCommand.Run(
                appleAbiJsonPaths ?? Array.Empty<string>(),
                appleIncludeTypes,
                outputDirectory!,
                appleSdkTrainMajor,
                appleSdkTrainLabel,
                platforms,
                generatedBy: null,
                allowPartial: allowPartialAppleTypesManifest,
                logger);
            return;
        }

        // Handle --emit-apple-types-cs: second stage of the Apple-types pipeline. Reads the
        // manifest emitted by --emit-apple-types-manifest plus an optional sequential-layout
        // whitelist and writes C# source into -o. Out-of-tree from the binding-generation
        // pipeline — must run BEFORE --platform validation, same as the manifest mode.
        if (emitAppleTypesCs)
        {
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                logger.LogError("Error: --emit-apple-types-cs requires -o <output-dir>.");
                context.ExitCode = 1;
                return;
            }
            context.ExitCode = AppleTypesManifest.AppleTypesCsCommand.Run(
                appleTypesManifestPath ?? string.Empty,
                appleTypesSequentialLayoutWhitelistPath,
                outputDirectory!,
                logger);
            return;
        }

        // Handle --validate-apple-types-manifest: live-SDK CI validator (Phase 2 / M10).
        // Probes every manifest entry advertised on the host platform via dlsym +
        // CallConvSwift accessor invocation, reads VWT size/alignment/stride, and either
        // detects drift vs. the manifest or writes the probed values back in place.
        // Out-of-tree from the binding-generation pipeline — must run BEFORE --platform
        // validation, same as the other Apple-types modes.
        if (validateAppleTypesManifest)
        {
            context.ExitCode = AppleTypesManifest.AppleTypesManifestValidateCommand.Run(
                appleTypesManifestPath ?? string.Empty,
                appleTypesManifestWriteBack,
                logger);
            return;
        }

        // Parse and validate --platform
        var parsedPlatform = PlatformInfoFactory.ParsePlatform(platformStr);
        if (parsedPlatform == null)
        {
            logger.LogError("Error: Invalid --platform '{Value}'. Valid values: 'ios', 'macos', 'tvos', 'maccatalyst'.", platformStr);
            context.ExitCode = 1;
            return;
        }

        // Reject malformed --platform-version up front. The override flows straight into
        // <TargetFramework>net10.0-ios{value}</TargetFramework> and the buildTransitive/
        // pack path; a typo like "26.two" or an unwanted pre-release tail like
        // "26.2-preview" would only fail later with an opaque MSBuild/NuGet error that
        // gives the user no pointer back at the flag they typed.
        if (!string.IsNullOrWhiteSpace(platformVersionOverride) &&
            !IsValidPlatformVersion(platformVersionOverride))
        {
            logger.LogError(
                "Error: Invalid --platform-version '{Value}'. Expected '<major>.<minor>' (e.g. '26.0', '26.2').",
                platformVersionOverride);
            context.ExitCode = 1;
            return;
        }

        var platformInfo = PlatformInfoFactory.Create(parsedPlatform.Value, platformVersionOverride);

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
                skipThunkCompilation, targetArchitectures);
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

        // Enforce --platform-version on the publishable path. The emitted csproj is
        // packable iff --swift-runtime-version is set to a non-sentinel value (see
        // BindingProjectEmitter's isPackable gate). On that path the TFM MUST be explicit
        // — silently falling back to PlatformInfo.DefaultPlatformVersion would label the
        // published nupkg for the wrong SDK cut, and the failure mode would only show up
        // later as a NETSDK1005 / TPV mismatch on whoever consumes it.
        //
        // Positioned AFTER the --compile-wrapper-only / --compile-bridge-only fast paths
        // because those modes never emit or pack a csproj — `swiftRuntimeVersion` is a
        // no-op on those branches and demanding `--platform-version` would be a false
        // positive. Catching it here still fires before any binding-emit work.
        if (RequiresExplicitPlatformVersion(swiftRuntimeVersion) &&
            string.IsNullOrWhiteSpace(platformVersionOverride))
        {
            logger.LogError(
                "Error: --platform-version is required when --swift-runtime-version is set to a published " +
                "value (got --swift-runtime-version '{Value}'). Pass --platform-version <major.minor> " +
                "(e.g. '26.2' for net10.0-ios26.2) so the emitted csproj is labeled for an explicit Apple " +
                "workload version instead of silently inheriting the in-tree default.",
                swiftRuntimeVersion);
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
        XCFrameworkResolver.ObjCFrameworkResolution? mixedObjcResolution = null;
        var shouldCompileWrapper = false;
        // "A wrapper xcframework will exist" — true even under --skip-wrapper-compilation,
        // because the SDK's _CompileSwiftWrapper target compiles + packs it in a later pass.
        // Distinct from shouldCompileWrapper (compile *now*) and hasWrapperXcfw (exists *now*).
        var wouldCompileWrapper = false;
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
            wouldCompileWrapper = BindingsGenerator.ShouldCompileWrapper(resolution.IsSimulatorSlice, wrapperArchEarly, platformInfo);
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

        // Direct/manual-mode wrapper-library default. xcframework mode auto-wires
        // --async-library above (line ~270) when wrapper compilation is gated on.
        // Direct mode has no equivalent auto-wire, so SBW_ wrapper-helper P/Invokes
        // would otherwise default to the module library path — which, when -l points
        // at a system framework like StoreKit, is the system framework itself. The
        // helpers do not exist there, so the validator emits SWIFTBIND093 (CC-003)
        // and any runtime call would EntryPointNotFound. Default to the conventional
        // "{Module}SwiftBindings" name (matching xcframework mode's behavior — see
        // NukeSwiftBindings, etc.) so the binding correctly expresses its intent to
        // call into a wrapper dylib. Producing that dylib is a separate concern;
        // this default only fixes the binding's contract, not its deployability.
        // Hoisted out of the asyncLibrary fall-through so direct-mode wrapper
        // compilation and csproj emission can reuse the peeked module name without
        // re-reading the abi.json.
        string? directModuleName = null;
        if (!hasXcframework)
        {
            directModuleName = BindingsGenerator.PeekModuleNameFromAbiJson(swiftAbiPath);
            if (string.IsNullOrWhiteSpace(asyncLibrary) && !string.IsNullOrEmpty(directModuleName))
            {
                asyncLibrary = $"{directModuleName}SwiftBindings";
                logger.LogInformation(
                    "Direct mode: defaulting --async-library to '{Library}'. " +
                    "Pass --async-library explicitly to override.",
                    asyncLibrary);
            }
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

        var depModuleNames = GetDependencyModuleNamesForSwiftImports(resolvedDependencies);
        var factsAggregator = BuildInterfaceFactsAggregator(interfaceFactsProducer, logger);
        var success = BindingsGenerator.GenerateBindings(swiftAbiPath, dylibPath, tbdPath, outputDirectory, runtimeLibraryName, asyncLibrary, swiftInterface, symbolGraph, bridgeHints, effectiveNamespacePattern, logger, loggerFactory, out var internalTypeNames, out var moduleNameForCollision, out var nestedTypesInCollidingClass, out var depModuleCollisions, dependencyModuleNames: depModuleNames, moduleDatabasePaths: moduleDatabases, resolvedDependencies: resolvedDependencies, platform: platformInfo.Platform, keepBuiltinDatabaseForTargetModule: keepBuiltinDatabase, factsAggregator: factsAggregator);
        if (!success)
        {
            context.ExitCode = 1;
            return;
        }

        // Persist wrapper compilation context for --compile-wrapper-only mode.
        if (hasXcframework)
        {
            BindingsGenerator.SaveWrapperContext(outputDirectory, internalTypeNames, moduleNameForCollision, nestedTypesInCollidingClass, depModuleCollisions, logger);
        }

        // Validate --wrapper-architectures
        var wrapperArchNormalized = wrapperArchitectures?.ToLowerInvariant() ?? "simulator";
        if (wrapperArchNormalized != "simulator" && wrapperArchNormalized != "device" && wrapperArchNormalized != "all")
        {
            logger.LogError("Error: Invalid --wrapper-architectures '{Value}'. Valid values: 'simulator', 'device', 'all'.", wrapperArchitectures);
            context.ExitCode = 1;
            return;
        }

        // Detect system-framework intent in direct mode. The new direct-mode wrapper-compile
        // and csproj-emit branches assume an Apple SDK framework whose binary lives on-device
        // under /System/Library/Frameworks/ and resolves at runtime via dyld @rpath. That
        // assumption only holds when -l explicitly points there. For non-system manual
        // workflows (a local third-party .framework, a custom dylib path), preserve the
        // pre-existing behavior: emit C# bindings + Wrapper.swift only, no auto wrapper
        // compilation, no csproj — the user owns the build harness in that case.
        var isSystemFrameworkTarget = IsSystemFrameworkTarget(hasXcframework, libraryName);

        // Direct/system-framework mode: wrapper compilation is gated only on
        // --skip-wrapper-compilation. There is no slice-availability check (no xcframework).
        // Multi-arch (`all`) requires both simulator and device swiftinterfaces; the direct
        // CLI only accepts a single -s, so reject `all` here with a clear error rather than
        // silently producing one slice.
        if (isSystemFrameworkTarget)
        {
            if (wrapperArchNormalized == "all")
            {
                logger.LogError(
                    "Error: --wrapper-architectures all is not supported in direct mode. " +
                    "Pass 'simulator' or 'device' (default: simulator) and rerun the generator " +
                    "once per slice with the matching swiftinterface (-s).");
                context.ExitCode = 1;
                return;
            }
            shouldCompileWrapper = !skipWrapperCompilation;
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

            // CPU target arch(es) — mirrors the --compile-wrapper-only fast path so the standalone
            // generation path honors --target-architectures identically: "auto" matches the source
            // slice's coverage (fat iff the source is fat), an explicit list fails loud (SWIFTBIND052)
            // on a missing arch, and empty/unset keeps the historical single arm64-preference pass.
            var autoMatchSource = string.Equals(targetArchitectures?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
            List<string> requestedArchs;
            if (autoMatchSource)
            {
                requestedArchs = new List<string>();
            }
            else
            {
                var parsed = BindingsGenerator.ParseTargetArchitectures(targetArchitectures, logger);
                if (parsed == null)
                {
                    context.ExitCode = 1; // invalid arch token already logged
                    return;
                }
                requestedArchs = parsed;
            }

            var (autoBasisArchs, autoBasisSliceId) = BindingsGenerator.ResolveAutoArchBasis(
                resolution, xcframeworkPath!, outputDirectory, platformTarget, wrapperArchNormalized,
                platformInfo, logger);
            if (!BindingsGenerator.TryDecideWrapperArchitectures(
                    autoMatchSource, requestedArchs, autoBasisArchs, autoBasisSliceId,
                    logger, out var primaryArch, out var extraArchs))
            {
                context.ExitCode = 1; // explicit arch missing from source — already logged (SWIFTBIND052)
                return;
            }

            // Compiles the wrapper for ONE requested CPU arch (null = historical arm64 preference).
            // Re-resolves per arch so the right per-arch .swiftinterface/abi is used; folded into a fat
            // build by CompileWrapperForArchitectures when extraArchs is non-empty.
            SwiftWrapperCompilationResult? CompileForArch(string? requestedArch)
            {
                if (wrapperArchNormalized == "all")
                {
                    // Multi-arch: resolve both slices, compile wrapper for both
                    var (simResolution, deviceResolution) = XCFrameworkResolver.ResolveAll(
                        xcframeworkPath!, outputDirectory, logger, platformInfo: platformInfo,
                        requestedArchitecture: requestedArch);

                    if (deviceResolution == null)
                    {
                        logger.LogWarning(
                            "Source xcframework has no device slice; wrapper will contain simulator slice only.");
                    }

                    return SwiftWrapperCompiler.CompileAll(
                        outputDirectory, resolution.ModuleName,
                        simResolution, deviceResolution, logger,
                        internalTypeNames: internalTypeNames,
                        simAdditionalSearchPaths: simDepPaths,
                        deviceAdditionalSearchPaths: deviceDepPaths,
                        skipThunkCompilation: skipThunkCompilation,
                        platformInfo: platformInfo,
                        moduleNameForCollision: moduleNameForCollision,
                        nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                        swiftInterfacePath: simResolution.SwiftInterfacePath,
                        depModuleNamesForCollisionSimulator: depModuleCollisions.Simulator,
                        depModuleNamesForCollisionDevice: depModuleCollisions.Device);
                }
                else if (wrapperArchNormalized == "device")
                {
                    // Device-only: resolve device slice and compile for iphoneos. A resolve failure
                    // propagates to the outer try and is reported by WrapperBuildOutcome.
                    var deviceOnlyResolution = XCFrameworkResolver.Resolve(
                        xcframeworkPath!, outputDirectory,
                        XCFrameworkPlatformTarget.Device, logger, platformInfo: platformInfo,
                        requestedArchitecture: requestedArch);

                    return SwiftWrapperCompiler.CompileSlice(
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
                        skipThunkCompilation: skipThunkCompilation,
                        resolvedArchitecture: deviceOnlyResolution.SelectedArchitecture,
                        depModuleNamesForCollision: depModuleCollisions.Device);
                }
                else
                {
                    // Simulator-only (default)
                    var simResolution = XCFrameworkResolver.Resolve(
                        xcframeworkPath!, outputDirectory,
                        platformTarget, logger, platformInfo: platformInfo,
                        requestedArchitecture: requestedArch);

                    return SwiftWrapperCompiler.Compile(
                        outputDirectory, resolution.ModuleName,
                        simResolution.FrameworkSearchPath, simResolution.DylibPath, logger,
                        internalTypeNames: internalTypeNames,
                        additionalFrameworkSearchPaths: simDepPaths,
                        platformInfo: platformInfo,
                        moduleNameForCollision: moduleNameForCollision,
                        nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                        swiftInterfacePath: simResolution.SwiftInterfacePath,
                        skipThunkCompilation: skipThunkCompilation,
                        resolvedArchitecture: simResolution.SelectedArchitecture,
                        depModuleNamesForCollision: depModuleCollisions.Simulator);
                }
            }

            try
            {
                compilationResult = BindingsGenerator.CompileWrapperForArchitectures(
                    primaryArch, extraArchs, CompileForArch, logger);
            }
            catch (Exception ex)
            {
                compilationException = ex;
            }

            var outcome = WrapperBuildOutcome.From(
                compilationResult, asyncLibraryAutoWired, sdkMode, compilationException);
            outcome.LogTo(logger);
            if (outcome.IsFatal)
            {
                context.ExitCode = outcome.ExitCode;
                return;
            }

            IReadOnlyList<CoGatedMember> coGated = Array.Empty<CoGatedMember>();
            if (outcome.StrippedSymbols.Count > 0)
            {
                coGated = CSharpWrapperCoGater.ProcessDirectory(
                    outputDirectory, outcome.StrippedSymbols, logger);
                if (coGated.Count > 0)
                    logger.LogInformation("Suppressed {Count} C# member(s) targeting stripped wrapper symbols.", coGated.Count);
            }

            BindingArtifactManifestStore.ReadModifyWrite(
                outputDirectory,
                resolution.ModuleName,
                m => m.Wrapper = WrapperSection.From(outcome, coGated),
                logger);
        }
        else if (shouldCompileWrapper && isSystemFrameworkTarget && !string.IsNullOrEmpty(directModuleName))
        {
            // Direct-mode wrapper compilation. The xcframework branch above resolves
            // frameworkSearchPath/dylibPath from the xcframework slice; in direct mode
            // we synthesize them from the CLI's --tbd input. The TBD lives at
            //   <SDK>/.../<Module>.framework/<Module>.tbd
            // so the framework directory is the TBD's parent and the framework search
            // path (-F target) is the framework directory's parent. For Apple system
            // frameworks this is `<sdk>/System/Library/Frameworks`, which swiftc would
            // already have searched implicitly via -sdk — passing it explicitly is
            // harmless and works uniformly for any SDK-resident framework.
            var frameworkDir = Path.GetDirectoryName(tbdPath);
            var frameworkSearchPath = !string.IsNullOrEmpty(frameworkDir)
                ? Path.GetDirectoryName(frameworkDir)
                : null;
            if (string.IsNullOrEmpty(frameworkSearchPath))
            {
                logger.LogError(
                    "Direct mode: cannot derive framework search path from TBD '{Tbd}'. " +
                    "Expected layout: <SDK>/.../<Module>.framework/<Module>.tbd.",
                    tbdPath);
                context.ExitCode = 1;
                return;
            }

            // Resolve the slice variant to compile against. wrapperArchNormalized is
            // already validated above and `all` is rejected for direct mode, so we only
            // need the simulator/device choice. Both fall back to the device slice on
            // platforms with no simulator variant (macOS, Mac Catalyst).
            var directSlice = wrapperArchNormalized == "device"
                ? platformInfo.DeviceSlice
                : platformInfo.GetSlice(true);

            // Apple direct mode must share the wrapper CPU-arch decision with the xcframework
            // path (constraints.md "Wrapper CPU-arch decision is shared, not per-call-site").
            // Without this fanout the wrapper xcframework's simulator slice is arm64-only, so
            // an iossimulator-x64 / tvossimulator-x64 / osx-x64 / maccatalyst-x64 consumer
            // resolves NativeReference against a slice that doesn't advertise x86_64 and
            // dotnet-for-apple reports "No matching framework found … SupportedArchitectures: x86_64".
            // There is no source xcframework to inspect, so the "auto" basis is synthetic and
            // derived from PlatformInfo rather than the active compile slice (see
            // ResolveAppleFrameworkAutoArchBasis for the device-first explicit-fat rationale).
            var autoMatchSourceDirect = string.Equals(targetArchitectures?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
            List<string> requestedArchsDirect;
            if (autoMatchSourceDirect)
            {
                requestedArchsDirect = new List<string>();
            }
            else
            {
                var parsedDirect = BindingsGenerator.ParseTargetArchitectures(targetArchitectures, logger);
                if (parsedDirect == null)
                {
                    context.ExitCode = 1;
                    return;
                }
                requestedArchsDirect = parsedDirect;
            }
            var (directBasisArchs, directBasisSliceId) =
                BindingsGenerator.ResolveAppleFrameworkAutoArchBasis(platformInfo);
            if (!BindingsGenerator.TryDecideWrapperArchitectures(
                    autoMatchSourceDirect, requestedArchsDirect, directBasisArchs, directBasisSliceId,
                    logger, out var directPrimaryArch, out var directExtraArchs))
            {
                context.ExitCode = 1;
                return;
            }

            // The basis above reflects wrapper COVERAGE (sim slice arches), but the generator
            // only compiles the ACTIVE directSlice. Filter extras to arches the active slice can
            // natively compile — e.g. x86_64 against an iOS/tvOS device-first directSlice has no
            // valid swiftc target and would leave a malformed xcframework slice that breaks the
            // SDK's downstream xcodebuild -create-xcframework merge. The dropped arches are NOT
            // lost: the SDK's _AFW_OtherIsFatSim path packs the sim second slice and fat-folds
            // them in there. This keeps auto and explicit `arm64,x86_64` device-first producing
            // the same wrapper (arm64 device + fat sim) without the generator attempting an
            // impossible compile.
            var directSliceNaturalArchs = BindingsGenerator.GetAppleFrameworkSliceNaturalArchs(directSlice);
            var droppedExtras = directExtraArchs
                .Where(a => !directSliceNaturalArchs.Any(n => string.Equals(n, a, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (droppedExtras.Count > 0)
            {
                directExtraArchs = directExtraArchs
                    .Where(a => directSliceNaturalArchs.Any(n => string.Equals(n, a, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                logger.LogInformation(
                    "Apple direct: arches [{Deferred}] are not native to active compile slice '{Slice}' " +
                    "and are covered by the SDK's fat-sim second-slice path; skipping generator compile.",
                    string.Join("+", droppedExtras), directSlice.SliceId);
            }

            // Compiles the wrapper for ONE requested CPU arch. The shared
            // CompileWrapperForArchitectures folds extra arches via lipo (aside→merge→restore);
            // null primary keeps the historical default-slice arch (no WithArchitecture rename).
            SwiftWrapperCompilationResult? CompileDirectForArch(string? requestedArch) =>
                SwiftWrapperCompiler.CompileSlice(
                    outputDirectory, directModuleName,
                    frameworkSearchPath!, tbdPath,
                    string.IsNullOrEmpty(requestedArch) ? directSlice : directSlice.WithArchitecture(requestedArch),
                    logger,
                    internalTypeNames: internalTypeNames,
                    moduleNameForCollision: moduleNameForCollision,
                    nestedTypesInCollidingClass: nestedTypesInCollidingClass,
                    swiftInterfacePath: swiftInterface,
                    skipThunkCompilation: skipThunkCompilation);

            // ResolveDeploymentTarget reads <frameworkDir>/Info.plist for MinimumOSVersion;
            // Apple system frameworks ship one. dylibPath argument is the TBD path — the
            // wrapper compiler only uses it for the Info.plist read and a (no-op-on-text)
            // resource bundle scan. Passing tbdPath here matches what direct-mode users
            // already pass to -d at the CLI.
            SwiftWrapperCompilationResult? directResult = null;
            Exception? directException = null;
            try
            {
                directResult = BindingsGenerator.CompileWrapperForArchitectures(
                    directPrimaryArch, directExtraArchs, CompileDirectForArch, logger);
            }
            catch (Exception ex)
            {
                directException = ex;
            }

            // Direct mode never auto-wires --async-library inside the xcframework
            // helper, so failures are always treated as Warnings (not Fatal). Surface
            // them and continue — the C# bindings are still correct on disk and the
            // user can rerun with --skip-wrapper-compilation to bypass.
            var directOutcome = WrapperBuildOutcome.From(
                directResult, asyncLibraryAutoWired: false, sdkMode, directException);
            directOutcome.LogTo(logger);
            if (directOutcome.IsFatal)
            {
                context.ExitCode = directOutcome.ExitCode;
                return;
            }

            compilationResult = directResult;

            IReadOnlyList<CoGatedMember> directCoGated = Array.Empty<CoGatedMember>();
            if (directOutcome.StrippedSymbols.Count > 0)
            {
                directCoGated = CSharpWrapperCoGater.ProcessDirectory(
                    outputDirectory, directOutcome.StrippedSymbols, logger);
                if (directCoGated.Count > 0)
                    logger.LogInformation("Suppressed {Count} C# member(s) targeting stripped wrapper symbols.", directCoGated.Count);
            }

            BindingArtifactManifestStore.ReadModifyWrite(
                outputDirectory,
                directModuleName,
                m => m.Wrapper = WrapperSection.From(directOutcome, directCoGated),
                logger);
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

                // Apple-supplement prototype: materialized BEFORE metadata props emission so the
                // resulting csproj path flows into binding-metadata.props for the SDK's
                // _InjectAppleSupplementPrototype target to consume. No-op when the generator
                // didn't resolve any supplement types or the consumer didn't pass the flag.
                string? appleSupplementPrototypeCsproj = null;
                if (!string.IsNullOrWhiteSpace(appleSupplementPrototypeDir) && AppleSupplementReferences.Any)
                {
                    var protoResult = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                    {
                        PrototypeDirectory = appleSupplementPrototypeDir!,
                        ReferencedIdentities = AppleSupplementReferences.Current,
                        PlatformInfo = platformInfo,
                        SwiftRuntimeVersion = swiftRuntimeVersion,
                        MinimumOSVersion = metadata.EffectiveMinimumOSVersion,
                    }, logger);
                    appleSupplementPrototypeCsproj = protoResult.CsprojPath;
                }

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
                    bridgeModuleName: bridgeModuleName,
                    needsAppleSupplement: AppleSupplementReferences.Any,
                    appleSupplementVersion: appleVersion,
                    appleSupplementPrototypeCsprojPath: appleSupplementPrototypeCsproj);

                // Read resource bundle manifest (written by CreateResourceBundleStubs during compilation)
                var resourceBundleManifest = Path.Combine(outputDirectory, "_resource-bundles.txt");
                IReadOnlyList<string>? resourceBundleNames = File.Exists(resourceBundleManifest)
                    ? File.ReadAllLines(resourceBundleManifest).Where(l => !string.IsNullOrWhiteSpace(l)).ToList()
                    : null;

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
                        SwiftRuntimeVersion = swiftRuntimeVersion,
                        Dependencies = resolvedDependencies,
                        ResolvedNamespace = projectResolver.ResolveNamespace(resolution.ModuleName),
                        ObjCProjectFileName = objcProjFileName,
                        PlatformInfo = platformInfo,
                        ResourceBundleNames = resourceBundleNames,
                        EmitsAppleSupplementReference = AppleSupplementReferences.Any,
                        AppleSupplementVersion = appleVersion,
                        AppleSupplementPrototypeProjectPath = appleSupplementPrototypeCsproj,
                    }, logger);
                }

                // Both HasWrapperXCFramework and HasBridgeXCFramework are set to the
                // "will be produced" signal (wouldCompileWrapper / hasBridgeSwift), NOT the
                // "exists now" one (hasWrapperXcfw / hasBridgeXcfw): under the SDK's two-pass
                // flow the full-generate pass runs with --skip-wrapper-compilation, so neither
                // xcframework exists yet — _CompileSwiftWrapper compiles + packs the wrapper in
                // a later pass. The consumer targets guard each NativeReference with an Exists()
                // check, so emitting a reference the deferred pass fulfills is safe.
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = outputDirectory,
                    ModuleName = resolution.ModuleName,
                    PackageId = effectivePackageId,
                    EffectiveMinimumOSVersion = metadata.EffectiveMinimumOSVersion,
                    HasWrapperXCFramework = hasWrapperXcfw || wouldCompileWrapper,
                    HasBridgeXCFramework = hasBridgeSwift,
                    XcframeworkPath = xcframeworkPath,
                    PlatformInfo = platformInfo,
                    ResourceBundleNames = resourceBundleNames,
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
        else if (sdkMode && isSystemFrameworkTarget && !string.IsNullOrEmpty(directModuleName))
        {
            // Direct-mode SDK build (Apple system frameworks). The SDK target writes
            // binding-metadata.props itself via shell heredoc, but it has no visibility into
            // AppleSupplementReferences state. Emit an auxiliary apple-supplement.props so the
            // SDK's heredoc can <Import> it and the _SwiftBindingNeedsAppleSupplement signal
            // reaches the PackageReference injection in target 4f. Prototype mode also routes
            // through here: the csproj path flows into the aux file alongside the version.
            string? directSdkPrototypeCsproj = null;
            if (!string.IsNullOrWhiteSpace(appleSupplementPrototypeDir) && AppleSupplementReferences.Any)
            {
                try
                {
                    var protoResult = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                    {
                        PrototypeDirectory = appleSupplementPrototypeDir!,
                        ReferencedIdentities = AppleSupplementReferences.Current,
                        PlatformInfo = platformInfo,
                        SwiftRuntimeVersion = swiftRuntimeVersion,
                        MinimumOSVersion = "15.0",
                    }, logger);
                    directSdkPrototypeCsproj = protoResult.CsprojPath;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Failed to emit Apple-supplement prototype: {Message}", ex.Message);
                }
            }
            XCFrameworkMetadataExtractor.EmitAppleSupplementPropsFragment(
                outputDirectory,
                AppleSupplementReferences.Any,
                appleVersion,
                directSdkPrototypeCsproj,
                logger);
        }
        else if (isSystemFrameworkTarget && !sdkMode && !string.IsNullOrEmpty(directModuleName))
        {
            // Direct-mode binding project emission. There is no source xcframework —
            // for Apple system frameworks the binary lives on-device under
            // /System/Library/Frameworks/ and is resolved at runtime by dyld via the
            // @rpath library name passed at -l. The csproj therefore omits the source
            // NativeReference and pack item, but still references SwiftBindings.Runtime
            // and the wrapper xcframework so the consumer pulls in the SBW_ helpers.
            try
            {
                // The TBD's containing directory is the .framework, which has the same
                // Info.plist layout as a packaged .xcframework slice — Extract works
                // unchanged. ReadPlatforms gracefully returns empty when xcframeworkPath
                // points at nothing.
                var metadata = XCFrameworkMetadataExtractor.Extract(
                    tbdPath, xcframeworkPath: "", directModuleName, logger);

                var hasWrapperXcfw = compilationResult?.XCFrameworkPath != null
                    && Directory.Exists(compilationResult.XCFrameworkPath);
                var wrapperModuleName = $"{directModuleName}SwiftBindings";
                var directPackageId = packageId ?? platformInfo.GetDefaultSwiftPackageId(directModuleName);

                var projectFrameworkName = BindingsGenerator.InferFrameworkName(tbdPath, directModuleName);
                var projectResolver = new NamespacePatternResolver(effectiveNamespacePattern, projectFrameworkName);

                // Direct mode has no binding-metadata.props, so the prototype csproj only feeds
                // the generator-emitted consumer csproj path below — no SDK-target handoff.
                string? directPrototypeCsproj = null;
                if (!string.IsNullOrWhiteSpace(appleSupplementPrototypeDir) && AppleSupplementReferences.Any)
                {
                    var protoResult = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
                    {
                        PrototypeDirectory = appleSupplementPrototypeDir!,
                        ReferencedIdentities = AppleSupplementReferences.Current,
                        PlatformInfo = platformInfo,
                        SwiftRuntimeVersion = swiftRuntimeVersion,
                        MinimumOSVersion = metadata.EffectiveMinimumOSVersion,
                    }, logger);
                    directPrototypeCsproj = protoResult.CsprojPath;
                }

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = outputDirectory,
                    ModuleName = directModuleName,
                    Metadata = metadata,
                    SourceXCFrameworkPath = null,
                    WrapperXCFrameworkPath = hasWrapperXcfw ? compilationResult!.XCFrameworkPath : null,
                    SwiftRuntimeVersion = swiftRuntimeVersion,
                    AppleSupplementVersion = appleVersion,
                    PlatformInfo = platformInfo,
                    ResolvedNamespace = projectResolver.ResolveNamespace(directModuleName),
                    EmitsAppleSupplementReference = AppleSupplementReferences.Any,
                    AppleSupplementPrototypeProjectPath = directPrototypeCsproj,
                }, logger);

                // Emit consumer targets too — BindingProjectEmitter unconditionally packs
                // {PackageId}.targets, so dotnet pack would fail without this file. The
                // existing emitter handles the system-framework case naturally because the
                // source NativeReference uses an Exists() condition (the runtimes/<rid>/native
                // path is empty for system frameworks).
                ConsumerTargetsEmitter.Emit(new ConsumerTargetsEmitterOptions
                {
                    OutputDirectory = outputDirectory,
                    ModuleName = directModuleName,
                    PackageId = directPackageId,
                    EffectiveMinimumOSVersion = metadata.EffectiveMinimumOSVersion,
                    HasWrapperXCFramework = hasWrapperXcfw,
                    HasBridgeXCFramework = false,
                    XcframeworkPath = null,
                    PlatformInfo = platformInfo,
                }, logger);

                logger.LogInformation("Direct-mode binding project emitted successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to emit direct-mode binding project: {Message}", ex.Message);
                context.ExitCode = 1;
                return;
            }
        }
    }

    /// <summary>
    /// Strip the leading "\" from a CLI value of the form "\@..." — the documented escape
    /// for passing "@rpath/..." names without triggering System.CommandLine's response-file
    /// handling. The stripped value is what flows into emitted DllImport string literals.
    /// </summary>
    private static string? StripCliAtEscape(string? value)
    {
        if (value != null && value.StartsWith("\\@", StringComparison.Ordinal))
            return value.Substring(1);
        return value;
    }

    /// <summary>
    /// Derives the Swift module name from a path of the canonical Apple-SDK swiftinterface
    /// layout: <c>&lt;Framework&gt;.framework/Modules/&lt;Module&gt;.swiftmodule/&lt;arch&gt;.swiftinterface</c>.
    /// Returns the module name (the parent dir's filename minus the <c>.swiftmodule</c> suffix),
    /// or null if the path doesn't match the expected layout.
    /// </summary>
    internal static string? DeriveModuleNameFromSwiftInterfacePath(string swiftInterfacePath)
    {
        if (string.IsNullOrWhiteSpace(swiftInterfacePath))
            return null;
        var parentDir = Path.GetDirectoryName(swiftInterfacePath);
        if (string.IsNullOrEmpty(parentDir))
            return null;
        var parentName = Path.GetFileName(parentDir);
        const string suffix = ".swiftmodule";
        if (!parentName.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var module = parentName.Substring(0, parentName.Length - suffix.Length);
        return string.IsNullOrEmpty(module) ? null : module;
    }

    // Parses the leading numeric component of an Apple supplement version string
    // (e.g. "26.0.0" → 26). Used to derive sdk_train.major when --apple-sdk-train-major
    // is not set explicitly. Fails loud on malformed input so a future train bump
    // can't silently fall back to the previous default.
    internal static int ParseAppleVersionMajor(string appleVersion)
    {
        if (string.IsNullOrWhiteSpace(appleVersion))
            throw new ArgumentException("--apple-version must not be empty.", nameof(appleVersion));
        var dot = appleVersion.IndexOf('.');
        var majorStr = dot >= 0 ? appleVersion.Substring(0, dot) : appleVersion;
        if (!int.TryParse(majorStr, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var major) || major <= 0)
            throw new ArgumentException($"--apple-version '{appleVersion}' is not a valid version (expected leading integer major).", nameof(appleVersion));
        return major;
    }

    /// <summary>
    /// Returns true when the CLI was invoked in direct (manual) mode against an Apple SDK
    /// system framework — i.e., no <c>--xcframework</c> was supplied and the runtime library
    /// name targets either an <c>@rpath/...</c> or an absolute <c>/System/Library/...</c>
    /// path. The direct-mode wrapper-compile and csproj-emit branches are gated on this
    /// predicate so non-system manual workflows (a local third-party .framework, a custom
    /// dylib path) keep their pre-existing "emit C# + Wrapper.swift only" behavior.
    /// Pulled out as a small helper so the gate is unit-testable in isolation from the
    /// rest of the CLI execution path.
    /// </summary>
    internal static bool IsSystemFrameworkTarget(bool hasXcframework, string? libraryName)
    {
        if (hasXcframework || string.IsNullOrEmpty(libraryName))
            return false;
        return libraryName.StartsWith("@rpath/", StringComparison.Ordinal)
            || libraryName.StartsWith("/System/Library/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Validate the <c>--platform-version</c> CLI value against the canonical Apple TPV
    /// shape "&lt;major&gt;.&lt;minor&gt;" (e.g. "26.0", "26.2"). The version segment of a
    /// .NET Apple TFM only accepts this two-integer form — anything else (a typo like
    /// "26.two", an unwanted pre-release tail like "26.2-preview", trailing whitespace
    /// from a poorly-quoted shell invocation) would propagate into the emitted
    /// <c>&lt;TargetFramework&gt;</c> element and only fail later with an opaque
    /// MSBuild/NuGet error.
    /// </summary>
    internal static bool IsValidPlatformVersion(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d+\.\d+$");
    }

    /// <summary>
    /// Returns true when the run is on the publishable path and therefore requires an
    /// explicit <c>--platform-version</c>. The emitted csproj is packable iff
    /// <c>--swift-runtime-version</c> is set to anything other than the dev sentinel
    /// (matches <see cref="BindingProjectEmitter"/>'s <c>isPackable</c> gate). On that
    /// path, silently inheriting <c>PlatformInfo.DefaultPlatformVersion</c> would label
    /// the published nupkg for the wrong SDK cut.
    /// </summary>
    internal static bool RequiresExplicitPlatformVersion(string? swiftRuntimeVersion) =>
        !string.IsNullOrWhiteSpace(swiftRuntimeVersion) &&
        swiftRuntimeVersion != BindingProjectEmitter.DefaultSwiftRuntimeVersion;

    internal static List<string>? GetDependencyModuleNamesForSwiftImports(
        IReadOnlyList<FrameworkDependencyInfo>? resolvedDependencies)
    {
        // ObjC-only modules can still appear in Swift signatures, so generated Swift
        // wrappers must import them even when they do not provide ABI JSON.
        return resolvedDependencies?
            .Select(d => d.ModuleName)
            .ToList();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  --xcframework        Path to xcframework directory. Replaces -a, -d, -t.");
        Console.WriteLine("  --platform           Apple platform: 'ios' (default), 'macos', 'tvos', 'maccatalyst'.");
        Console.WriteLine("  --platform-version   Optional. Apple workload platform version baked into the emitted csproj (e.g. '26.2' for net10.0-ios26.2). Required when packing for nuget.org so library-default 'oldest TPV' resolution can't desync TFM from buildTransitive/ paths. Default falls back to the in-tree value.");
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
        Console.WriteLine("  --package-id         Optional. Package ID for NuGet packaging. Default: '{Module}.Swift.{Platform}' (e.g. Nuke.Swift.iOS, Nuke.Swift.macOS).");
        Console.WriteLine("  --swift-runtime-version  Optional. SwiftBindings.Runtime version for the emitted .csproj. Default '0.0.0-dev' is local-dev only (IsPackable=false). Pass a published version to enable 'dotnet pack'.");
        Console.WriteLine("  --wrapper-architectures  Optional. Wrapper compilation scope: 'simulator' (default), 'device', or 'all'.");
        Console.WriteLine("  --framework-dependency   Optional. Repeatable. Path to dependency xcframework for -F search paths. Requires --xcframework.");
        Console.WriteLine("  --module-database    Optional. Repeatable. Path to dependency module database XML for cross-module type resolution.");
        Console.WriteLine("  --no-auto-detect     Optional. Disable automatic dependency detection from binary linkage.");
        Console.WriteLine("  --keep-builtin-database  Optional. Disable Apple-framework target mode auto-detection (keeps the built-in stub when the input module name matches).");
        Console.WriteLine("  --objc               Optional. Force ObjC binding pipeline (auto-detected if not specified).");
        Console.WriteLine("  --skip-wrapper-compilation  Optional. Skip wrapper compilation (SDK defers to _CompileSwiftWrapper target).");
        Console.WriteLine("  --skip-thunk-compilation    Optional. Skip native thunk assembly compilation.");
        Console.WriteLine("  --compile-wrapper-only      Optional. Compile existing .swift wrapper files only (no parsing/generation).");
        Console.WriteLine("  --compile-bridge-only       Optional. Compile existing SwiftUI bridge .swift files only (no parsing/generation).");
        Console.WriteLine();
        Console.WriteLine("Per-RID xcframework slicing — ignores binding-generation options:");
        Console.WriteLine("  --slice-xcframework  Stage a sliced copy of --xcframework into -o, retaining only slices the given --rid can consume.");
        Console.WriteLine("  --rid                NuGet RID: ios-arm64, tvos-arm64, osx-arm64, maccatalyst-arm64.");
        Console.WriteLine();
        Console.WriteLine("Apple types manifest (SwiftBindings.Apple) — ignores binding-generation options:");
        Console.WriteLine("  --emit-apple-types-manifest  Emit the Apple-types metadata manifest from ABI JSON dumps. With this flag, -o is a FILE path (the target .json), not a directory.");
        Console.WriteLine("  --apple-abi-json         Repeatable. Path to an Apple SDK ABI JSON dump (from `swift-api-digester -dump-sdk`). Union-merged per-platform.");
        Console.WriteLine("  --apple-include-types    Required. Path to include-types.json (positive-list of 'Module.NestedType' identities to emit).");
        Console.WriteLine("  --apple-version          Apple SDK train / SwiftBindings.Apple supplement version (e.g. 26.0.0). Default: 26.0.0.");
        Console.WriteLine("  --apple-sdk-train-major  Optional override for sdk_train.major. Derived from --apple-version when omitted.");
        Console.WriteLine("  --apple-sdk-train-label  Optional. Human-readable SDK train label (e.g. 'Xcode 26 / iOS 26').");
        Console.WriteLine("  --apple-sdk-min-ios / --apple-sdk-min-maccatalyst / --apple-sdk-min-tvos / --apple-sdk-min-macos  Optional per-platform floors.");
        Console.WriteLine();
        Console.WriteLine($"  --config             Optional. Path to config file. Default: {BindingsGenerator.DefaultConfigFileName}");
        Console.WriteLine("  --interface-facts-producer  'auto' (default, M2 S3), 'swift-syntax', or 'regex'. 'auto' picks swift-syntax on Darwin when the host binary is present, else regex.");
        Console.WriteLine("  -v, --verbose        Verbosity level. 0 = No logging, 1 = General information, 2 = Debugging information. (default: 1)");
    }

    /// <summary>
    /// Construct the <see cref="InterfaceFactsAggregator"/> from the CLI flag.
    /// <list type="bullet">
    /// <item><c>auto</c> (default, M2 S3): on Darwin, attempts to locate the SwiftInterfaceParser
    /// host binary; if present, prepends the SwiftSyntax producer to the regex fallback. On
    /// non-Darwin or when the binary cannot be located, transparently degrades to regex-only.
    /// This is the cross-platform-safe path — Linux CI builds keep working without the
    /// SwiftSyntax host binary.</item>
    /// <item><c>swift-syntax</c>: hard-requires the host binary. Hard-fails on non-Darwin or
    /// when the binary cannot be located. Used by parity tests and developers who want to
    /// detect a missing-binary regression rather than silently fall back.</item>
    /// <item><c>regex</c>: legacy single-producer aggregator. Behavior is byte-equal to the
    /// pre-M2 inline parsing flow. Kept available through M2 S4 for parity diffing and
    /// emergency rollback.</item>
    /// </list>
    /// Unknown values throw — silent fallback would defeat the explicit-switch design.
    /// </summary>
    private static InterfaceFactsAggregator BuildInterfaceFactsAggregator(string flag, ILogger logger)
    {
        return flag switch
        {
            "auto" => BuildAutoAggregator(logger),
            "regex" => new InterfaceFactsAggregator(new IInterfaceFactsProducer[]
            {
                new RegexInterfaceFactsProducer(),
            }),
            "swift-syntax" => BuildSwiftSyntaxAggregator(logger),
            _ => throw new ArgumentException(
                $"Unknown --interface-facts-producer value '{flag}'. Expected 'auto', 'swift-syntax', or 'regex'."),
        };
    }

    /// <summary>
    /// 'auto' mode: cross-platform-safe wiring. Prefers the SwiftSyntax host binary on Darwin
    /// when present; transparently falls back to regex-only on non-Darwin or when the binary
    /// can't be located. Logs the chosen path so consumers can see which producer ran.
    /// </summary>
    private static InterfaceFactsAggregator BuildAutoAggregator(ILogger logger)
    {
        if (!OperatingSystem.IsMacOS())
        {
            logger.LogInformation("--interface-facts-producer=auto: non-Darwin host, using regex producer only.");
            return new InterfaceFactsAggregator(new IInterfaceFactsProducer[]
            {
                new RegexInterfaceFactsProducer(),
            });
        }

        var binaryPath = SwiftSyntaxInterfaceFactsProducer.TryLocateBinary();
        if (binaryPath is null)
        {
            logger.LogInformation(
                "--interface-facts-producer=auto: SwiftInterfaceParser host binary not found; falling back to regex producer. " +
                "Run `nuke compile` to build the host binary, or set SWIFT_INTERFACE_PARSER_PATH.");
            return new InterfaceFactsAggregator(new IInterfaceFactsProducer[]
            {
                new RegexInterfaceFactsProducer(),
            });
        }

        logger.LogInformation("--interface-facts-producer=auto: using SwiftSyntax producer at {Path} (regex producer is fallback).", binaryPath);
        return new InterfaceFactsAggregator(new IInterfaceFactsProducer[]
        {
            new SwiftSyntaxInterfaceFactsProducer(binaryPath),
            new RegexInterfaceFactsProducer(),
        });
    }

    private static InterfaceFactsAggregator BuildSwiftSyntaxAggregator(ILogger logger)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new InvalidOperationException(
                "--interface-facts-producer=swift-syntax requires Darwin (macOS): the SwiftInterfaceParser " +
                "host binary is built only for Darwin. Use --interface-facts-producer=auto for " +
                "cross-platform-safe selection, or 'regex' to force the legacy producer.");
        }

        var binaryPath = SwiftSyntaxInterfaceFactsProducer.TryLocateBinary();
        if (binaryPath is null)
        {
            throw new InvalidOperationException(
                "SwiftSyntaxInterfaceFactsProducer: could not locate SwiftInterfaceParser binary. " +
                "Run `nuke compile` (Darwin only) or set SWIFT_INTERFACE_PARSER_PATH.");
        }
        logger.LogInformation("Using SwiftSyntax interface facts producer at: {Path}", binaryPath);
        return new InterfaceFactsAggregator(new IInterfaceFactsProducer[]
        {
            new SwiftSyntaxInterfaceFactsProducer(binaryPath),
            new RegexInterfaceFactsProducer(),
        });
    }
}
