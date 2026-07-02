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
        var strictInputs = parseResult.GetValueForOption(options.StrictInputs);
        var packageId = parseResult.GetValueForOption(options.PackageId);
        var assemblyNameOverride = parseResult.GetValueForOption(options.AssemblyName);
        var swiftRuntimeVersion = parseResult.GetValueForOption(options.SwiftRuntimeVersion);
        var wrapperArchitectures = parseResult.GetValueForOption(options.WrapperArchitectures);
        var targetArchitectures = parseResult.GetValueForOption(options.TargetArchitectures);
        var frameworkDependencies = parseResult.GetValueForOption(options.FrameworkDependency);
        var linkFrameworks = parseResult.GetValueForOption(options.LinkFramework);
        var linkLibraries = parseResult.GetValueForOption(options.LinkLibrary);
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
        var resolveAutoDeps = parseResult.GetValueForOption(options.ResolveAutoDeps);
        var autoDepSpec = parseResult.GetValueForOption(options.AutoDepSpec);
        var explicitDeps = parseResult.GetValueForOption(options.ExplicitDeps);
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
        var regenStdlibConformances = parseResult.GetValueForOption(options.RegenStdlibConformances);
        var stdlibDumpPath = parseResult.GetValueForOption(options.StdlibDump);
        var stdlibConformancesPath = parseResult.GetValueForOption(options.StdlibConformances);
        var stdlibConformancesWriteBack = parseResult.GetValueForOption(options.StdlibConformancesWriteBack);
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

        // Handle --resolve-auto-deps: resolve auto-detected cross-module Swift dependencies
        // into sibling ProjectReference paths (or unresolved-dependency warnings) for the
        // SDK's _ResolveSwiftAutoDetectedDependencies target. Out-of-tree from the
        // binding-generation pipeline — never consumes dylib/TBD/ABI-JSON, so it must run
        // BEFORE --platform validation. Emits the frozen PROJREF|/WARN| line grammar to
        // stdout, which the SDK captures via ConsoleToMSBuild.
        if (resolveAutoDeps)
        {
            try
            {
                AutoDepResolver.Run(autoDepSpec, explicitDeps, Console.Out);
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

        // Handle --validate-apple-types-manifest: live-SDK CI validator.
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

        // Handle --regen-stdlib-conformances: verify/prune the embedded stdlib conformance fact
        // table against a Swift-stdlib swift-api-digester dump (produced by the nuke target).
        // Out-of-tree from the binding-generation pipeline — must run BEFORE --platform validation,
        // same as the Apple-types modes.
        if (regenStdlibConformances)
        {
            context.ExitCode = StdlibConformances.StdlibConformancesRegenCommand.Run(
                stdlibDumpPath ?? string.Empty,
                stdlibConformancesPath ?? string.Empty,
                stdlibConformancesWriteBack,
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
                skipThunkCompilation, targetArchitectures,
                linkFrameworks: linkFrameworks, linkLibraries: linkLibraries);
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
                wrapperArchitectures, frameworkDependencies, logger, platformInfo,
                targetArchitectures);
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

        // --link-framework / --link-library only take effect on the wrapper link of a
        // force-loaded static-archive source, which is an --xcframework-mode concept. In
        // -a/-d/-t direct mode (and the Apple system-framework path) there is no wrapper
        // link to consume them, so accepting them silently would drop the author's declared
        // system dependencies and produce a wrapper that fails to resolve at load. Fail closed
        // instead, honoring the CLI descriptions ("Requires --xcframework"). The
        // --compile-wrapper-only fast path is unaffected: it returns above and already
        // requires --xcframework.
        if (LinkDependenciesSuppliedWithoutXcframework(hasXcframework, linkFrameworks, linkLibraries))
        {
            logger.LogError(
                "Error: --link-framework and --link-library require --xcframework mode. They declare " +
                "system frameworks/libraries for the wrapper link of a force-loaded static-archive source " +
                "and have no effect in -a/-d/-t direct mode.");
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

        // Finding 50: clear the ambient input-resolution collector at the start of every
        // generation so slice/arch/artifact/dependency decisions accumulate from a clean slate.
        // Placed ahead of both the xcframework-resolution path (below) and the direct-input
        // path, since either can record decisions (e.g. a degraded auto-detected dependency).
        InputResolutionReport.Reset();

        // Finding 58: assert the active host toolchain against the tested support envelope before any
        // parsing. Recorded on the same InputResolutionReport channel (category Toolchain) so an
        // out-of-envelope Xcode warns loudly (SWIFTBIND055) and fails closed under --strict-inputs via
        // the EmitStrictInputsFailureIfDegraded gate shared by every completion path below. Placed
        // after Reset() so the toolchain decision is part of this generation's report, and before the
        // hasXcframework branch so it covers both the xcframework and direct-input paths. The
        // --compile-wrapper-only / --compile-bridge-only fast paths return above and are intentionally
        // NOT covered: they ingest no ABI (there is no parse to mis-calibrate) and run none of the
        // InputResolutionReport / --strict-inputs lifecycle this records into; the SDK's full build
        // still asserts here on its generate pass, which precedes the later wrapper-only pass.
        SupportedToolchain.AssertSupported(new SystemCommandRunner(), logger);

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
                // Pure-ObjC primary generation: record the slice choice (recordResolution: true) so a
                // silent device→simulator fallback is caught by the --strict-inputs gate below.
                var objcResolution = XCFrameworkResolver.ResolveObjCFramework(
                    xcframeworkPath!, platformTarget, logger, platformInfo: platformInfo,
                    recordResolution: true);
                if (objcResolution == null)
                {
                    logger.LogError("Failed to resolve ObjC framework from '{Path}'.", xcframeworkPath);
                    context.ExitCode = 1;
                    return;
                }
                // Finding 50: fail-close before emitting a silently-degraded pure-ObjC binding.
                if (EmitStrictInputsFailureIfDegraded(strictInputs, logger))
                {
                    context.ExitCode = 1;
                    return;
                }
                var siblingSearchPaths = XCFrameworkResolver.ResolveSiblingFrameworkSearchPaths(
                    xcframeworkPath!, platformTarget, logger, platformInfo: platformInfo);
                // A2: also thread each --framework-dependency's resolved slice dir into the ObjC
                // clang -F search path so a cross-framework #import in the umbrella header resolves.
                // The pure-ObjC paths return before the full dependency resolution below, so resolve
                // the dependency slice dirs directly here.
                var objcSearchPaths = MergeObjCFrameworkSearchPaths(
                    siblingSearchPaths,
                    ResolveObjCDependencySliceDirs(frameworkDependencies, platformTarget, logger, platformInfo));
                var objcResult = ObjCPipeline.Run(
                    objcResolution, xcframeworkPath!, outputDirectory, platformTarget, logger,
                    namespacePattern: namespacePattern, packageId: packageId,
                    sdkMode: sdkMode, isMixed: false,
                    additionalFrameworkSearchPaths: objcSearchPaths,
                    platformInfo: platformInfo);
                context.ExitCode = objcResult.ExitCode;
                if (objcResult.ErrorMessage != null)
                    logger.LogError("{Message}", objcResult.ErrorMessage);
                return;
            }

            try
            {
                resolution = XCFrameworkResolver.Resolve(
                    xcframeworkPath!, outputDirectory, platformTarget, logger, platformInfo: platformInfo,
                    companionFrameworkPaths: frameworkDependencies);
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
                // Auto-detect ObjC fallback (covers pure ObjC frameworks and static libraries)
                var reason = ex is StaticLibraryException ? "Static library" : "No Swift module found";
                logger.LogInformation("{Reason} — attempting ObjC framework detection...", reason);
                // Swift resolution failed and we fell back to a pure-ObjC binding: this ObjC slice
                // choice is now the PRIMARY input, so record it (recordResolution: true) for the gate.
                var objcResolution = XCFrameworkResolver.ResolveObjCFramework(
                    xcframeworkPath!, platformTarget, logger, platformInfo: platformInfo,
                    recordResolution: true);
                if (objcResolution == null)
                {
                    logger.LogError("Framework has no ObjC module.modulemap and no Swift module.");
                    context.ExitCode = 1;
                    return;
                }
                // Finding 50: fail-close before emitting a silently-degraded pure-ObjC binding.
                if (EmitStrictInputsFailureIfDegraded(strictInputs, logger))
                {
                    context.ExitCode = 1;
                    return;
                }
                var siblingSearchPaths = XCFrameworkResolver.ResolveSiblingFrameworkSearchPaths(
                    xcframeworkPath!, platformTarget, logger, platformInfo: platformInfo);
                // A2: also thread each --framework-dependency's resolved slice dir into the ObjC
                // clang -F search path so a cross-framework #import in the umbrella header resolves.
                // The pure-ObjC paths return before the full dependency resolution below, so resolve
                // the dependency slice dirs directly here.
                var objcSearchPaths = MergeObjCFrameworkSearchPaths(
                    siblingSearchPaths,
                    ResolveObjCDependencySliceDirs(frameworkDependencies, platformTarget, logger, platformInfo));
                var objcResult = ObjCPipeline.Run(
                    objcResolution, xcframeworkPath!, outputDirectory, platformTarget, logger,
                    namespacePattern: namespacePattern, packageId: packageId,
                    sdkMode: sdkMode, isMixed: false,
                    additionalFrameworkSearchPaths: objcSearchPaths,
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
                logger, platformInfo: platformInfo,
                companionFrameworkPaths: frameworkDependencies);
            if (analysisResult != null)
            {
                autoDetectedDeps = analysisResult.ResolvedDependencies;
                foreach (var dep in autoDetectedDeps)
                    logger.LogInformation("Auto-detected dependency: {Module} ({Path})",
                        dep.ModuleName, dep.XCFrameworkPath);
                RecordUnresolvedDependencyDegradations(analysisResult.UnresolvedDependencies, logger);
            }
            else
            {
                RecordSystemicDependencyAnalysisFailure(logger);
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
        // "{Module}SwiftBindings" name (matching xcframework mode's behavior — e.g.
        // "FooSwiftBindings" for a module named "Foo") so the binding correctly expresses its intent to
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

        // Apple SYSTEM frameworks: reduce the embedded library name to the bare framework
        // name (e.g. "CryptoKit") so the per-assembly DllImport resolver maps it to /System
        // on a physical device (see ResolveRuntimeLibraryName for the full rationale). This
        // must run before the GenerateBindings call below, which bakes the name into every
        // emitted [LibraryImport]/LoadFromSymbol string.
        runtimeLibraryName = ResolveRuntimeLibraryName(
            runtimeLibraryName, directModuleName, IsSystemFrameworkTarget(hasXcframework, libraryName));
        var effectiveNamespacePattern = BindingsGenerator.ResolveNamespacePattern(namespacePattern, configPath, logger);

        // Auto-extract symbol graph for doc comments (xcframework mode only)
        symbolGraph = BindingsGenerator.ResolveSymbolGraphPath(symbolGraph, noDocs, resolution, outputDirectory, logger, platformInfo: platformInfo);

        var depModuleNames = GetDependencyModuleNamesForSwiftImports(resolvedDependencies);

        // Validate the --interface-facts-producer flag here (cheap, platform-independent), but DEFER
        // aggregator construction into GenerateBindings. InterfaceFactsAggregator.CreateDefault
        // enforces the macOS-only / host-binary-present invariant by throwing; constructing it here —
        // outside GenerateBindings' try/catch — would surface that expected hard error as an unhandled
        // command exception instead of the structured "logged + ExitCode = 1" failure every other
        // generation error uses. GenerateBindings builds the default aggregator lazily, only when a
        // .swiftinterface is actually aggregated, so an ABI/TBD-only run never requires the host.
        if (!IsValidInterfaceFactsProducer(interfaceFactsProducer))
        {
            logger.LogError(
                "Unknown --interface-facts-producer value '{Flag}'. Expected 'auto' or 'swift-syntax'. " +
                "(The legacy 'regex' producer was removed; interface-facts parsing is SwiftSyntax-only " +
                "and requires macOS.)",
                interfaceFactsProducer);
            context.ExitCode = 1;
            return;
        }

        // Mixed ObjC+Swift type-resolution bridge (Phase 1): parse the ObjC half BEFORE Swift
        // generation so records synthesized from the ObjC surface (classes → ObjCBridged, NS_ENUM →
        // SimpleEnum) can be registered into the target module's database before Swift resolution
        // runs. A Swift member that names an ObjC type (e.g. FBSDKCoreKit.AccessToken, bound on the
        // ObjC side as `partial interface FBSDKAccessToken`) then resolves to the companion's C# type
        // instead of degrading to object/AnyType or being dropped. The ObjC pipeline is split so this
        // single parse feeds both the bridge here and the companion emission below (FilterAndEmit) —
        // clang runs once. Emission is deferred until after the Swift pass writes swift-types.json
        // (it needs that exclude set to dedup Swift-owned types). A hard Parse failure aborts right
        // here, BEFORE Swift generation: the framework has a known ObjC surface, so a systemic parse
        // failure always fails closed (ShouldAbortForFailedMixedObjC), and running the full Swift
        // pass first would only burn work and leave partial .cs artifacts on disk behind a non-zero
        // exit. On success, mixedBridgeRecords carries the synthesized records into GenerateBindings.
        ObjCParseResult? mixedParse = null;
        IReadOnlyList<TypeRecord>? mixedBridgeRecords = null;
        if (hasXcframework && resolution != null && mixedObjcResolution != null)
        {
            var mixedSiblingPaths = XCFrameworkResolver.ResolveSiblingFrameworkSearchPaths(
                xcframeworkPath!, platformTarget, logger, platformInfo: platformInfo);
            // A2: thread the resolved dependency slice dirs (manual --framework-dependency +
            // auto-detected) into the ObjC clang -F search path so a mixed framework whose umbrella
            // header cross-imports a dependency (e.g. FBSDKLoginKit → FBSDKCoreKit) resolves.
            var mixedObjcSearchPaths = MergeObjCFrameworkSearchPaths(
                mixedSiblingPaths,
                SelectObjCDependencySearchPaths(resolvedDependencies, platformTarget));
            mixedParse = ObjCPipeline.Parse(
                mixedObjcResolution, xcframeworkPath!, logger,
                namespacePattern: namespacePattern,
                additionalFrameworkSearchPaths: mixedObjcSearchPaths,
                platformInfo: platformInfo);
            if (mixedParse.ExitCode == 0 && mixedParse.Module != null)
            {
                mixedBridgeRecords = ObjCBridgeRecordFactory.CreateRecords(
                    mixedParse.Module, mixedObjcResolution.ModuleName, mixedParse.ResolvedNamespace, logger);
            }
        }

        // Fail closed before Swift generation. A systemic ObjC parse failure (clang/AST dump,
        // umbrella-header resolution, or the native-symbol eligibility filters) on a framework that
        // HAS an ObjC surface must not degrade to a Swift-only package — that would silently drop the
        // ObjC types AND bypass SWIFTBIND039 (which only fires when metadata still says "Mixed").
        // ShouldAbortForFailedMixedObjC has no permissive escape, so this always aborts; doing it
        // here avoids emitting partial Swift artifacts we would only throw away. Mirrors the pure-ObjC
        // path's context.ExitCode = objcResult.ExitCode propagation.
        if (mixedParse != null && (mixedParse.ExitCode != 0 || mixedParse.Module == null))
        {
            logger.LogError(
                "ObjC pipeline for mixed framework failed (exit {Code}); refusing to emit a " +
                "Swift-only binding that would silently drop the ObjC surface. {Msg}",
                mixedParse.ExitCode, mixedParse.ErrorMessage ?? "(no detail)");
            context.ExitCode = mixedParse.ExitCode != 0 ? mixedParse.ExitCode : 1;
            return;
        }

        var success = BindingsGenerator.GenerateBindings(swiftAbiPath, dylibPath, tbdPath, outputDirectory, runtimeLibraryName, asyncLibrary, swiftInterface, symbolGraph, bridgeHints, effectiveNamespacePattern, logger, loggerFactory, out var internalTypeNames, out var moduleNameForCollision, out var nestedTypesInCollidingClass, out var depModuleCollisions, dependencyModuleNames: depModuleNames, moduleDatabasePaths: moduleDatabases, resolvedDependencies: resolvedDependencies, platform: platformInfo.Platform, keepBuiltinDatabaseForTargetModule: keepBuiltinDatabase, descriptorAssemblyNameOverride: assemblyNameOverride, swiftRuntimeVersion: swiftRuntimeVersion, objcBridgeRecords: mixedBridgeRecords);
        if (!success)
        {
            context.ExitCode = 1;
            return;
        }

        // Finding 50: fail-closed on a degraded input edge under --strict-inputs (the CI compile
        // gate). The input-resolution report (slice fallback, missing swiftinterface, ABI-JSON
        // fallback, ambiguous/synthesized TBD, degraded auto-detected dependency) was recorded
        // during XCFrameworkResolver.Resolve and dependency parsing; surfacing it as a fatal
        // error here closes the "graceful-to-a-fault" gap where a silently-substituted input
        // shrank the API surface but still exited 0. On this Swift path GenerateBindings (above)
        // has already persisted the full decision list (Info plus degradations) to the
        // inputResolution section of binding-artifact-manifest.json; this gate logs each
        // degradation as a SWIFTBIND027 line and escalates only the *degradations* to a failure.
        if (EmitStrictInputsFailureIfDegraded(strictInputs, logger))
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

            // Gap (a): mirror ABI extraction's co-located sibling auto-detection on the wrapper
            // compile, so a companion xcframework dropped next to the source resolves its module
            // for swiftc just as it already does for ABI generation. Explicit --framework-dependency
            // paths keep priority; siblings are merged in.
            var simDepPathsMerged = XCFrameworkResolver.MergeWrapperDependencySearchPaths(
                simDepPaths, xcframeworkPath!, XCFrameworkPlatformTarget.Simulator, logger, platformInfo);
            var deviceDepPathsMerged = XCFrameworkResolver.MergeWrapperDependencySearchPaths(
                deviceDepPaths, xcframeworkPath!, XCFrameworkPlatformTarget.Device, logger, platformInfo);
            simDepPaths = simDepPathsMerged;
            deviceDepPaths = deviceDepPathsMerged;

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
                platformInfo, logger, companionFrameworkPaths: frameworkDependencies);
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
                        requestedArchitecture: requestedArch,
                        companionFrameworkPaths: frameworkDependencies);

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
                        depModuleNamesForCollisionDevice: depModuleCollisions.Device,
                        linkFrameworks: linkFrameworks,
                        linkLibraries: linkLibraries);
                }
                else if (wrapperArchNormalized == "device")
                {
                    // Device-only: resolve device slice and compile for iphoneos. A resolve failure
                    // propagates to the outer try and is reported by WrapperBuildOutcome.
                    var deviceOnlyResolution = XCFrameworkResolver.Resolve(
                        xcframeworkPath!, outputDirectory,
                        XCFrameworkPlatformTarget.Device, logger, platformInfo: platformInfo,
                        requestedArchitecture: requestedArch,
                        companionFrameworkPaths: frameworkDependencies);

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
                        depModuleNamesForCollision: depModuleCollisions.Device,
                        linkFrameworks: linkFrameworks,
                        linkLibraries: linkLibraries);
                }
                else
                {
                    // Simulator-only (default)
                    var simResolution = XCFrameworkResolver.Resolve(
                        xcframeworkPath!, outputDirectory,
                        platformTarget, logger, platformInfo: platformInfo,
                        requestedArchitecture: requestedArch,
                        companionFrameworkPaths: frameworkDependencies);

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
                        depModuleNamesForCollision: depModuleCollisions.Simulator,
                        linkFrameworks: linkFrameworks,
                        linkLibraries: linkLibraries);
                }
            }

            IReadOnlyList<string> unmergedExtraArchs = Array.Empty<string>();
            try
            {
                compilationResult = BindingsGenerator.CompileWrapperForArchitectures(
                    primaryArch, extraArchs, CompileForArch, logger, out unmergedExtraArchs);
            }
            catch (Exception ex)
            {
                compilationException = ex;
            }

            // An explicit --target-architectures list is a contract: an extra arch the fold failed to
            // deliver fails the build instead of silently shipping a narrower wrapper. Auto-matched
            // archs stay best-effort.
            var contractualUnmet = autoMatchSource
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : unmergedExtraArchs;

            var outcome = WrapperBuildOutcome.From(
                compilationResult, asyncLibraryAutoWired, sdkMode, compilationException, contractualUnmet);
            outcome.LogTo(logger);
            if (outcome.IsFatal)
            {
                context.ExitCode = outcome.ExitCode;
                return;
            }

            IReadOnlyList<CoGatedMember> coGated = Array.Empty<CoGatedMember>();
            if (outcome.StrippedSymbols.Count > 0)
            {
                coGated = StrippedSymbolCSharpReconciler.ProcessDirectory(
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
            // path — the wrapper CPU-arch decision is shared, not per-call-site.
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
            IReadOnlyList<string> directUnmergedExtraArchs = Array.Empty<string>();
            try
            {
                directResult = BindingsGenerator.CompileWrapperForArchitectures(
                    directPrimaryArch, directExtraArchs, CompileDirectForArch, logger,
                    out directUnmergedExtraArchs);
            }
            catch (Exception ex)
            {
                directException = ex;
            }

            // An explicit --target-architectures list is a contract here too. directExtraArchs was already
            // pruned of arches the active slice can't natively compile (those are deferred to the SDK's
            // fat-sim second slice, NOT a violation), so anything still undelivered is a real shortfall.
            var directContractualUnmet = autoMatchSourceDirect
                ? (IReadOnlyList<string>)Array.Empty<string>()
                : directUnmergedExtraArchs;

            // Direct mode never auto-wires --async-library inside the xcframework
            // helper, so plain failures are treated as Warnings (not Fatal) — but an unmet explicit
            // architecture contract stays fatal (From keeps it fatal regardless of sdkMode). Surface
            // and continue otherwise — the C# bindings are still correct on disk and the user can
            // rerun with --skip-wrapper-compilation to bypass.
            var directOutcome = WrapperBuildOutcome.From(
                directResult, asyncLibraryAutoWired: false, sdkMode, directException, directContractualUnmet);
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
                directCoGated = StrippedSymbolCSharpReconciler.ProcessDirectory(
                    outputDirectory, directOutcome.StrippedSymbols, logger);
                if (directCoGated.Count > 0)
                    logger.LogInformation("Suppressed {Count} C# member(s) targeting stripped wrapper symbols.", directCoGated.Count);
            }

            BindingArtifactManifestStore.ReadModifyWrite(
                outputDirectory,
                directModuleName,
                m => m.Wrapper = WrapperSection.From(directOutcome, directCoGated),
                logger);

            // SwiftUI bridge primary slice (Apple direct mode). The xcframework path
            // builds the bridge via the SDK's --compile-bridge-only target, but Apple
            // system frameworks compile the wrapper inline here (no source xcframework
            // to defer against), so the bridge has to be built inline too — otherwise
            // its @_cdecl trampolines never make it into a dylib and any generated
            // SwiftUI bridge view throws DllNotFoundException("<Module>Bridge") at
            // runtime. The SDK's _CompileAppleFrameworkSecondBridgeSlice adds the other
            // slice (device, or fat x86_64 simulator) post-generate, mirroring the
            // wrapper's two-phase split. Reuse the shared arch-fanout so the bridge
            // primary slice carries the identical arch coverage to the wrapper primary
            // slice — the wrapper CPU-arch decision is shared. Failure is
            // non-fatal: the bridge is an additive convenience and must not take down
            // the main bindings (mirrors the SwiftFramework path's SWIFTBIND052 contract).
            var directBridgeFiles = SwiftWrapperCompiler.CollectBridgeSwiftFiles(outputDirectory);
            if (directBridgeFiles.Count > 0)
            {
                SwiftWrapperCompilationResult? CompileBridgeForArch(string? requestedArch) =>
                    SwiftWrapperCompiler.CompileBridgeSlice(
                        outputDirectory, directModuleName,
                        frameworkSearchPath!, tbdPath,
                        string.IsNullOrEmpty(requestedArch) ? directSlice : directSlice.WithArchitecture(requestedArch),
                        logger);

                try
                {
                    // Bridge slices are additive — an undelivered extra arch is best-effort, never a
                    // contract violation — so the unmerged-arch signal is intentionally discarded.
                    BindingsGenerator.CompileWrapperForArchitectures(
                        directPrimaryArch, directExtraArchs, CompileBridgeForArch, logger, out _);
                    logger.LogInformation(
                        "Apple direct: compiled SwiftUI bridge primary slice ({Count} file(s)).",
                        directBridgeFiles.Count);
                }
                catch (Exception bridgeEx)
                {
                    logger.LogWarning(
                        "Apple direct: SwiftUI bridge compilation failed (non-fatal — main bindings unaffected): {Message}",
                        bridgeEx.Message);
                }
            }
        }

        // Gap 2: classify the source framework's native linkage ONCE, before the mixed ObjC
        // pipeline runs. When it's a static `ar` archive, the Swift wrapper force-loaded it (sole
        // carrier) so the source xcframework MUST be dropped from every consumer reference/pack
        // site — re-linking the same ObjC classes would duplicate-register them. This single
        // signal feeds the mixed companion emitter (so its own NativeReference follows the same
        // policy), the binding-project/consumer-targets emitters below, and the SDK's reference
        // targets (_SwiftBindingSourceNativeLinkage).
        var sourceNativeLinkage = resolution != null
            ? NativeLinkageProbe.Detect(resolution.DylibPath, new SystemCommandRunner(), logger)
            : NativeLinkage.Dynamic;
        if (sourceNativeLinkage == NativeLinkage.Static)
            logger.LogInformation(
                "Source framework '{Module}' has static native linkage — wrapper is the sole carrier; " +
                "source xcframework will be dropped from consumer references.",
                resolution?.ModuleName);
        // The wrapper is the carrier whose presence decides the static-source drop. Use the
        // "will be produced" intent (wouldCompileWrapper) OR an already-built wrapper on disk —
        // the same signal the consumer-targets emitter uses — because under the SDK two-pass flow
        // the wrapper isn't compiled yet when the companion csproj is emitted.
        var wrapperWillExist = wouldCompileWrapper
            || (compilationResult?.XCFrameworkPath != null
                && Directory.Exists(compilationResult.XCFrameworkPath));

        // Run mixed framework ObjC pipeline (after Swift bindings generated, before project emission).
        // The companion is a managed-only assembly embedded into the Swift binding's single package
        // (one xcframework, one package), so it carries no independent PackageVersion to lockstep.
        ObjCPipelineResult? mixedObjcResult = null;
        if (hasXcframework && resolution != null && mixedObjcResolution != null
            && mixedParse != null && mixedParse.Module != null)
        {
            // Parse succeeded — a systemic failure would have aborted before Swift generation
            // (above), so the module is non-null here. Reuse that parse (clang ran once). The Swift
            // pass has now written swift-types.json, so the exclude set is available to dedup
            // Swift-owned types out of the companion. FilterAndEmit runs the mixed-only filters
            // (Swift-owned class removal, foreign-category projection) and emits the companion.
            var swiftTypeNames = BindingsGenerator.CollectSwiftEmittedTypeNames(outputDirectory);
            mixedObjcResult = ObjCPipeline.FilterAndEmit(
                mixedParse.Module, mixedParse.ResolvedNamespace, mixedParse.PlatformInfo,
                mixedParse.Diagnostics, mixedObjcResolution, xcframeworkPath!, outputDirectory,
                logger, packageId: null, sdkMode: sdkMode, isMixed: true,
                excludeTypeNames: swiftTypeNames,
                sourceNativeLinkage: sourceNativeLinkage,
                hasWrapperXCFramework: wrapperWillExist);
            // Fail closed: the framework HAS an ObjC surface (mixedObjcResolution != null), so a
            // non-zero pipeline exit means we tried to bind a known ObjC surface and failed. Do
            // NOT silently degrade to a Swift-only package — that drops the ObjC types with no
            // diagnostic AND bypasses SWIFTBIND039 (which only fires when metadata still says
            // "Mixed"). Propagate the exit code (mirroring the pure-ObjC path's
            // `context.ExitCode = objcResult.ExitCode` in the SwiftModuleNotFound/StaticLibrary
            // catch above) so the Nuke gate's --strict/--permissive layer decides severity.
            if (ShouldAbortForFailedMixedObjC(mixedObjcResult))
            {
                logger.LogError(
                    "ObjC pipeline for mixed framework failed (exit {Code}); refusing to emit a " +
                    "Swift-only binding that would silently drop the ObjC surface. {Msg}",
                    mixedObjcResult.ExitCode, mixedObjcResult.ErrorMessage ?? "(no detail)");
                context.ExitCode = mixedObjcResult.ExitCode;
                return;
            }
        }

        // Emit binding project files (xcframework mode only)
        if (hasXcframework && resolution != null)
        {
            try
            {
                // Extract framework metadata for project emission. A genuinely unreadable
                // framework throws here and hits this block's fatal-error handling.
                var metadata = XCFrameworkMetadataExtractor.Extract(
                    resolution.DylibPath, resolution.XCFrameworkPath,
                    resolution.ModuleName, logger, platformInfo: platformInfo);

                var wrapperXcfwPath = compilationResult?.XCFrameworkPath;
                var hasWrapperXcfw = wrapperXcfwPath != null && Directory.Exists(wrapperXcfwPath);
                var effectivePackageId = packageId ?? platformInfo.GetDefaultSwiftPackageId(resolution.ModuleName);
                var wrapperModuleName = $"{resolution.ModuleName}SwiftBindings";

                // sourceNativeLinkage was classified once above (before the mixed ObjC pipeline)
                // so the companion emitter and these binding-project/consumer-targets emitters
                // share one probe and one drop decision (Gap 2).

                // Mixed requires a zero-exit pipeline AND at least one ObjC class, protocol, or
                // category after filtering (see IsMixedFramework for the deliberate "zero types
                // → Swift-only" decision).
                bool isMixed = IsMixedFramework(mixedObjcResult);
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
                string? appleSupplementPrototypeCsproj = EmitAppleSupplementPrototype(
                    appleSupplementPrototypeDir, platformInfo, swiftRuntimeVersion,
                    metadata.EffectiveMinimumOSVersion, logger);

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
                    appleSupplementPrototypeCsprojPath: appleSupplementPrototypeCsproj,
                    sourceNativeLinkage: sourceNativeLinkage);

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
                        SourceNativeLinkage = sourceNativeLinkage,
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
                    SourceNativeLinkage = sourceNativeLinkage,
                    PlatformInfo = platformInfo,
                    ResourceBundleNames = resourceBundleNames,
                    // Mixed only: lets the local .ProjectReference.targets inject a <Reference> to
                    // the ObjC companion so PR consumers' C# sees the ObjC types (path c). Null for
                    // Swift-only/pure-ObjC bindings, so no companion reference target is emitted.
                    ObjCCompanionProjectFileName = objcProjFileName,
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
            // SDK mode pins a fixed "15.0" min-OS floor (Apple-supplement decoupling) and catches a
            // prototype failure: the apple-supplement.props version signal emitted below drives the
            // SDK's PackageReference injection and must still run even if the prototype can't be built.
            string? directSdkPrototypeCsproj = null;
            try
            {
                directSdkPrototypeCsproj = EmitAppleSupplementPrototype(
                    appleSupplementPrototypeDir, platformInfo, swiftRuntimeVersion, "15.0", logger);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to emit Apple-supplement prototype: {Message}", ex.Message);
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
                    tbdPath, xcframeworkPath: "", directModuleName, logger, platformInfo: platformInfo);

                var hasWrapperXcfw = compilationResult?.XCFrameworkPath != null
                    && Directory.Exists(compilationResult.XCFrameworkPath);
                var wrapperModuleName = $"{directModuleName}SwiftBindings";
                var directPackageId = packageId ?? platformInfo.GetDefaultSwiftPackageId(directModuleName);

                // Detect the SwiftUI bridge the direct-mode COMPILE step built inline (above). Without
                // this the consumer .targets hardcoded HasBridgeXCFramework=false, dropping the
                // {Module}Bridge NativeReference so any generated bridge view threw
                // DllNotFoundException("<Module>Bridge") at runtime even though the dylib was packed.
                var directBridgeModuleName = $"{directModuleName}Bridge";
                var directBridgeXcfwPath = Path.Combine(outputDirectory, $"{directBridgeModuleName}.xcframework");
                var directHasBridgeSwift = SwiftWrapperCompiler.CollectBridgeSwiftFiles(outputDirectory).Count > 0;
                var directHasBridgeXcfw = Directory.Exists(directBridgeXcfwPath);

                var projectFrameworkName = BindingsGenerator.InferFrameworkName(tbdPath, directModuleName);
                var projectResolver = new NamespacePatternResolver(effectiveNamespacePattern, projectFrameworkName);

                // Direct mode has no binding-metadata.props, so the prototype csproj only feeds
                // the generator-emitted consumer csproj path below — no SDK-target handoff.
                string? directPrototypeCsproj = EmitAppleSupplementPrototype(
                    appleSupplementPrototypeDir, platformInfo, swiftRuntimeVersion,
                    metadata.EffectiveMinimumOSVersion, logger);

                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = outputDirectory,
                    ModuleName = directModuleName,
                    Metadata = metadata,
                    SourceXCFrameworkPath = null,
                    WrapperXCFrameworkPath = hasWrapperXcfw ? compilationResult!.XCFrameworkPath : null,
                    BridgeXCFrameworkPath = directHasBridgeXcfw ? directBridgeXcfwPath : null,
                    HasBridgeSwift = directHasBridgeSwift,
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
                    HasBridgeXCFramework = directHasBridgeSwift,
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
    /// Emits the Apple-supplement prototype csproj for the resolved supplement references and
    /// returns its path, or <c>null</c> when prototype emission wasn't requested (no
    /// <c>--apple-supplement-prototype-dir</c>) or nothing referenced a supplement type.
    /// Centralizes the three otherwise byte-identical emit blocks (xcframework, SDK, direct) so
    /// their <see cref="AppleSupplementPrototypeEmitter.Options"/> can no longer silently drift
    /// apart. The minimum-OS floor differs per mode (SDK mode pins a fixed floor by design — see
    /// the Apple-supplement decoupling), so it stays an explicit caller-supplied parameter rather
    /// than being baked in. Error handling is the caller's choice: the xcframework/direct paths let
    /// a failure propagate (loud), while the SDK path catches it to preserve the version-signal
    /// props emission that must still run.
    /// </summary>
    private static string? EmitAppleSupplementPrototype(
        string? appleSupplementPrototypeDir,
        PlatformInfo platformInfo,
        string? swiftRuntimeVersion,
        string? minimumOSVersion,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(appleSupplementPrototypeDir) || !AppleSupplementReferences.Any)
            return null;

        var protoResult = AppleSupplementPrototypeEmitter.Emit(new AppleSupplementPrototypeEmitter.Options
        {
            PrototypeDirectory = appleSupplementPrototypeDir!,
            ReferencedIdentities = AppleSupplementReferences.Current,
            PlatformInfo = platformInfo,
            SwiftRuntimeVersion = swiftRuntimeVersion,
            MinimumOSVersion = minimumOSVersion,
        }, logger);
        return protoResult.CsprojPath;
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
    /// Returns true when <c>--link-framework</c> or <c>--link-library</c> was supplied without
    /// <c>--xcframework</c>. Those flags declare system frameworks/libraries for the wrapper link
    /// of a force-loaded static-archive source, which only exists on the <c>--xcframework</c>
    /// path; in <c>-a/-d/-t</c> direct mode there is no wrapper link to consume them. The CLI
    /// fails closed on this combination rather than silently dropping the author's declared
    /// dependencies (which would yield a wrapper that fails to resolve symbols at load). Empty
    /// arrays do not trip the guard. Pulled out as a small helper so the gate is unit-testable.
    /// </summary>
    internal static bool LinkDependenciesSuppliedWithoutXcframework(
        bool hasXcframework, string[]? linkFrameworks, string[]? linkLibraries)
        => !hasXcframework
            && (((linkFrameworks?.Length ?? 0) > 0) || ((linkLibraries?.Length ?? 0) > 0));

    /// <summary>
    /// Computes the library name baked into generated <c>[LibraryImport]</c> and
    /// <c>LoadFromSymbol</c> strings. For an Apple <b>system</b> framework target the embedded
    /// name is reduced to the bare framework/module name (e.g. <c>"CryptoKit"</c>) rather than
    /// the <c>@rpath/Name.framework/Name</c> install path. Non-system bindings (user
    /// xcframeworks, app-bundled dylibs) are returned unchanged — their install name IS
    /// <c>@rpath/Name.framework/Name</c> and resolves directly on device.
    ///
    /// Why bare names for system frameworks: <c>Swift.Runtime.SwiftFrameworkResolver</c> is
    /// registered for the binding assembly from its <c>[ModuleInitializer]</c> and maps a bare
    /// name through an ordered search list whose last entry is
    /// <c>/System/Library/Frameworks/Name.framework/Name</c>, so the framework resolves on a
    /// physical device. On NativeAOT the per-assembly DllImport resolver is NOT consulted for
    /// an already dyld-style <i>path</i> name — the runtime hands it straight to dyld — so a
    /// <c>[LibraryImport("@rpath/Name.framework/Name")]</c> metadata accessor throws
    /// <see cref="System.DllNotFoundException"/> on device (the system framework is not
    /// reachable via <c>@rpath</c> there) even though it resolves on the simulator. This bites
    /// a generic type's <c>$s…Ma</c> accessor in particular: unlike a non-generic type it has
    /// no <c>@_cdecl</c> wrapper-DLL primary to fall back from, so the raw <c>@rpath</c> import
    /// is the only path and it fails. Bare names also avoid the macios linker's
    /// <c>.framework/</c> substring scan that force-adds <c>-framework X</c> (BlastRadius #9).
    /// This mirrors the Apple supplement's <c>AppleTypesCsEmitter.ResolveLibraryPath</c>, which
    /// already emits bare system-framework names for the same reasons.
    /// </summary>
    internal static string ResolveRuntimeLibraryName(
        string runtimeLibraryName, string? moduleName, bool isSystemFrameworkTarget)
        => isSystemFrameworkTarget && !string.IsNullOrEmpty(moduleName)
            ? moduleName
            : runtimeLibraryName;

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

    /// <summary>
    /// Fail-closed gate for the mixed-framework ObjC pipeline. The caller only runs the pipeline
    /// when the framework HAS a detected ObjC surface, so a non-zero pipeline exit means we tried
    /// to bind a known ObjC surface and failed. When this returns true, <c>Execute</c> propagates
    /// the pipeline's exit code and refuses to emit a binding at all — silently degrading to a
    /// Swift-only package would drop the ObjC types with no diagnostic AND bypass
    /// <c>SWIFTBIND039</c> (which only fires when the emitted metadata still says "Mixed").
    /// A null result means the pipeline never ran (not a mixed framework) → never abort.
    /// </summary>
    internal static bool ShouldAbortForFailedMixedObjC(ObjCPipelineResult? mixedObjcResult)
        => mixedObjcResult != null && mixedObjcResult.ExitCode != 0;

    /// <summary>
    /// Selects the platform-appropriate framework search path (<c>-F</c> slice dir) for each
    /// resolved dependency, so a cross-framework <c>#import</c> in an ObjC umbrella header resolves
    /// during the clang AST dump. Falls back to the opposite slice when only one platform variant
    /// was resolved (a device-only / simulator-only dependency). Used on the mixed-framework path,
    /// where the full <see cref="FrameworkDependencyInfo"/> set (manual + auto-detected) is known.
    /// </summary>
    internal static IReadOnlyList<string> SelectObjCDependencySearchPaths(
        IReadOnlyList<FrameworkDependencyInfo>? resolvedDependencies,
        XCFrameworkPlatformTarget platformTarget)
    {
        var paths = new List<string>();
        if (resolvedDependencies == null) return paths;
        foreach (var dep in resolvedDependencies)
        {
            var primary = platformTarget == XCFrameworkPlatformTarget.Device
                ? dep.DeviceFrameworkSearchPath
                : dep.SimulatorFrameworkSearchPath;
            var fallback = platformTarget == XCFrameworkPlatformTarget.Device
                ? dep.SimulatorFrameworkSearchPath
                : dep.DeviceFrameworkSearchPath;
            var chosen = primary ?? fallback;
            if (!string.IsNullOrEmpty(chosen)) paths.Add(chosen!);
        }
        return paths;
    }

    /// <summary>
    /// Resolves each <c>--framework-dependency</c> xcframework to its platform-appropriate slice
    /// directory for use as an ObjC clang <c>-F</c> path. Used on the pure-ObjC generation paths,
    /// which return before the full dependency resolution runs (those paths have no Swift
    /// resolution to anchor it). Unparseable / slice-less dependencies are skipped (best-effort,
    /// matching the sibling resolver).
    /// </summary>
    internal static IReadOnlyList<string> ResolveObjCDependencySliceDirs(
        string[]? frameworkDependencies,
        XCFrameworkPlatformTarget platformTarget,
        ILogger logger,
        PlatformInfo? platformInfo)
    {
        var paths = new List<string>();
        if (frameworkDependencies == null) return paths;
        foreach (var dep in frameworkDependencies)
        {
            var slice = XCFrameworkResolver.TryResolveSliceSearchPath(dep, platformTarget, logger, platformInfo);
            if (slice != null) paths.Add(slice);
        }
        return paths;
    }

    /// <summary>
    /// Merges explicit/resolved dependency <c>-F</c> slice dirs with auto-detected sibling framework
    /// search paths into one ordered, normalized, de-duplicated list for the ObjC clang AST dump.
    /// Dependency paths lead, siblings follow: clang searches <c>-F</c> directories left-to-right and
    /// takes the first match, so a deliberately-declared <c>--framework-dependency</c> must outrank an
    /// incidental co-located sibling that happens to export the same module name. De-duplication (via
    /// <see cref="Path.GetFullPath(string)"/>) collapses a <c>--framework-dependency</c> that is also a
    /// co-located sibling onto its first (dependency) position.
    /// </summary>
    internal static IReadOnlyList<string> MergeObjCFrameworkSearchPaths(
        IReadOnlyList<string> siblingSearchPaths,
        IEnumerable<string> dependencySearchPaths)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? p)
        {
            if (string.IsNullOrEmpty(p)) return;
            var full = Path.GetFullPath(p);
            if (seen.Add(full)) ordered.Add(full);
        }
        foreach (var p in dependencySearchPaths) Add(p);
        foreach (var p in siblingSearchPaths) Add(p);
        return ordered;
    }

    /// <summary>
    /// Finding 50 fail-closed gate: under <c>--strict-inputs</c> (the CI compile gate's strict
    /// mode), a generation whose input edge degraded — a device→simulator slice fallback, a
    /// missing swiftinterface, an ABI-JSON fallback, an ambiguous TBD pick, or a degraded
    /// auto-detected dependency — must fail rather than ship a silently-shrunk API surface. The
    /// degradations are recorded on the ambient <see cref="InputResolutionReport"/> during
    /// resolution and dependency parsing; this predicate is the single decision point so a future
    /// refactor of <c>Execute</c> can't quietly turn the gate back into warn-and-continue.
    /// </summary>
    internal static bool ShouldFailClosedOnDegradedInputs(bool strictInputs, bool hasDegradations)
        => strictInputs && hasDegradations;

    /// <summary>
    /// Finding 50: when <c>--strict-inputs</c> is set and at least one input-resolution degradation
    /// was recorded, logs the SWIFTBIND027 diagnostics (one per degradation plus a summary) and
    /// returns <c>true</c> so the caller aborts the generation with a non-zero exit rather than
    /// reporting success on a silently-narrowed binding. Returns <c>false</c> otherwise (the
    /// graceful, exit-0 path). Every generation route calls this, so the fail-closed guarantee has
    /// no per-path holes, but the gate sits at a different point per route: the <c>--objc</c>-forced
    /// pure-ObjC path and the Swift-resolution-failed ObjC fallback gate BEFORE emission (before
    /// <c>ObjCPipeline.Run</c>), whereas the Swift path gates AFTER <c>GenerateBindings</c> has
    /// already emitted the C# and persisted the manifest — but still before the downstream
    /// compilation/packaging phases that would consume and ship it, so a degraded Swift binding is
    /// never built or packed. The per-degradation SWIFTBIND027 error lines (not a persisted file)
    /// are the authoritative decision list the summary line points to, because not every route
    /// writes a manifest: the pure-ObjC routes emit no <see cref="BindingArtifactManifest"/>, so
    /// only the Swift path also persists the inputResolution section to binding-artifact-manifest.json.
    /// </summary>
    internal static bool EmitStrictInputsFailureIfDegraded(bool strictInputs, ILogger logger)
    {
        if (!ShouldFailClosedOnDegradedInputs(strictInputs, InputResolutionReport.HasDegradations))
            return false;

        foreach (var decision in InputResolutionReport.Decisions)
        {
            if (decision.Severity == InputResolutionSeverity.Degradation)
                logger.LogError("SWIFTBIND027: degraded input resolution ({Category}): {Detail}", decision.Category, decision.Detail);
        }
        logger.LogError("SWIFTBIND027: input resolution degraded under --strict-inputs; failing the generation. Each degraded input is listed in the SWIFTBIND027 entries above.");
        return true;
    }

    /// <summary>
    /// Finding 50: records every auto-detected-but-unresolved companion dependency as a
    /// <see cref="InputResolutionCategory.Dependency"/> degradation and emits the consumer-facing
    /// warning. An unresolved dependency shrinks the API surface exactly like one that resolves but
    /// fails to parse (<see cref="Program"/> records that case) — its types resolve to
    /// <c>AnyType</c> and secondary gates prune the members that reference them — so it must be a
    /// recorded degradation that <c>--strict-inputs</c> can escalate to a hard failure (SWIFTBIND027)
    /// rather than a silent exit-0 on a narrowed binding. This recording happens before the
    /// fail-closed gate at the call site so the degradation is counted. Only genuine <c>@rpath</c>
    /// companion frameworks reach here: <see cref="BinaryDependencyAnalyzer.ParseOtoolOutput"/>
    /// already filters system/OS-resident frameworks (absolute <c>/System/...</c> paths and
    /// <c>/usr/lib/swift</c>), so this does not fire on Apple SDK linkage. Returns the count recorded.
    /// </summary>
    internal static int RecordUnresolvedDependencyDegradations(
        IReadOnlyList<DetectedDependency> unresolvedDependencies, ILogger logger)
    {
        foreach (var unresolved in unresolvedDependencies)
        {
            var reason = unresolved.UnresolvedReason ?? "missing-xcframework";
            InputResolutionReport.RecordDegradation(
                InputResolutionCategory.Dependency,
                $"Auto-detected dependency '{unresolved.FrameworkName}' could not be resolved to an xcframework " +
                $"({reason}); its types will resolve to AnyType and dependent members will be pruned.");
            logger.LogWarning("{Message}",
                BindingsGenerator.FormatDependencyWarning(unresolved.FrameworkName, reason));
        }
        return unresolvedDependencies.Count;
    }

    /// <summary>
    /// Finding 50: records a <see cref="InputResolutionCategory.Dependency"/> degradation when
    /// automatic dependency analysis failed systemically. <see cref="BinaryDependencyAnalyzer.Analyze"/>
    /// returns <c>null</c> ONLY on a non-zero <c>otool -L</c> exit (an empty-but-successful scan
    /// returns a non-null result with empty lists), so a <c>null</c> result means EVERY companion
    /// dependency is invisible and the API surface silently shrinks (dependent types resolve to
    /// <c>AnyType</c>). Mirroring Finding 63's tri-state probe philosophy, a systemic analysis failure
    /// is itself a degradation — not a clean "no dependencies" — so <c>--strict-inputs</c> fails closed
    /// instead of exiting 0 on a silently-narrowed binding.
    /// </summary>
    internal static void RecordSystemicDependencyAnalysisFailure(ILogger logger)
    {
        InputResolutionReport.RecordDegradation(
            InputResolutionCategory.Dependency,
            "Automatic dependency analysis failed (otool -L returned non-zero); companion " +
            "dependencies could not be detected and any dependent types will resolve to AnyType, " +
            "pruning dependent members.");
        logger.LogWarning(
            "Automatic dependency analysis failed; dependent members may be silently pruned.");
    }

    /// <summary>
    /// Classifies a framework as "Mixed" (Swift API + an embedded ObjC companion) iff the ObjC
    /// pipeline both <b>succeeded</b> (exit 0) AND produced at least one bindable ObjC class,
    /// protocol, or category <i>after</i> mixed-framework filtering. A zero-exit run that filtered
    /// down to zero bindable types is deliberately treated as a plain Swift framework — there is no
    /// managed ObjC surface to embed, so emitting a companion (and its <c>SWIFTBIND039</c> contract)
    /// would be spurious. This is the same predicate that decides <c>frameworkType</c> and whether
    /// an <c>objcProjectName</c> is recorded, so the companion-embed machinery and the metadata
    /// agree by construction. Callers that detected an ObjC surface but reach here with a non-zero
    /// exit are handled earlier by <see cref="ShouldAbortForFailedMixedObjC"/>.
    /// </summary>
    internal static bool IsMixedFramework(ObjCPipelineResult? mixedObjcResult)
        => mixedObjcResult?.ExitCode == 0
           && (mixedObjcResult.Module?.Classes.Count > 0
               || mixedObjcResult.Module?.Protocols.Count > 0
               || mixedObjcResult.Module?.Categories.Count > 0);

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
        Console.WriteLine("  --package-id         Optional. Package ID for NuGet packaging. Default: '{Module}.Swift.{Platform}' (e.g. ImagePipeline.Swift.iOS, ImagePipeline.Swift.macOS).");
        Console.WriteLine("  --swift-runtime-version  Optional. SwiftBindings.Runtime version for the emitted .csproj. Default '0.0.0-dev' is local-dev only (IsPackable=false). Pass a published version to enable 'dotnet pack'.");
        Console.WriteLine("  --wrapper-architectures  Optional. Wrapper compilation scope: 'simulator' (default), 'device', or 'all'.");
        Console.WriteLine("  --framework-dependency   Optional. Repeatable. Path to dependency xcframework for -F search paths. Requires --xcframework.");
        Console.WriteLine("  --link-framework     Optional. Repeatable. Apple system framework to link into the wrapper (e.g. 'CoreVideo'). Emits '-framework <name>' so a force-loaded static-archive source can resolve system-framework deps that carry no autolink hints. Requires --xcframework.");
        Console.WriteLine("  --link-library       Optional. Repeatable. System library to link into the wrapper by linker name (e.g. 'c++' for libc++). Emits '-l<name>'. Use alongside --link-framework when a static-archive source pulls in C++/library symbols. Requires --xcframework.");
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
        Console.WriteLine("  --interface-facts-producer  'auto' (default) or 'swift-syntax'. Both shell out to the SwiftInterfaceParser host binary (macOS-only); the legacy 'regex' producer was removed.");
        Console.WriteLine("  -v, --verbose        Verbosity level. 0 = No logging, 1 = General information, 2 = Debugging information. (default: 1)");
    }

    /// <summary>
    /// Validates the <c>--interface-facts-producer</c> flag. The two retained values both map to the
    /// same SwiftSyntax host aggregator (<see cref="InterfaceFactsAggregator"/>), which
    /// <c>BindingsGenerator.GenerateBindings</c> builds lazily — so this is a pure predicate, not a
    /// constructor:
    /// <list type="bullet">
    /// <item><c>auto</c> (default) and <c>swift-syntax</c>: the SwiftSyntax host producer. This
    /// generator is macOS-only by design — both values require the SwiftInterfaceParser host binary
    /// and hard-fail (inside <c>GenerateBindings</c>' structured failure path) on non-Darwin or when
    /// it cannot be located. The two values are kept distinct for backward compatibility but behave
    /// identically.</item>
    /// </list>
    /// The legacy <c>regex</c> producer was removed; <c>regex</c> (or any other value) returns
    /// <see langword="false"/> so the caller can reject it — silent fallback would defeat the
    /// explicit-switch design. <c>internal</c> (not <c>private</c>) so the unit-test assembly can
    /// pin the accept/reject set directly, matching the sibling CLI predicates
    /// (<see cref="IsValidPlatformVersion"/>, <see cref="ShouldFailClosedOnDegradedInputs"/>).
    /// </summary>
    internal static bool IsValidInterfaceFactsProducer(string flag) =>
        flag is "auto" or "swift-syntax";
}
