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
        var noVerifyCSharp = parseResult.GetValueForOption(options.NoVerifyCSharp);
        var verificationPackageFeed = parseResult.GetValueForOption(options.VerificationPackageFeed);
        var emitInputGraphPath = parseResult.GetValueForOption(options.EmitInputGraph);
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
        var consumerProject = parseResult.GetValueForOption(options.ConsumerProject);
        var emitAppleTypesManifest = parseResult.GetValueForOption(options.EmitAppleTypesManifest);
        var appleAbiJsonPaths = parseResult.GetValueForOption(options.AppleAbiJson);
        var appleIncludeTypes = parseResult.GetValueForOption(options.AppleIncludeTypes);
        var appleVersion = parseResult.GetValueForOption(options.AppleVersion) ?? CliOptions.DefaultAppleSupplementVersion;
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
                AutoDepResolver.Run(autoDepSpec, explicitDeps, Console.Out, consumerProject);
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

        // The Apple-type surface index verifies every ObjC-bridged reference against the reference
        // assembly that actually ships for the target platform. Record the platform now, before any
        // downstream ingest can read AppleTypeSurfaceIndex.Default, so a macOS/tvOS/MacCatalyst run
        // is checked against its own surface rather than Microsoft.iOS.
        AppleTypeSurfaceIndex.SetAmbientPlatform(platformInfo.Platform);

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
                // The ObjC module identity is resolved here, so the abort writes the structured
                // failure report like every other nonzero exit with a known module and inputs.
                if (FailStrictInputsWithReport(
                        strictInputs, objcResolution.ModuleName,
                        PureObjCFailureInputs(objcResolution, platformInfo), outputDirectory, logger))
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
                // A1: make the ObjC drop set visible. A pure-ObjC binding runs no Swift generation
                // pass (no manifest yet), so persist one carrying the ObjC skip section — otherwise
                // these drops live only in an INFO log and never reach binding-report.json's gate.
                if (objcResult.ExitCode == 0)
                {
                    PersistPureObjCSkipReport(outputDirectory, objcResolution.ModuleName, objcResult.Diagnostics, logger);
                    // Fail-closed C# verification, the same gate as the Swift wrapper/direct paths: a
                    // pure-ObjC binding also emits a consumer-facing csproj (ObjCBindingProjectEmitter),
                    // so emitted C# that doesn't compile must fail publication here rather than surface
                    // first in the consumer's build. SDK mode and the --no-verify-csharp opt-out are
                    // excluded to match the Swift gate; there is no wrapper-compile term because a
                    // pure-ObjC binding builds no Swift wrapper.
                    if (!sdkMode && !noVerifyCSharp && objcResult.ProjectPath is { } objcCsproj
                        && !VerifyGeneratedCSharp(objcCsproj, logger))
                    {
                        BindingsGenerator.EmitFatalExitReport(
                            objcResolution.ModuleName,
                            BindingFailureOutcomeKind.CSharpVerificationFailure,
                            "CSHARP_VERIFICATION_FAILURE", RecoveryStage.CSharpCompile,
                            "The generated C# failed in-generator compile verification; " +
                            "see the C# verification diagnostics above.",
                            PureObjCFailureInputs(objcResolution, platformInfo), outputDirectory, logger);
                        context.ExitCode = 1;
                        return;
                    }
                }
                // A nonzero pipeline exit propagates directly, so leave the structured report here —
                // the ObjC module identity is known and no later path will write one.
                ReportPureObjCPipelineFailure(
                    objcResult, objcResolution.ModuleName,
                    PureObjCFailureInputs(objcResolution, platformInfo), outputDirectory, logger);
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
                // The ObjC module identity is resolved here, so the abort writes the structured
                // failure report like every other nonzero exit with a known module and inputs.
                if (FailStrictInputsWithReport(
                        strictInputs, objcResolution.ModuleName,
                        PureObjCFailureInputs(objcResolution, platformInfo), outputDirectory, logger))
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
                // A1: make the ObjC drop set visible. A pure-ObjC binding runs no Swift generation
                // pass (no manifest yet), so persist one carrying the ObjC skip section — otherwise
                // these drops live only in an INFO log and never reach binding-report.json's gate.
                if (objcResult.ExitCode == 0)
                {
                    PersistPureObjCSkipReport(outputDirectory, objcResolution.ModuleName, objcResult.Diagnostics, logger);
                    // Fail-closed C# verification, the same gate as the Swift wrapper/direct paths: a
                    // pure-ObjC binding also emits a consumer-facing csproj (ObjCBindingProjectEmitter),
                    // so emitted C# that doesn't compile must fail publication here rather than surface
                    // first in the consumer's build. SDK mode and the --no-verify-csharp opt-out are
                    // excluded to match the Swift gate; there is no wrapper-compile term because a
                    // pure-ObjC binding builds no Swift wrapper.
                    if (!sdkMode && !noVerifyCSharp && objcResult.ProjectPath is { } objcCsproj
                        && !VerifyGeneratedCSharp(objcCsproj, logger))
                    {
                        BindingsGenerator.EmitFatalExitReport(
                            objcResolution.ModuleName,
                            BindingFailureOutcomeKind.CSharpVerificationFailure,
                            "CSHARP_VERIFICATION_FAILURE", RecoveryStage.CSharpCompile,
                            "The generated C# failed in-generator compile verification; " +
                            "see the C# verification diagnostics above.",
                            PureObjCFailureInputs(objcResolution, platformInfo), outputDirectory, logger);
                        context.ExitCode = 1;
                        return;
                    }
                }
                // A nonzero pipeline exit propagates directly, so leave the structured report here —
                // the ObjC module identity is known and no later path will write one.
                ReportPureObjCPipelineFailure(
                    objcResult, objcResolution.ModuleName,
                    PureObjCFailureInputs(objcResolution, platformInfo), outputDirectory, logger);
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
                logger.LogInformation("Auto-setting --async-library to '{Module}'.", wrapperModuleName);
            }
        }

        // Validate and resolve --framework-dependency options.
        // Ordered BEFORE auto-detection on purpose: an explicit dependency overrides whatever
        // co-located artifact auto-detection would have picked for that module, so the closure has to
        // be seeded with the overriding artifact. Resolving it afterwards means the closure scans the
        // artifact that is about to be discarded — its transitive imports survive the merge below
        // (which only drops the overridden module itself) while the chosen artifact's own imports are
        // never discovered at all.
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
                // Validation failed — error already logged. The module and primary inputs are
                // resolved by now (xcframework mode), so leave the structured report.
                BindingsGenerator.EmitFatalExitReport(
                    resolution!.ModuleName, BindingFailureOutcomeKind.DependencyInputFailure,
                    "FRAMEWORK_DEPENDENCY_RESOLUTION", RecoveryStage.Parse,
                    "One or more --framework-dependency options failed to resolve: " +
                    string.Join(", ", frameworkDependencies!),
                    new BindingFailureInputPaths(
                        swiftAbiPath, dylibPath, tbdPath, swiftInterface,
                        platformInfo.Platform.ToString()),
                    outputDirectory, logger);
                context.ExitCode = 1;
                return;
            }
        }

        // Auto-detect dependencies from binary linkage (xcframework mode only)
        List<FrameworkDependencyInfo>? autoDetectedDeps = null;
        DependencyAnalysisResult? analysisResult = null;

        if (hasXcframework && !noAutoDetect)
        {
            // Iterated to a fixpoint rather than one pass over the primary: an auto-added dependency
            // brings its own public surface into the compile-import graph that has to close, so the
            // set of inputs the run needs is not knowable from the primary's link list alone.
            analysisResult = DependencyClosureResolver.ResolveToFixpoint(
                resolution!.DylibPath, xcframeworkPath!, resolution.ModuleName,
                resolution.SwiftInterfacePath,
                platformTarget,
                wrapperArchitectures?.ToLowerInvariant() ?? "simulator",
                logger, platformInfo: platformInfo,
                companionFrameworkPaths: frameworkDependencies,
                preResolvedDependencies: resolvedDependencies);
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

        // Merge auto-detected deps with manual deps (manual takes precedence). With the closure now
        // seeded from `resolvedDependencies`, a manual module can no longer be auto-proposed at all —
        // the guard stays as defence in depth, not as the mechanism.
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

        // Required-input validation. In xcframework mode the module identity is already resolved
        // (the resolver staged these paths), so a missing input still leaves the structured
        // failure report. In direct (-a/-d/-t) mode these fire before any module identity exists
        // — the ABI peek below needs a valid ABI file — so no report is written for them: the
        // report contract requires a KNOWN module, and inventing one would misattribute the run.
        void FailRequiredInput(string message, string? offendingPath)
        {
            logger.LogError("{Message}", message);
            if (resolution != null)
            {
                BindingsGenerator.EmitFatalExitReport(
                    resolution.ModuleName, BindingFailureOutcomeKind.RequiredInputMissing,
                    "REQUIRED_INPUT_MISSING", RecoveryStage.Parse,
                    offendingPath == null ? message : $"{message} (got: '{offendingPath}')",
                    new BindingFailureInputPaths(
                        swiftAbiPath, dylibPath, tbdPath, swiftInterface,
                        platformInfo.Platform.ToString()),
                    outputDirectory, logger);
            }
            context.ExitCode = 1;
        }

        if (string.IsNullOrWhiteSpace(swiftAbiPath) || !File.Exists(swiftAbiPath))
        {
            FailRequiredInput("Error: Valid Swift ABI file is required.", swiftAbiPath);
            return;
        }

        if (string.IsNullOrWhiteSpace(dylibPath) || !File.Exists(dylibPath))
        {
            FailRequiredInput("Error: Valid dynamic library is required.", dylibPath);
            return;
        }

        if (string.IsNullOrWhiteSpace(tbdPath) || !File.Exists(tbdPath))
        {
            FailRequiredInput("Error: Valid TBD file is required.", tbdPath);
            return;
        }

        if (!Directory.Exists(outputDirectory))
        {
            FailRequiredInput("Error: Valid output directory is required.", outputDirectory);
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
                    // Both modes have a module identity by now (resolver or ABI peek), so this
                    // pre-generation abort leaves the structured report too.
                    BindingsGenerator.EmitFatalExitReport(
                        resolution?.ModuleName ?? directModuleName,
                        BindingFailureOutcomeKind.DependencyInputFailure,
                        "SWIFTBIND070", RecoveryStage.Parse,
                        $"Module database not found: '{dbPath}'.",
                        new BindingFailureInputPaths(
                            swiftAbiPath, dylibPath, tbdPath, swiftInterface,
                            platformInfo.Platform.ToString()),
                        outputDirectory, logger);
                    context.ExitCode = 1;
                    return;
                }
            }
        }

        // Gap 2: classify the source framework's native linkage ONCE, HERE — before GenerateBindings
        // below bakes library names into the emitted C#. The probe is a pure function of the bytes at
        // resolution.DylibPath and nothing between resolution and the packaging decision at the end of
        // this method mutates that file, so hoisting it changes nothing except WHEN the answer is
        // known. It used to be probed only after generation, which is exactly why the emitter could
        // not see it: a static `ar` source is force-loaded into the wrapper and dropped from every
        // consumer reference and pack site, yet the emitted P/Invokes still named the vendor module,
        // so the binding imported a library the package does not ship (DllNotFoundException on
        // ordinary API use). The packaging sites at the end of this method reuse this same value.
        var sourceNativeLinkage = resolution != null
            ? NativeLinkageProbe.Detect(resolution.DylibPath, new SystemCommandRunner(), logger)
            : NativeLinkage.Dynamic;

        // Use the provided library name, or fall back to the dylib path
        var runtimeLibraryName = string.IsNullOrWhiteSpace(libraryName) ? dylibPath : libraryName;

        // The emission-time half of the static-merge decision. Derived from the SAME function that
        // gates packaging — ShouldIncludeSourceXcframework returning false IS "the wrapper is the sole
        // carrier" — rather than a parallel re-derivation that could drift from it. The carrier term is
        // the "will be produced" intent (wouldCompileWrapper), matching what the consumer-targets
        // emitter uses: under the SDK's two-pass flow the wrapper is not compiled yet when this
        // generate pass runs.
        var staticMergedModuleName =
            resolution != null
            && !string.IsNullOrWhiteSpace(asyncLibrary)
            && !NativePackagingPolicy.ShouldIncludeSourceXcframework(sourceNativeLinkage, wouldCompileWrapper)
                ? resolution.ModuleName
                : null;
        if (staticMergedModuleName != null)
            logger.LogInformation(
                "Source framework '{Module}' has static native linkage and is force-loaded into wrapper " +
                "'{Wrapper}' — emitted imports for its symbols will name the wrapper, which is the sole " +
                "runtime carrier.",
                staticMergedModuleName, asyncLibrary);

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
            // Late-validated flag: the module identity and inputs are already resolved on both
            // modes at this point, so the abort leaves the structured report too.
            BindingsGenerator.EmitFatalExitReport(
                resolution?.ModuleName ?? directModuleName,
                BindingFailureOutcomeKind.InvalidConfiguration,
                "INVALID_CONFIGURATION", RecoveryStage.Parse,
                $"Unknown --interface-facts-producer value '{interfaceFactsProducer}'. " +
                "Expected 'auto' or 'swift-syntax'.",
                new BindingFailureInputPaths(
                    swiftAbiPath, dylibPath, tbdPath, swiftInterface,
                    platformInfo.Platform.ToString()),
                outputDirectory, logger);
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
        if (ShouldAbortForFailedMixedObjC(mixedParse))
        {
            logger.LogError(
                "ObjC pipeline for mixed framework failed (exit {Code}); refusing to emit a " +
                "Swift-only binding that would silently drop the ObjC surface. {Msg}",
                mixedParse!.ExitCode, mixedParse.ErrorMessage ?? "(no detail)");
            // Pre-generation abort with a fully resolved module + inputs: leave the structured
            // report (the same outcome kind as the post-generation mixed-companion abort — both
            // mean the ObjC surface of a mixed framework could not be produced).
            BindingsGenerator.EmitFatalExitReport(
                resolution!.ModuleName, BindingFailureOutcomeKind.MixedObjCSurfaceFailure,
                "MIXED_OBJC_SURFACE_FAILURE", RecoveryStage.Parse,
                $"ObjC pipeline for mixed framework failed (exit {mixedParse.ExitCode}): " +
                (mixedParse.ErrorMessage ?? "(no detail)"),
                new BindingFailureInputPaths(
                    swiftAbiPath, dylibPath, tbdPath, swiftInterface,
                    platformInfo.Platform.ToString()),
                outputDirectory, logger);
            context.ExitCode = mixedParse.ExitCode != 0 ? mixedParse.ExitCode : 1;
            return;
        }

        // Verify-recover: for the default (simulator) xcframework generation path, hand GenerateBindings
        // a compile delegate so it can run the in-emission verify-recover loop — render the module under
        // a denylist, compile the promised simulator slice, attribute any failure to a leaf/accessor unit,
        // withdraw it, and re-render until the wrapper compiles clean or the module fails closed. The loop
        // settles the ON-DISK wrapper source; the command's post-loop compile below is unchanged and
        // recompiles that settled source (primary + extra-arch fold, stripped-symbol reconcile,
        // WrapperSection) exactly as for a non-recovered binding, so the shipped artifact and manifest are
        // byte-identical for a healthy module. The delegate reuses the already-resolved slice (the denylist
        // never changes resolution) and never re-resolves, so it adds no input-resolution decisions; the
        // loop additionally snapshots and restores that report so the finalized manifest is unaffected.
        // Device/`all` wrapper-arch modes keep today's single-emission path (delegate null): wave-1
        // verify-recover covers the simulator slice, and a device-only failure still fails the build closed
        // through the unchanged post-loop compile.
        Func<WrapperRecoveryCompileRequest, WrapperCompileDiagnostics>? verifyRecoverCompile = null;
        if (shouldCompileWrapper && resolution != null &&
            (wrapperArchitectures?.ToLowerInvariant() ?? "simulator") == "simulator")
        {
            var loopResolution = resolution;
            var loopSimDepPaths = resolvedDependencies?
                .Where(d => d.SimulatorFrameworkSearchPath != null)
                .Select(d => d.SimulatorFrameworkSearchPath!)
                .ToList();
            loopSimDepPaths = XCFrameworkResolver.MergeWrapperDependencySearchPaths(
                loopSimDepPaths, xcframeworkPath!, XCFrameworkPlatformTarget.Simulator, logger, platformInfo);

            verifyRecoverCompile = req =>
            {
                var collector = new WrapperSliceCollector();
                var result = SwiftWrapperCompiler.Compile(
                    req.OutputDirectory, loopResolution.ModuleName,
                    loopResolution.FrameworkSearchPath, loopResolution.DylibPath, logger,
                    internalTypeNames: req.InternalTypeNames,
                    additionalFrameworkSearchPaths: loopSimDepPaths,
                    platformInfo: platformInfo,
                    moduleNameForCollision: req.ModuleNameForCollision,
                    nestedTypesInCollidingClass: req.NestedTypesInCollidingClass,
                    swiftInterfacePath: loopResolution.SwiftInterfacePath,
                    skipThunkCompilation: skipThunkCompilation,
                    resolvedArchitecture: loopResolution.SelectedArchitecture,
                    depModuleNamesForCollision: req.DepModuleCollisions.Simulator,
                    linkFrameworks: linkFrameworks,
                    linkLibraries: linkLibraries,
                    collector: collector);
                return collector.ToDiagnostics(result);
            };
        }

        // C# verify-recover. In xcframework mode this extends the Swift loop above into a JOINT
        // fixed-point over both planes; in Apple system-framework direct mode — which has no
        // in-generation wrapper compile to hang a loop on, its wrapper being built from the on-device SDK
        // slice after emission returns — it IS the loop. Either way this delegate emits the binding csproj
        // for the CURRENT render and runs the authoritative MSBuild+SARIF C# verifier; a C# compile error
        // is attributed (via the C#-plane interval map) to a leaf/accessor recovery unit and fed into the
        // monotonic denylist, so the next round re-renders, drops the C# culprit, and re-verifies. The
        // command's unchanged post-loop csproj emit + VerifyGeneratedCSharp ship gate below then run over
        // the settled source, so the loop only ever REDUCES what reaches that fail-closed gate.
        // Enabled when the emitted C# is companion-free (CanVerifyCSharpInLoop): the in-loop verification
        // csproj sets ObjCProjectFileName = null, so a binding whose C# references a bridged ObjC companion
        // assembly (built only AFTER GenerateBindings returns) can't be verified in-loop and keeps the
        // post-loop publication gate (fail-closed) unchanged. A "potential mixed" framework whose ObjC
        // bridge filtered to zero records (an umbrella header re-exporting only Swift) emits no companion
        // reference and IS verified in-loop.
        //
        // The mode gate is "this run will emit a consumer-facing binding csproj the verifier can build,
        // and the publication gate will grade it" — the two conditions the two emitting branches at the
        // end of this method carry. For xcframework mode the wrapper loop's own precondition already
        // implies it; for direct mode it is the system-framework target plus the same wrapper-compile
        // intent that gates the post-loop verification there. A direct run that is NOT a system-framework
        // target emits no csproj at all, so there is nothing to verify and nothing to recover into.
        // The architecture test is a positive allowlist, not "anything but 'all'": the argument is only
        // validated after GenerateBindings returns, so a bogus token would otherwise spend a whole
        // verify-recover loop before the command rejects it. Only the two single-slice values this mode
        // actually generates for qualify.
        var directWrapperArch = wrapperArchitectures?.ToLowerInvariant() ?? "simulator";
        var directCSharpLoopMode =
            !hasXcframework
            && IsSystemFrameworkTarget(hasXcframework, libraryName)
            && !skipWrapperCompilation
            && (directWrapperArch == "simulator" || directWrapperArch == "device")
            && !string.IsNullOrEmpty(directModuleName);

        Func<IReadOnlySet<RecoveryUnitId>, CSharpVerificationResult>? verifyRecoverCsharp = null;
        if (CanVerifyCSharpInLoop(
                verifyRecoverCompile != null || directCSharpLoopMode,
                sdkMode, noVerifyCSharp, mixedObjcResolution, mixedBridgeRecords))
        {
            // Both modes verify the same artifact — the emitted C# for this module, built through the
            // csproj BindingProjectEmitter writes under the module's default package id. What differs is
            // only where the module identity and its metadata come from: a resolved xcframework slice, or
            // (direct mode) the ABI-peeked module name plus the .tbd's containing .framework, whose
            // Info.plist layout Extract reads exactly as it reads a packaged slice.
            var csharpModuleName = resolution?.ModuleName ?? directModuleName!;
            XCFrameworkMetadata? csharpMetadata = null;
            NativeLinkage? csharpNativeLinkage = null;
            var csharpRepoRoot = MsbuildSarifCSharpVerifier.TryFindSwiftBindingsRepoRoot();
            var csharpCsprojPath = Path.Combine(
                outputDirectory, $"{platformInfo.GetDefaultSwiftPackageId(csharpModuleName)}.csproj");

            // Verification caching — the economics layer for the loop's single most expensive stage, the
            // external dotnet build the Roslyn/MSBuild probe runs (measured ~0.9s warm / ~1.6s cold per
            // probe, versus ~0.4s for the swiftc wrapper probe and ~0.25s to render). WHEN the fingerprint
            // captures every input to the verify verdict, a hit returns the exact verdict a miss would, so
            // the loop's decisions — and therefore the settled source, the published artifacts, and the
            // report — are byte-identical whether reused or recomputed. It does NOT yet capture every
            // inherited MSBuild input (see the opt-in note below), so a stale hit can diverge from an
            // uncached run — which is exactly why the cache is opt-in. The authoritative post-loop
            // publication gate always re-runs uncached, keeping the cache strictly subordinate.
            //
            // The cache never runs in repo/dev mode (csharpRepoRoot != null): there the verify links the
            // IN-TREE Swift.Runtime, whose source is NOT a fingerprint input, so an edit there would change
            // the verdict without changing the key — our own gates must always recompute. In package/
            // consumer mode it is furthermore OPT-IN (CreateIfEnabled → non-null only when the operator sets
            // an explicit SWIFTBINDINGS_VERIFY_CACHE root), never default-on. The reason is that even in
            // package mode the fingerprint keys the emitted .cs, the verification csproj, and the
            // ABI/toolchain/generator/denylist inputs, but NOT every input MSBuild inherits into the verify
            // compile (a parent Directory.Build.props/.targets, Directory.Packages.props, nuget.config) nor
            // the resolved runtime package body (the SwiftBindings.Runtime PackageReference version range
            // floats across patch releases — its version text is in the csproj, its resolved contents are
            // not). A shared cache dir could therefore serve a stale verdict across two runs differing only
            // in one of those — never shipping a broken binding, since the authoritative post-loop
            // publication gate always re-verifies uncached, but risking an unnecessary API withdrawal that
            // diverges from an uncached run. Until the key provably covers those inputs, requiring an
            // explicit root confines the cache to an operator who owns the environment and its lifetime;
            // completing the key so it can default on is tracked in not-planned.md.
            // A run-scoped verification feed is NOT part of the fingerprint key (the key covers the emitted
            // .cs, the csproj, and the ABI/toolchain/generator/denylist inputs — not the feed's contents),
            // so a cached verdict could go stale the moment a sibling is (re)packed into the feed. Disable
            // the cache whenever a feed is in play rather than serve a verdict the key cannot vouch for.
            var verificationCache = csharpRepoRoot == null && string.IsNullOrEmpty(verificationPackageFeed)
                ? VerificationCache.CreateIfEnabled(logger)
                : null;
            // The generator's own module version id: rebuilding the generator changes it, so any generator
            // edit invalidates every cached verdict by key construction (no stale verdict after a rebuild).
            var generatorVersion = System.Reflection.Assembly
                .GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString();
            // The .NET SDK version driving the build — resolved once, lazily, only if the cache is live.
            string? toolchainId = null;
            string ResolveToolchainId()
            {
                if (toolchainId != null)
                    return toolchainId;
                var sdk = string.Empty;
                try
                {
                    var (_, stdout, _) = new SystemCommandRunner().Run("dotnet", "--version", 30000);
                    sdk = (stdout ?? string.Empty).Trim();
                }
                catch { /* an unknown SDK id just widens the key conservatively */ }
                toolchainId = sdk + "|" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
                return toolchainId;
            }

            verifyRecoverCsharp = denylist =>
            {
                // Metadata and native linkage are pure reads of the source framework — render-independent,
                // so resolve them once. The supplement reference set and the emitted .cs change with each
                // render, so re-emit the verification csproj every round from the CURRENT
                // AppleSupplementReferences state (a withdrawal only ever shrinks it, so the reference set
                // is a sound superset of the shipped one — no CS0234 false positive, only an unused ref).
                // Direct mode has no source xcframework and no source dylib to probe: the binary lives
                // on-device under /System/Library/Frameworks and dyld resolves it at runtime, so the
                // shipped csproj emits no source NativeReference and leaves the linkage at its default.
                // Mirror that here — the in-loop csproj must present the same managed-compile inputs the
                // publication gate will, or the loop would grade the render against a different project
                // than the one that ships.
                csharpMetadata ??= resolution != null
                    ? XCFrameworkMetadataExtractor.Extract(
                        resolution.DylibPath, resolution.XCFrameworkPath,
                        csharpModuleName, logger, platformInfo: platformInfo)
                    : XCFrameworkMetadataExtractor.Extract(
                        tbdPath, xcframeworkPath: "", csharpModuleName, logger, platformInfo: platformInfo);
                csharpNativeLinkage ??= resolution != null
                    ? NativeLinkageProbe.Detect(resolution.DylibPath, new SystemCommandRunner(), logger)
                    : NativeLinkage.Dynamic;

                var prototypeCsproj = EmitAppleSupplementPrototype(
                    appleSupplementPrototypeDir, platformInfo, swiftRuntimeVersion,
                    csharpMetadata.EffectiveMinimumOSVersion, logger);

                var frameworkName = BindingsGenerator.InferFrameworkName(
                    resolution?.DylibPath ?? tbdPath, csharpModuleName);
                var csharpNamespaceResolver = new NamespacePatternResolver(effectiveNamespacePattern, frameworkName);

                // Verification csproj: the managed-compile-relevant inputs match the shipped csproj
                // (dependencies, supplement reference, native linkage, metadata TFM/min-OS, namespace).
                // The native/resource concerns that do NOT affect the managed C# compile — the
                // wrapper/bridge NativeReferences (Exists()-guarded, and the wrapper isn't packaged yet
                // inside the loop) and bundle resources — are left off; the C# compiles identically.
                BindingProjectEmitter.Emit(new BindingProjectEmitterOptions
                {
                    OutputDirectory = outputDirectory,
                    ModuleName = csharpModuleName,
                    Metadata = csharpMetadata,
                    SourceXCFrameworkPath = resolution?.XCFrameworkPath,
                    SourceNativeLinkage = csharpNativeLinkage.Value,
                    WrapperXCFrameworkPath = null,
                    BridgeXCFrameworkPath = null,
                    HasBridgeSwift = false,
                    SwiftRuntimeVersion = swiftRuntimeVersion,
                    Dependencies = resolvedDependencies,
                    ResolvedNamespace = csharpNamespaceResolver.ResolveNamespace(csharpModuleName),
                    ObjCProjectFileName = null,
                    PlatformInfo = platformInfo,
                    ResourceBundleNames = null,
                    EmitsAppleSupplementReference = AppleSupplementReferences.Any,
                    AppleSupplementVersion = appleVersion,
                    AppleSupplementPrototypeProjectPath = prototypeCsproj,
                    AppleSiblingPackageReferences = ResolveSiblingAppleBindingPackages(
                        csharpModuleName, appleVersion, logger),
                }, logger);

                // With the verification csproj now on disk, the fingerprint keys the dotnet build's verdict
                // on (input ABI facts, toolchain, generator version, the emitted C# it compiles, denylist).
                // These are the components the key captures — NOT the complete MSBuild input set (a parent
                // Directory.Build.props/.targets, Directory.Packages.props, nuget.config, and the resolved
                // runtime package body are inherited but unkeyed; see the opt-in note above), which is why
                // the cache only runs when the operator opts in to a root it controls.
                // Fingerprint those and reuse a prior verdict when they match; the settled-plan component is
                // the emitted-source hash, which the compiler actually sees and which fully materializes the
                // denylist (a withdrawn member is absent from it), while the explicit denylist makes that
                // dependence direct. obj/ and bin/ intermediates are excluded — they are build outputs, not
                // the source under test.
                if (verificationCache != null)
                {
                    var abiFacts = File.Exists(swiftAbiPath)
                        ? File.ReadAllBytes(swiftAbiPath)
                        : System.Array.Empty<byte>();
                    var objSep = $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}";
                    var binSep = $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}";
                    var planFiles = Directory
                        .EnumerateFiles(outputDirectory, "*.cs", SearchOption.AllDirectories)
                        .Where(p => !p.Contains(objSep, StringComparison.Ordinal) &&
                                    !p.Contains(binSep, StringComparison.Ordinal))
                        .Append(csharpCsprojPath);
                    var settledPlan = VerificationFingerprint.HashFiles(planFiles);
                    var fingerprint = VerificationFingerprint.Compute(
                        abiFacts, ResolveToolchainId(), generatorVersion, settledPlan,
                        denylist.Select(u => u.ToString()));

                    if (verificationCache.TryGet(fingerprint, out var cachedResult))
                    {
                        logger.LogInformation(
                            "SWIFTBIND118: C# verification cache hit ({Fp}); reusing the prior verdict and " +
                            "skipping the dotnet build.", fingerprint[..12]);
                        return cachedResult;
                    }

                    var freshResult = MsbuildSarifCSharpVerifier.Verify(
                        csharpCsprojPath, new SystemCommandRunner(), csharpRepoRoot, logger: logger,
                        verificationPackageFeed: verificationPackageFeed);
                    verificationCache.Store(fingerprint, freshResult);
                    return freshResult;
                }

                return MsbuildSarifCSharpVerifier.Verify(
                    csharpCsprojPath, new SystemCommandRunner(), csharpRepoRoot, logger: logger,
                    verificationPackageFeed: verificationPackageFeed);
            };
        }

        // Advisory-only sidecar: the dependency-first module order + real import edges for an
        // orchestrator that packs multi-module siblings into a run-scoped verification feed. Never
        // fails generation — a write error is logged and ignored.
        if (!string.IsNullOrEmpty(emitInputGraphPath))
        {
            WriteInputGraphSidecar(
                emitInputGraphPath,
                primaryModuleName: resolution?.ModuleName ?? directModuleName,
                primarySwiftInterfacePath: swiftInterface,
                primaryDylibPath: dylibPath,
                primaryAbiJsonPath: swiftAbiPath,
                primaryTbdPath: tbdPath,
                primaryXcframeworkPath: xcframeworkPath,
                resolvedDependencies: resolvedDependencies,
                logger: logger);
        }

        var success = BindingsGenerator.GenerateBindings(swiftAbiPath, dylibPath, tbdPath, outputDirectory, runtimeLibraryName, asyncLibrary, swiftInterface, symbolGraph, bridgeHints, effectiveNamespacePattern, logger, loggerFactory, out var internalTypeNames, out var moduleNameForCollision, out var nestedTypesInCollidingClass, out var depModuleCollisions, dependencyModuleNames: depModuleNames, moduleDatabasePaths: moduleDatabases, resolvedDependencies: resolvedDependencies, platform: platformInfo.Platform, keepBuiltinDatabaseForTargetModule: keepBuiltinDatabase, descriptorAssemblyNameOverride: assemblyNameOverride, swiftRuntimeVersion: swiftRuntimeVersion, objcBridgeRecords: mixedBridgeRecords, compileWrapper: verifyRecoverCompile, verifyRecoverCsharp: verifyRecoverCsharp, staticMergedModuleName: staticMergedModuleName);
        if (!success)
        {
            context.ExitCode = 1;
            return;
        }

        // From here on, generation itself SUCCEEDED — which also cleared any stale
        // binding-failure-report.json from the output directory. Every nonzero exit below is a
        // post-generation gate, so each must write the structured report itself, or the artifact
        // contract — "report present ⇔ the last generation into this directory failed" — silently
        // breaks on exactly these paths.
        var failureReportModule = resolution?.ModuleName ?? directModuleName;
        var failureReportInputs = new BindingFailureInputPaths(
            swiftAbiPath, dylibPath, tbdPath, swiftInterface, platformInfo.Platform.ToString());
        void EmitCommandFailureReport(
            BindingFailureOutcomeKind kind, string reasonCode, RecoveryStage stage, string? evidence) =>
            BindingsGenerator.EmitFatalExitReport(
                failureReportModule, kind, reasonCode, stage, evidence,
                failureReportInputs, outputDirectory, logger);

        // Finding 50: fail-closed on a degraded input edge under --strict-inputs (the CI compile
        // gate). The input-resolution report (slice fallback, missing swiftinterface, ABI-JSON
        // fallback, ambiguous/synthesized TBD, degraded auto-detected dependency) was recorded
        // during XCFrameworkResolver.Resolve and dependency parsing; surfacing it as a fatal
        // error here closes the "graceful-to-a-fault" gap where a silently-substituted input
        // shrank the API surface but still exited 0. On this Swift path GenerateBindings (above)
        // has already persisted the full decision list (Info plus degradations) to the
        // inputResolution section of binding-artifact-manifest.json; this gate logs each
        // degradation as a SWIFTBIND027 line and escalates only the *degradations* to a failure.
        if (FailStrictInputsWithReport(
                strictInputs, failureReportModule, failureReportInputs, outputDirectory, logger))
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
            EmitCommandFailureReport(
                BindingFailureOutcomeKind.WrapperCompileFailure, "INVALID_WRAPPER_CONFIGURATION",
                RecoveryStage.SwiftCompile,
                $"Invalid --wrapper-architectures '{wrapperArchitectures}'. Valid values: 'simulator', 'device', 'all'.");
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
                EmitCommandFailureReport(
                    BindingFailureOutcomeKind.WrapperCompileFailure, "INVALID_WRAPPER_CONFIGURATION",
                    RecoveryStage.SwiftCompile,
                    "--wrapper-architectures all is not supported in direct mode; pass 'simulator' or 'device'.");
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
                    EmitCommandFailureReport(
                        BindingFailureOutcomeKind.WrapperCompileFailure, "INVALID_WRAPPER_CONFIGURATION",
                        RecoveryStage.SwiftCompile,
                        $"--target-architectures '{targetArchitectures}' contains an invalid architecture token.");
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
                EmitCommandFailureReport(
                    BindingFailureOutcomeKind.WrapperCompileFailure, "SWIFTBIND052",
                    RecoveryStage.SwiftCompile,
                    "An explicitly requested wrapper architecture is missing from the source slice; " +
                    "see the SWIFTBIND052 diagnostics above.");
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
                compilationResult, compilationException, contractualUnmet);
            outcome.LogTo(logger);
            if (outcome.IsFatal)
            {
                EmitCommandFailureReport(
                    BindingFailureOutcomeKind.WrapperCompileFailure,
                    outcome.DiagnosticCode ?? "WRAPPER_COMPILE_FAILURE",
                    RecoveryStage.SwiftCompile, outcome.Message);
                context.ExitCode = outcome.ExitCode;
                return;
            }

            IReadOnlyList<CoGatedMember> coGated = Array.Empty<CoGatedMember>();
            switch (ClassifyStrippedSymbols(onVerifyRecoverLoopPath: verifyRecoverCompile != null, outcome.StrippedSymbols.Count))
            {
                case StrippedSymbolDisposition.FailClosedOnLoopPath:
                    // Loop path (the verify-recover loop is active — wave-1 Swift-only included): the
                    // stripped-symbol reconciler is retired here. The loop re-renders from a pristine plan
                    // under a monotonic denylist and is the single mechanism that keeps the surface sound.
                    // It converges on a clean wrapper compile across all slices, which is NOT the same
                    // predicate as a zero-strip surface: the post-processor can drop an uncompilable
                    // @_cdecl while the rest of the wrapper still compiles clean, so a converged loop can
                    // still hand back a non-empty stripped-symbol set. Rather than claw those members back
                    // post-hoc — the retired reconciler's job — or ship a dangling P/Invoke, fail closed.
                    // A residual strip here is expected to be rare: the wrapper-symbol integrity gate
                    // inside GenerateBindings already withdraws members whose emitted wrapper symbols went
                    // missing, so this is the belt-and-braces backstop for anything it does not cover.
                    logger.LogError(
                        "SWIFTBIND115: the verify-recover loop settled the wrapper for {Module}, yet the " +
                        "generated surface still carries {Count} stripped symbol(s); failing closed rather " +
                        "than reconciling members the loop-path reconciler no longer claws back.",
                        resolution.ModuleName, outcome.StrippedSymbols.Count);
                    BindingArtifactManifestStore.ReadModifyWrite(
                        outputDirectory,
                        resolution.ModuleName,
                        m => m.Wrapper = WrapperSection.From(
                            outcome, coGated, "post-loop stripped symbols on the verify-recover path"),
                        logger);
                    EmitCommandFailureReport(
                        BindingFailureOutcomeKind.WrapperSymbolViolation, "SWIFTBIND115",
                        RecoveryStage.SymbolValidation,
                        $"The verify-recover loop settled the wrapper, yet the generated surface still " +
                        $"carries {outcome.StrippedSymbols.Count} stripped symbol(s); failing closed.");
                    context.ExitCode = 1;
                    return;

                case StrippedSymbolDisposition.Reconcile:
                    try
                    {
                        coGated = StrippedSymbolCSharpReconciler.ProcessDirectory(
                            outputDirectory, outcome.StrippedSymbols, logger);
                    }
                    catch (StrippedSymbolReconciliationException ex)
                    {
                        // Reconciliation could not make the surface sound. Shipping it would mean a
                        // binding that compiles and then throws on first use. Record the fatal phase
                        // before bailing: the manifest is the authoritative artifact record, and
                        // returning on the exit code alone would leave it either absent or stale-green
                        // for an output directory that must not be consumed.
                        logger.LogError("{Message}", ex.Message);
                        BindingArtifactManifestStore.ReadModifyWrite(
                            outputDirectory,
                            resolution.ModuleName,
                            m => m.Wrapper = WrapperSection.From(outcome, coGated, ex.Message),
                            logger);
                        EmitCommandFailureReport(
                            BindingFailureOutcomeKind.WrapperSymbolViolation,
                            "STRIPPED_SYMBOL_RECONCILIATION", RecoveryStage.SymbolValidation, ex.Message);
                        context.ExitCode = 1;
                        return;
                    }
                    if (coGated.Count > 0)
                        logger.LogInformation("Suppressed {Count} C# member(s) targeting stripped wrapper symbols.", coGated.Count);
                    break;
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
                EmitCommandFailureReport(
                    BindingFailureOutcomeKind.WrapperCompileFailure, "INVALID_WRAPPER_CONFIGURATION",
                    RecoveryStage.SwiftCompile,
                    $"Direct mode: cannot derive the framework search path from TBD '{tbdPath}'.");
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
                    EmitCommandFailureReport(
                        BindingFailureOutcomeKind.WrapperCompileFailure, "INVALID_WRAPPER_CONFIGURATION",
                        RecoveryStage.SwiftCompile,
                        $"--target-architectures '{targetArchitectures}' contains an invalid architecture token.");
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
                EmitCommandFailureReport(
                    BindingFailureOutcomeKind.WrapperCompileFailure, "SWIFTBIND052",
                    RecoveryStage.SwiftCompile,
                    "An explicitly requested wrapper architecture is missing from the source slice; " +
                    "see the SWIFTBIND052 diagnostics above.");
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

            // An unmet explicit architecture contract is fatal (From keeps it fatal). A plain
            // wrapper-compile failure is also fatal: generated C# would reference a wrapper that
            // does not exist, so publication must fail rather than leave DllNotFoundException for
            // consumers.
            var directOutcome = WrapperBuildOutcome.From(
                directResult, directException, directContractualUnmet);
            directOutcome.LogTo(logger);
            if (directOutcome.IsFatal)
            {
                EmitCommandFailureReport(
                    BindingFailureOutcomeKind.WrapperCompileFailure,
                    directOutcome.DiagnosticCode ?? "WRAPPER_COMPILE_FAILURE",
                    RecoveryStage.SwiftCompile, directOutcome.Message);
                context.ExitCode = directOutcome.ExitCode;
                return;
            }

            compilationResult = directResult;

            IReadOnlyList<CoGatedMember> directCoGated = Array.Empty<CoGatedMember>();
            if (directOutcome.StrippedSymbols.Count > 0)
            {
                try
                {
                    directCoGated = StrippedSymbolCSharpReconciler.ProcessDirectory(
                        outputDirectory, directOutcome.StrippedSymbols, logger);
                }
                catch (StrippedSymbolReconciliationException ex)
                {
                    // Same fail-closed record as the xcframework path above — the manifest must
                    // not stay green for an output directory that cannot be consumed.
                    logger.LogError("{Message}", ex.Message);
                    BindingArtifactManifestStore.ReadModifyWrite(
                        outputDirectory,
                        directModuleName,
                        m => m.Wrapper = WrapperSection.From(directOutcome, directCoGated, ex.Message),
                        logger);
                    EmitCommandFailureReport(
                        BindingFailureOutcomeKind.WrapperSymbolViolation,
                        "STRIPPED_SYMBOL_RECONCILIATION", RecoveryStage.SymbolValidation, ex.Message);
                    context.ExitCode = 1;
                    return;
                }
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

        // Gap 2 (continued): sourceNativeLinkage was classified once near the top of this method,
        // before generation, so the emitter could see it too. When it's a static `ar` archive, the
        // Swift wrapper force-loaded it (sole carrier) so the source xcframework MUST be dropped from
        // every consumer reference/pack site — re-linking the same ObjC classes would duplicate-
        // register them. That single signal feeds the mixed companion emitter (so its own
        // NativeReference follows the same policy), the binding-project/consumer-targets emitters
        // below, and the SDK's reference targets (_SwiftBindingSourceNativeLinkage).
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
            // A1: attach the ObjC surface's dropped symbols to the manifest the Swift generation
            // pass already wrote into this output dir, so they fold into binding-report.json's
            // single SkipTriage/ReviewCount gate. mixedParse.Diagnostics is the same object
            // FilterAndEmit accumulated into. Recorded even if the pipeline then aborts below, so a
            // failed mixed run still surfaces whatever it dropped before failing.
            BindingArtifactManifestStore.ReadModifyWrite(
                outputDirectory,
                resolution.ModuleName,
                m => m.ObjC = ObjCSection.From(mixedParse.Diagnostics),
                logger);
            // Fail closed: the framework HAS an ObjC surface (mixedObjcResolution != null), so a
            // non-zero pipeline exit OR a null module means we tried to bind a known ObjC
            // surface and failed. Do NOT silently degrade to a Swift-only package — that drops
            // the ObjC types with no diagnostic AND bypasses SWIFTBIND039 (which only fires when
            // metadata still says "Mixed"). Propagate a non-zero exit (mirroring the pure-ObjC
            // path's `context.ExitCode = objcResult.ExitCode` in the SwiftModuleNotFound/
            // StaticLibrary catch above) so the Nuke gate's --strict/--permissive layer decides
            // severity.
            if (ShouldAbortForFailedMixedObjC(mixedObjcResult))
            {
                logger.LogError(
                    "ObjC pipeline for mixed framework failed (exit {Code}); refusing to emit a " +
                    "Swift-only binding that would silently drop the ObjC surface. {Msg}",
                    mixedObjcResult!.ExitCode, mixedObjcResult.ErrorMessage ?? "(no detail)");
                EmitCommandFailureReport(
                    BindingFailureOutcomeKind.MixedObjCSurfaceFailure, "MIXED_OBJC_SURFACE_FAILURE",
                    RecoveryStage.Emit,
                    $"ObjC pipeline for mixed framework failed (exit {mixedObjcResult!.ExitCode}): " +
                    $"{mixedObjcResult.ErrorMessage ?? "(no detail)"}");
                // Exit 0 + null Module is still a failed ObjC surface — force non-zero so the
                // Nuke --strict/--permissive layer sees a real failure (matches the pre-Swift gate).
                context.ExitCode = mixedObjcResult.ExitCode != 0 ? mixedObjcResult.ExitCode : 1;
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

                // Mixed requires a zero-exit pipeline AND at least one bindable ObjC type — class,
                // protocol, category, or bridgeable enum — after filtering (see IsMixedFramework for
                // the deliberate "zero types → Swift-only" decision, and why the enum term must
                // track the ObjCPipeline companion-emission gate).
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
                        AppleSiblingPackageReferences = ResolveSiblingAppleBindingPackages(
                            resolution.ModuleName, appleVersion, logger),
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

                // In-generator C# verification gate. On the standalone, wrapper-compiling generation
                // path (the same path asymmetry as the Swift wrapper verify-recover loop — the SDK
                // two-pass and --compile-only paths are excluded because a downstream build is their
                // C# compile gate), build the emitted csproj and fail publication when the generated
                // C# does not compile, rather than shipping a binding whose consumer build breaks.
                // Only a C# compiler error fails; a restore/infrastructure failure is inconclusive
                // (the binding is not at fault) and a verifier-internal error never fails a healthy
                // binding — the gate answers "does the emitted C# compile", nothing more.
                if (!sdkMode && shouldCompileWrapper && !noVerifyCSharp)
                {
                    if (!VerifyGeneratedCSharp(
                            Path.Combine(outputDirectory,
                                $"{platformInfo.GetDefaultSwiftPackageId(resolution.ModuleName)}.csproj"),
                            logger, verificationPackageFeed))
                    {
                        EmitCommandFailureReport(
                            BindingFailureOutcomeKind.CSharpVerificationFailure,
                            "CSHARP_VERIFICATION_FAILURE", RecoveryStage.CSharpCompile,
                            "The generated C# failed in-generator compile verification; " +
                            "see the C# verification diagnostics above.");
                        context.ExitCode = 1;
                        return;
                    }
                }

                logger.LogInformation("Binding project emitted successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to emit binding project: {Message}", ex.Message);
                EmitCommandFailureReport(
                    BindingFailureOutcomeKind.ProjectEmissionFailure, "PROJECT_EMISSION_FAILURE",
                    RecoveryStage.Emit, $"Failed to emit binding project: {ex.Message}");
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
                    AppleSiblingPackageReferences = ResolveSiblingAppleBindingPackages(
                        directModuleName, appleVersion, logger),
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

                // Same fail-closed C# verification gate as the xcframework path above — direct mode
                // is also a standalone, wrapper-compiling path that emits a consumer-facing binding
                // csproj (via BindingProjectEmitter), so a generator bug that emits non-compiling C#
                // must fail publication here too rather than ship a binding whose consumer build
                // breaks. This branch is already !sdkMode, so only the wrapper-compile and opt-out
                // conditions remain. The csproj name mirrors BindingProjectEmitter's own naming
                // (GetDefaultSwiftPackageId(ModuleName)), not the --package-id override, so the gate
                // finds the file it just wrote instead of soft-skipping.
                if (shouldCompileWrapper && !noVerifyCSharp)
                {
                    if (!VerifyGeneratedCSharp(
                            Path.Combine(outputDirectory,
                                $"{platformInfo.GetDefaultSwiftPackageId(directModuleName)}.csproj"),
                            logger, verificationPackageFeed))
                    {
                        EmitCommandFailureReport(
                            BindingFailureOutcomeKind.CSharpVerificationFailure,
                            "CSHARP_VERIFICATION_FAILURE", RecoveryStage.CSharpCompile,
                            "The generated C# failed in-generator compile verification; " +
                            "see the C# verification diagnostics above.");
                        context.ExitCode = 1;
                        return;
                    }
                }

                logger.LogInformation("Direct-mode binding project emitted successfully.");
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to emit direct-mode binding project: {Message}", ex.Message);
                EmitCommandFailureReport(
                    BindingFailureOutcomeKind.ProjectEmissionFailure, "PROJECT_EMISSION_FAILURE",
                    RecoveryStage.Emit, $"Failed to emit direct-mode binding project: {ex.Message}");
                context.ExitCode = 1;
                return;
            }
        }
    }

    /// <summary>
    /// Resolves the sibling Apple binding packages the emitted C# actually needs, from the Swift
    /// modules whose types the generator resolved during this render.
    /// </summary>
    /// <remarks>
    /// <para>The input is <em>use</em>-derived, not import-derived. <c>AppleFrameworkImportDetector.Detect</c>
    /// answers the same question from a swiftinterface's <c>import</c> lines, which is the right
    /// source for the SDK's out-of-band <c>--detect-apple-cross-module-deps</c> mode (it has no
    /// generator run to observe). Inside a generation we have something strictly better: the set of
    /// types the emitted C# names. An import the public surface never exposes drops out, and a type
    /// reached through a re-exported umbrella — which no <c>import</c> line mentions — is still
    /// caught.</para>
    /// <para>The registry filter inside <c>ResolveDependencies</c> does the rest of the work: only
    /// modules carrying a <c>packageId</c> in <c>apple-frameworks.json</c> produce an edge, so
    /// modules resolved out of the OS-resident SDK (or out of the module being generated) contribute
    /// nothing. Failures are swallowed — a missing reference is a consumer compile error, but a
    /// crash here would fail a generation that is otherwise sound.</para>
    /// </remarks>
    private static IReadOnlyList<DetectedAppleFrameworkDependency>? ResolveSiblingAppleBindingPackages(
        string? currentModule,
        string appleVersion,
        ILogger logger)
    {
        if (!CrossModuleBindingReferences.Any)
            return null;

        try
        {
            var resolved = AppleFrameworkImportDetector.ResolveDependencies(
                CrossModuleBindingReferences.Current, currentModule ?? string.Empty, appleVersion);
            return resolved.Count > 0 ? resolved : null;
        }
        catch (System.Exception ex)
        {
            logger.LogWarning(
                "Could not resolve sibling Apple binding package references ({Message}); the emitted " +
                "csproj may be missing a PackageReference for a cross-framework type it names.",
                ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Run the C# verification gate on the emitted csproj. Returns true when publication may
    /// proceed (the C# compiled, or verification was inconclusive for a non-C# reason), false when
    /// the generated C# does not compile (the caller then fails publication with a non-zero exit).
    /// The gate answers exactly "does the emitted C# compile"; restore/infrastructure failures and
    /// verifier-internal errors are logged and pass through — a healthy binding is never failed for
    /// a cause that is not its own C#.
    /// </summary>
    private static bool VerifyGeneratedCSharp(string csprojPath, ILogger logger, string? verificationPackageFeed = null)
    {
        try
        {
            if (!System.IO.File.Exists(csprojPath))
            {
                logger.LogWarning("C# verification skipped: emitted csproj not found at {Path}", csprojPath);
                return true;
            }

            var repoRoot = MsbuildSarifCSharpVerifier.TryFindSwiftBindingsRepoRoot();
            var verification = MsbuildSarifCSharpVerifier.Verify(
                csprojPath, new SystemCommandRunner(), repoRoot, logger: logger,
                verificationPackageFeed: verificationPackageFeed);

            switch (verification.Outcome)
            {
                case CSharpVerificationOutcome.CompileErrors:
                    var errors = verification.CompilerErrors;
                    logger.LogError(
                        "SWIFTBIND113: the generated C# does not compile ({Count} error(s)); failing " +
                        "publication rather than shipping a binding whose consumer build breaks.",
                        errors.Count);
                    foreach (var e in errors.Take(20))
                        logger.LogError("  {Id} {File}({Line},{Col}): {Message}",
                            e.Id, e.FilePath, e.Line, e.Column, e.Message);
                    if (errors.Count > 20)
                        logger.LogError("  … and {More} more C# error(s).", errors.Count - 20);
                    return false;

                case CSharpVerificationOutcome.Inconclusive:
                    logger.LogWarning(
                        "C# verification inconclusive ({Reason}); the generated C# was not proven to " +
                        "compile in-generator. Not failing publication on a non-C# cause.",
                        verification.InconclusiveReason);
                    return true;

                default:
                    logger.LogInformation("C# verification passed: the generated C# compiles.");
                    return true;
            }
        }
        catch (System.Exception ex)
        {
            logger.LogWarning(
                "C# verification could not run ({Message}); not failing publication on a verifier-internal error.",
                ex.Message);
            return true;
        }
    }

    /// <summary>
    /// Writes the advisory input-graph sidecar: the supplied modules in dependency-first order and their
    /// real (import-derived) inter-module dependencies, as JSON. An orchestrator that runs the generator
    /// once per module unions these across a corpus to order a run-scoped verification feed — pack each
    /// binding feed-first, then verify the dependent against a populated feed. Advisory only: any failure
    /// is logged and swallowed so it can never fail a generation.
    /// </summary>
    private static void WriteInputGraphSidecar(
        string sidecarPath,
        string? primaryModuleName,
        string? primarySwiftInterfacePath,
        string? primaryDylibPath,
        string? primaryAbiJsonPath,
        string? primaryTbdPath,
        string? primaryXcframeworkPath,
        System.Collections.Generic.IReadOnlyList<FrameworkDependencyInfo>? resolvedDependencies,
        ILogger logger)
    {
        try
        {
            if (string.IsNullOrEmpty(primaryModuleName))
            {
                logger.LogWarning(
                    "Input-graph sidecar skipped: the primary module name could not be resolved.");
                return;
            }

            var inventory = InputInventory.FromCliInvocation(
                primaryModuleName: primaryModuleName,
                primarySwiftInterfacePath: primarySwiftInterfacePath,
                primaryDylibPath: primaryDylibPath,
                primaryAbiJsonPath: primaryAbiJsonPath,
                primaryTbdPath: primaryTbdPath,
                primaryXcframeworkPath: primaryXcframeworkPath,
                resolvedDependencies: resolvedDependencies);

            // An SDK/runtime module is never built by this run, so it never gates a sibling's order —
            // classify every unsupplied module as unresolved (isSdkModuleResolved: _ => false), keeping
            // it out of the supplied set the order and edges are computed over.
            var graph = BindingInputClosurePreflight.BuildGraph(inventory, isSdkModuleResolved: _ => false);

            var document = new InputGraphSidecarDocument
            {
                PrimaryModule = primaryModuleName,
                TopologicalOrder = graph.TopologicalOrder(logger).ToList(),
                ImportDependencies = graph.SuppliedImportDependencies()
                    .ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal),
            };

            var json = System.Text.Json.JsonSerializer.Serialize(
                document, InputGraphSidecarJsonContext.Default.InputGraphSidecarDocument);

            var dir = Path.GetDirectoryName(Path.GetFullPath(sidecarPath));
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(sidecarPath, json);
            logger.LogInformation("Wrote input-graph sidecar: {Path}", sidecarPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Input-graph sidecar could not be written ({Message}); continuing — it is advisory only.",
                ex.Message);
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
    /// when the framework HAS a detected ObjC surface, so a non-zero pipeline exit OR a null
    /// module means we tried to bind a known ObjC surface and failed. When this returns true,
    /// <c>Execute</c> propagates a non-zero exit code and refuses to emit a binding at all —
    /// silently degrading to a Swift-only package would drop the ObjC types with no diagnostic
    /// AND bypass <c>SWIFTBIND039</c> (which only fires when the emitted metadata still says
    /// "Mixed"). A null result means the pipeline never ran (not a mixed framework) → never abort.
    /// </summary>
    /// <remarks>
    /// Single oracle shared by the pre-Swift parse gate (<see cref="ObjCParseResult"/>) and the
    /// post-Swift FilterAndEmit gate (<see cref="ObjCPipelineResult"/>). Both result types carry
    /// the same exit-code + module shape; the overloads unwrap and delegate to the shared core.
    /// </remarks>
    internal static bool ShouldAbortForFailedMixedObjC(int exitCode, ObjCModule? module)
        => exitCode != 0 || module == null;

    /// <inheritdoc cref="ShouldAbortForFailedMixedObjC(int, ObjCModule?)"/>
    internal static bool ShouldAbortForFailedMixedObjC(ObjCPipelineResult? mixedObjcResult)
        => mixedObjcResult is not null
           && ShouldAbortForFailedMixedObjC(mixedObjcResult.ExitCode, mixedObjcResult.Module);

    /// <inheritdoc cref="ShouldAbortForFailedMixedObjC(int, ObjCModule?)"/>
    internal static bool ShouldAbortForFailedMixedObjC(ObjCParseResult? mixedParse)
        => mixedParse is not null
           && ShouldAbortForFailedMixedObjC(mixedParse.ExitCode, mixedParse.Module);

    /// <summary>
    /// Decides what to do with the post-loop wrapper compile's stripped-symbol set. This is the
    /// retirement seam for <see cref="StrippedSymbolCSharpReconciler"/>: on the verify-recover loop
    /// path the loop has already settled the surface through its own render/re-render machinery, so
    /// the reconciler is never invoked. The loop's convergence predicate is a clean wrapper compile,
    /// not a zero-strip surface, so a non-empty stripped-symbol set can still reach here; on the loop
    /// path we no longer claw those members back, so any residual strip must fail closed rather than
    /// ship a dangling P/Invoke. Off the loop path (SDK two-pass, <c>--compile-wrapper-only</c>) the
    /// reconciler still owns the claw-back. Extracted as a pure function so the "reconciler not
    /// invoked on the loop path" invariant is unit-testable.
    /// </summary>
    internal static StrippedSymbolDisposition ClassifyStrippedSymbols(
        bool onVerifyRecoverLoopPath, int strippedSymbolCount)
        => strippedSymbolCount <= 0
            ? StrippedSymbolDisposition.None
            : onVerifyRecoverLoopPath
                ? StrippedSymbolDisposition.FailClosedOnLoopPath
                : StrippedSymbolDisposition.Reconcile;

    /// <summary>
    /// A1: persist a pure-ObjC binding's dropped-symbol diagnostics into the output directory's
    /// binding manifest (and the rederived <c>binding-report.json</c>). A pure-ObjC binding runs no
    /// Swift generation pass, so no manifest exists yet and this is the sole writer — it builds a
    /// Partial manifest carrying only the ObjC section, which <see cref="BindingReportProjection"/>
    /// folds into the <c>SkipTriage</c>/<c>ReviewCount</c> gate. No-op when the run produced no
    /// diagnostics object (an early resolve failure before any emission).
    /// </summary>
    private static void PersistPureObjCSkipReport(
        string outputDirectory, string moduleName, ObjCBindingDiagnostics? diagnostics, ILogger logger)
    {
        if (diagnostics == null)
            return;

        var manifest = new BindingArtifactManifest
        {
            Module = moduleName,
            GeneratorVersion = BindingArtifactManifestStore.GetGeneratorVersion(),
            Status = ManifestStatus.Partial,
            PartialReason =
                "Pure-ObjC binding: no Swift generation phase runs, so the manifest carries only the ObjC skip section.",
            ObjC = ObjCSection.From(diagnostics),
        };
        BindingArtifactManifestStore.Write(manifest, outputDirectory, logger);
    }

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
    /// The strict-inputs gate plus its failure-report emission. This abort exits nonzero AFTER a
    /// successful generation already cleared any stale <c>binding-failure-report.json</c>, so the
    /// gate must write the structured report itself — the report's evidence carries each recorded
    /// degradation, mirroring the SWIFTBIND027 lines the gate logs. Returns true when the
    /// generation must abort.
    /// </summary>
    internal static bool FailStrictInputsWithReport(
        bool strictInputs,
        string? moduleName,
        BindingFailureInputPaths inputs,
        string outputDirectory,
        ILogger logger)
    {
        if (!EmitStrictInputsFailureIfDegraded(strictInputs, logger))
            return false;

        var degradations = InputResolutionReport.Decisions
            .Where(d => d.Severity == InputResolutionSeverity.Degradation)
            .Select(d => $"{d.Category}: {d.Detail}");
        BindingsGenerator.EmitFatalExitReport(
            moduleName, BindingFailureOutcomeKind.StrictInputsDegraded, "SWIFTBIND027",
            RecoveryStage.Parse, string.Join(" | ", degradations), inputs, outputDirectory, logger);
        return true;
    }

    /// <summary>
    /// Failure-report inputs for a pure-ObjC lane. The lane consumes no ABI JSON, TBD, or
    /// swiftinterface; its one native input is the resolved framework binary, carried in the
    /// dylib slot (it IS the Mach-O dynamic library being bound) so the input fingerprint stays
    /// framework-specific rather than platform-only.
    /// </summary>
    internal static BindingFailureInputPaths PureObjCFailureInputs(
        XCFrameworkResolver.ObjCFrameworkResolution resolution, PlatformInfo platformInfo) => new(
            null,
            Path.Combine(
                resolution.FrameworkSearchPath,
                $"{resolution.FrameworkDirectoryName}.framework",
                resolution.FrameworkDirectoryName),
            null, null, platformInfo.Platform.ToString());

    /// <summary>
    /// Writes the structured failure report for a pure-ObjC lane whose pipeline exited nonzero.
    /// The lane propagates the pipeline's exit code directly and the ObjC module identity is
    /// already resolved at that point, so the exit must leave the artifact; a zero exit writes
    /// nothing. Stage attribution follows the pipeline's shape: no parsed module means the
    /// clang/AST/umbrella-header phase died (Parse); a parsed module that still failed died in
    /// filtering or companion emission (Emit).
    /// </summary>
    internal static void ReportPureObjCPipelineFailure(
        ObjCPipelineResult result,
        string moduleName,
        BindingFailureInputPaths inputs,
        string outputDirectory,
        ILogger logger)
    {
        if (result.ExitCode == 0)
            return;

        BindingsGenerator.EmitFatalExitReport(
            moduleName, BindingFailureOutcomeKind.ObjCPipelineFailure, "OBJC_PIPELINE_FAILURE",
            result.Module == null ? RecoveryStage.Parse : RecoveryStage.Emit,
            result.ErrorMessage, inputs, outputDirectory, logger);
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
    /// pipeline both <b>succeeded</b> (exit 0) AND produced at least one bindable ObjC type — a
    /// class, protocol, category, <b>or a bridgeable enum</b> — <i>after</i> mixed-framework
    /// filtering. A zero-exit run that filtered down to zero bindable types is deliberately treated
    /// as a plain Swift framework — there is no managed ObjC surface to embed, so emitting a
    /// companion (and its <c>SWIFTBIND039</c> contract) would be spurious.
    ///
    /// <para>The enum term is load-bearing and MUST stay in lockstep with the companion-emission
    /// gate in <c>ObjCPipeline.FilterAndEmit</c> (which skips emission only when
    /// classes/protocols/categories/enums are ALL zero). A bridged <c>NS_ENUM</c>/<c>NS_OPTIONS</c>
    /// with no accompanying ObjC class still forces a companion — a synthesized bridge record
    /// resolves a Swift member (e.g. <c>validate(options:)</c>) to that companion's <c>[Flags]</c>
    /// enum. If this predicate drops the enum term while the pipeline still emits the companion, the
    /// framework is mis-classified "Swift", the SDK never builds/references the companion
    /// (<c>_BuildMixedObjCCompanion</c>/<c>_ReferenceMixedObjCCompanion</c> are both gated on
    /// <c>FrameworkType == 'Mixed'</c>), and the Swift binding's reference to the enum fails with
    /// CS0234 — the exact failure mode of an enum-only companion (no class-bearing PackGate fixture
    /// exercises that shape).</para>
    ///
    /// This is the same predicate that decides <c>frameworkType</c> and whether an
    /// <c>objcProjectName</c> is recorded, so the companion-embed machinery and the metadata agree
    /// by construction. Callers that detected an ObjC surface but reach here with a non-zero exit
    /// are handled earlier by <see cref="ShouldAbortForFailedMixedObjC"/>.
    /// </summary>
    internal static bool IsMixedFramework(ObjCPipelineResult? mixedObjcResult)
        => mixedObjcResult?.ExitCode == 0
           && (mixedObjcResult.Module?.Classes.Count > 0
               || mixedObjcResult.Module?.Protocols.Count > 0
               || mixedObjcResult.Module?.Categories.Count > 0
               || mixedObjcResult.Module?.Enums.Count > 0);

    /// <summary>
    /// Whether the in-loop C# verify-recover leg can soundly verify this render's emitted C#. The
    /// leg is sound only when the emitted C# does not reference an ObjC companion assembly: that
    /// companion is built AFTER <c>GenerateBindings</c> returns, and the in-loop verification csproj
    /// deliberately does not reference it (<c>ObjCProjectFileName = null</c>), so a C# member that
    /// USES a bridged ObjC type would fail to resolve in-loop and be withdrawn on a false error.
    /// The emitted C# references a companion type iff at least one bridged ObjC record was threaded
    /// into generation. So two shapes both emit companion-free C# and ARE in-loop verifiable: a
    /// framework with no ObjC surface at all (<paramref name="mixedObjcResolution"/> null), and a
    /// "potential mixed" framework whose ObjC bridge filtered to zero records — an umbrella header
    /// that only re-exports the Swift module (<paramref name="mixedBridgeRecords"/> empty). A
    /// framework that actually bridges ≥1 ObjC record keeps the post-loop publication gate
    /// (fail-closed) unchanged.
    ///
    /// <para>The other precondition is that this generation mode will actually produce a verifiable
    /// binding project — <paramref name="verifiableProjectMode"/>. It is deliberately NOT "the Swift
    /// wrapper loop is active": the C# leg verifies the emitted C# through a binding csproj, which the
    /// Apple system-framework direct path also emits and also grades with a fail-closed publication gate,
    /// even though it has no in-generation wrapper compile for a Swift loop to run on. Keying this gate
    /// on the wrapper delegate is what left that path with no withdrawal/re-emit net at all. What the
    /// flag must mean at every call site is "the run reaches a branch that emits the consumer-facing
    /// csproj and verifies it" — a mode that emits no csproj has nothing to build and no publication gate
    /// for the loop to reduce work for. Non-SDK mode and C# verification not opted out still apply: SDK
    /// mode defers both wrapper and project emission to its own passes.</para>
    /// </summary>
    internal static bool CanVerifyCSharpInLoop(
        bool verifiableProjectMode,
        bool sdkMode,
        bool noVerifyCSharp,
        XCFrameworkResolver.ObjCFrameworkResolution? mixedObjcResolution,
        IReadOnlyList<TypeRecord>? mixedBridgeRecords)
        => verifiableProjectMode && !sdkMode && !noVerifyCSharp
           && (mixedObjcResolution == null
               || mixedBridgeRecords == null
               || mixedBridgeRecords.Count == 0);

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
        Console.WriteLine($"  --apple-version          Apple SDK train / SwiftBindings.Apple supplement version (e.g. 26.2.4). Default: {CliOptions.DefaultAppleSupplementVersion}.");
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

/// <summary>
/// What to do with a post-loop wrapper compile's stripped-symbol set — the outcome of
/// <see cref="BindingsGeneratorCommand.ClassifyStrippedSymbols"/>.
/// </summary>
internal enum StrippedSymbolDisposition
{
    /// <summary>No symbols were stripped; nothing to do.</summary>
    None,

    /// <summary>
    /// Off the verify-recover loop path (SDK two-pass, <c>--compile-wrapper-only</c>): claw the
    /// dangling C# members back through <see cref="StrippedSymbolCSharpReconciler"/>.
    /// </summary>
    Reconcile,

    /// <summary>
    /// On the loop path the reconciler is retired, so a non-empty stripped-symbol set is a soundness
    /// surprise the loop did not converge; fail closed rather than reconcile or ship dangling
    /// P/Invokes.
    /// </summary>
    FailClosedOnLoopPath,
}

/// <summary>
/// On-disk shape of the advisory input-graph sidecar (<c>--emit-input-graph</c>): the supplied modules
/// in dependency-first order and their real (import-derived) inter-module dependencies. An orchestrator
/// unions these across a corpus to topologically order a run-scoped verification feed.
/// </summary>
public sealed class InputGraphSidecarDocument
{
    /// <summary>The primary module this generation targeted.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("primaryModule")]
    public string PrimaryModule { get; set; } = "";

    /// <summary>The supplied modules (primary + supplied dependencies), dependency-first.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("topologicalOrder")]
    public List<string> TopologicalOrder { get; set; } = new();

    /// <summary>For each supplied module, the supplied modules it imports (real, pruned edges).</summary>
    [System.Text.Json.Serialization.JsonPropertyName("importDependencies")]
    public Dictionary<string, List<string>> ImportDependencies { get; set; } = new();
}

/// <summary>Source-generated, AOT/trim-safe serializer context for the input-graph sidecar.</summary>
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(InputGraphSidecarDocument))]
internal partial class InputGraphSidecarJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
