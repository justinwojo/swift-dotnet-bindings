// Copyright (c) Microsoft Corporation.
// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CommandLine;

namespace BindingsGeneration;

/// <summary>
/// Defines all CLI options for the bindings generator tool.
/// Extracted from Program.cs to separate option definitions from handler logic.
/// </summary>
public class CliOptions
{
    public Option<string> SwiftAbi { get; } = new(aliases: new[] { "-a", "--swiftabi" }, "Path to the Swift ABI file.");
    public Option<string> Dylib { get; } = new(aliases: new[] { "-d", "--dylib" }, "Path to the dynamic library.");
    public Option<string> Tbd { get; } = new(aliases: new[] { "-t", "--tbd" }, "Path to the TBD file.");
    public Option<string> OutputDirectory { get; } = new(aliases: new[] { "-o", "--output" }, "Output directory for generated bindings.");

    public Option<string> XCFramework { get; } = new(
        aliases: new[] { "--xcframework" },
        description: "Path to an xcframework directory. Automatically resolves ABI JSON, dylib, TBD, and swiftinterface. " +
                     "Mutually exclusive with -a, -d, -t.");

    public Option<string> Platform { get; } = new(
        aliases: new[] { "--platform" },
        description: "Apple platform target: 'ios' (default), 'macos', 'tvos', 'maccatalyst'.",
        getDefaultValue: () => "ios");

    public Option<string?> PlatformVersion { get; } = new(
        aliases: new[] { "--platform-version" },
        description: "Apple-workload platform version (e.g. '26.0', '26.2') baked into the " +
                     "generator-emitted csproj's <TargetFramework> and buildTransitive/ pack " +
                     "path. .NET 10 library projects default to the OLDEST installed TPV " +
                     "(unlike apps, which float to the newest), so a versionless " +
                     "<TargetFramework>net10.0-ios</TargetFramework> would silently desync " +
                     "from the buildTransitive/ pack path on multi-workload machines. Pass " +
                     "the explicit form (e.g. '--platform-version 26.2' for net10.0-ios26.2) " +
                     "when packing for nuget.org. Default keeps the in-tree fallback so " +
                     "existing local-dev callers don't break.");

    public Option<string> PlatformTarget { get; } = new(
        aliases: new[] { "--platform-target" },
        description: "Platform target for xcframework slice selection: 'simulator' (default) or 'device'. " +
                     "Only used with --xcframework.",
        getDefaultValue: () => "simulator");

    public Option<string> LibraryName { get; } = new(
        aliases: new[] { "-l", "--library-name" },
        description: "Runtime library name for DllImport. If not specified, uses the dylib path. " +
                     "Note: If the name starts with '@' (e.g., @rpath/...), escape it with backslash: '\\@rpath/Nuke.framework/Nuke'");

    public Option<string> AsyncLibrary { get; } = new(
        aliases: new[] { "--async-library" },
        description: "Library name for async wrapper functions. If not specified, uses the module library. " +
                     "Only needed in manual mode (-a/-d/-t) when the wrapper is compiled as a separate dylib.");

    public Option<string> NamespacePattern { get; } = new(
        aliases: new[] { "--namespace-pattern" },
        description: "C# namespace pattern for generated modules and types. Supports {Module} and {Framework}. Default: {Module}");

    public Option<string> SwiftInterface { get; } = new(
        aliases: new[] { "-s", "--swiftinterface" },
        description: "Path to the .swiftinterface file. Used to detect @inlinable internal members " +
                     "that can't be distinguished from public in the ABI JSON alone.");

    public Option<string> SymbolGraph { get; } = new(
        aliases: new[] { "--symbolgraph" },
        description: "Path to symbol graph JSON file or directory. Used to extract Swift doc comments for C# XML doc comment generation.");

    public Option<bool> NoDocs { get; } = new(
        aliases: new[] { "--no-docs" },
        description: "Disable automatic symbol graph extraction for doc comment generation. " +
                     "Does not affect explicit --symbolgraph paths.",
        getDefaultValue: () => false);

    public Option<string> BridgeHints { get; } = new(
        aliases: new[] { "--bridge-hints" },
        description: "Path to bridge hints JSON file for customizing SwiftUI bridge generation.");

    public Option<string> Config { get; } = new(
        aliases: new[] { "--config" },
        description: "Path to config JSON file. Default: .swiftbindings.json in current directory.");

    public Option<bool> SdkMode { get; } = new(
        aliases: new[] { "--sdk-mode" },
        description: "SDK mode: skips .csproj emission (used when the SDK IS the project system).",
        getDefaultValue: () => false);

    public Option<string?> PackageId { get; } = new(
        aliases: new[] { "--package-id" },
        description: "Package ID for NuGet packaging. Overrides the default '{Module}.Swift.iOS'.");

    public Option<string?> SwiftRuntimeVersion { get; } = new(
        aliases: new[] { "--swift-runtime-version" },
        description: "Version of the SwiftBindings.Runtime NuGet package to reference from the emitted .csproj. " +
                     "Default '0.0.0-dev' is a local-dev sentinel: it binds against the in-tree Swift.Runtime " +
                     "via SwiftBindingsRepoRoot and marks the project IsPackable=false. Pass a published " +
                     "version (e.g. '0.8.0') to emit a normal PackageReference and enable 'dotnet pack'.");

    public Option<string> WrapperArchitectures { get; } = new(
        aliases: new[] { "--wrapper-architectures" },
        description: "Wrapper compilation scope: 'simulator' (default), 'device', or 'all' (both slices).",
        getDefaultValue: () => "simulator");

    public Option<string[]> FrameworkDependency { get; } = new(
        aliases: new[] { "--framework-dependency" },
        description: "Path to a dependency xcframework. Repeatable. Adds -F search paths for wrapper compilation " +
                     "and PackageReference entries in the emitted .csproj. Requires --xcframework.")
    { AllowMultipleArgumentsPerToken = false };

    public Option<string[]> ModuleDatabase { get; } = new(
        aliases: new[] { "--module-database" },
        description: "Path to a dependency module database XML file. Repeatable. " +
                     "Loads type records from previously generated modules for cross-module resolution.")
    { AllowMultipleArgumentsPerToken = false };

    public Option<bool> NoAutoDetect { get; } = new(
        aliases: new[] { "--no-auto-detect" },
        description: "Disable automatic dependency detection from binary linkage.",
        getDefaultValue: () => false);

    public Option<bool> KeepBuiltinDatabase { get; } = new(
        aliases: new[] { "--keep-builtin-database" },
        description: "Disable Apple-framework target mode auto-detection. By default, when the input " +
                     "abi.json's module name matches a built-in dependency database (e.g., generating " +
                     "real bindings for StoreKit), the colliding stub is skipped so the parse-and-emit " +
                     "gate fires. Pass this flag to keep the legacy stub and let the gate skip the input.",
        getDefaultValue: () => false);

    public Option<bool> ObjC { get; } = new(
        aliases: new[] { "--objc" },
        description: "Force ObjC binding pipeline (auto-detected if not specified).",
        getDefaultValue: () => false);

    public Option<bool> SkipWrapperCompilation { get; } = new(
        aliases: new[] { "--skip-wrapper-compilation" },
        description: "Skip Swift wrapper compilation. Generates C# bindings and Swift wrapper source but does not compile the wrapper. " +
                     "Used by the SDK to defer wrapper compilation until after dependencies are built.",
        getDefaultValue: () => false);

    public Option<bool> SkipThunkCompilation { get; } = new(
        aliases: new[] { "--skip-thunk-compilation" },
        description: "Skip native thunk assembly compilation. Generated .arm64.s files will not be compiled or linked. " +
                     "Thunk symbols will be missing from the wrapper binary.",
        getDefaultValue: () => false);

    public Option<bool> CompileWrapperOnly { get; } = new(
        aliases: new[] { "--compile-wrapper-only" },
        description: "Compile-wrapper-only mode: skips all parsing and C# generation, compiles existing .swift wrapper files " +
                     "from the output directory, and updates binding-metadata.props. Requires --xcframework and -o.",
        getDefaultValue: () => false);

    public Option<bool> CompileBridgeOnly { get; } = new(
        aliases: new[] { "--compile-bridge-only" },
        description: "Compile-bridge-only mode: skips all parsing and C# generation, compiles existing .SwiftUIBridge.swift files " +
                     "from the output directory into a {Module}Bridge.xcframework, and updates binding-metadata.props. Requires --xcframework and -o.",
        getDefaultValue: () => false);

    public Option<bool> EmitAppleTypesManifest { get; } = new(
        aliases: new[] { "--emit-apple-types-manifest" },
        description: "Emit-apple-types-manifest mode: ingest one or more Apple Xcode SDK ABI JSON dumps and write the " +
                     "SwiftBindings.Apple metadata manifest to -o. Requires --apple-abi-json and --apple-include-types. " +
                     "See src/Swift.Bindings.Sdk/tools/apple-types-manifest/README.md.",
        getDefaultValue: () => false);

    public Option<string[]> AppleAbiJson { get; } = new(
        aliases: new[] { "--apple-abi-json" },
        description: "Path to an Apple Xcode SDK ABI JSON dump. Repeatable; one dump per (module, platform). Used only by --emit-apple-types-manifest.")
    { AllowMultipleArgumentsPerToken = false };

    public Option<string?> AppleIncludeTypes { get; } = new(
        aliases: new[] { "--apple-include-types" },
        description: "Path to an include-types.json file listing Swift identities (and optional typealiases) to emit into the Apple types manifest. " +
                     "Positive-list only so the supplement never shadows Runtime-owned canonical types.");

    public Option<string> AppleVersion { get; } = new(
        aliases: new[] { "--apple-version" },
        description: "Apple SDK train / SwiftBindings.Apple supplement version (e.g. 26.0.0). " +
                     "Drives the generated PackageReference floor, binding-metadata.props, and — when " +
                     "--apple-sdk-train-major is not set explicitly — the manifest sdk_train.major " +
                     "(parsed from the leading numeric component). Package major tracks Apple SDK train.",
        getDefaultValue: () => "26.0.0");

    // Optional override; falls back to the major component of --apple-version when null.
    public Option<int?> AppleSdkTrainMajor { get; } = new(
        aliases: new[] { "--apple-sdk-train-major" },
        description: "Apple SDK train major (e.g. 26). When omitted, derived from --apple-version.",
        getDefaultValue: () => (int?)null);

    public Option<string?> AppleSdkTrainLabel { get; } = new(
        aliases: new[] { "--apple-sdk-train-label" },
        description: "Free-form label for sdk_train.label (e.g. 'Xcode 16 / iOS 18 / macOS 15 / tvOS 18').");

    public Option<string?> AppleSdkMinIos { get; } = new(aliases: new[] { "--apple-sdk-min-ios" }, description: "sdk_train.platforms.ios value.");
    public Option<string?> AppleSdkMinMaccatalyst { get; } = new(aliases: new[] { "--apple-sdk-min-maccatalyst" }, description: "sdk_train.platforms.maccatalyst value.");
    public Option<string?> AppleSdkMinTvos { get; } = new(aliases: new[] { "--apple-sdk-min-tvos" }, description: "sdk_train.platforms.tvos value.");
    public Option<string?> AppleSdkMinMacos { get; } = new(aliases: new[] { "--apple-sdk-min-macos" }, description: "sdk_train.platforms.macos value.");

    public Option<bool> EmitAppleTypesCs { get; } = new(
        aliases: new[] { "--emit-apple-types-cs" },
        description: "Emit-apple-types-cs mode: read the Apple types manifest and write C# source for " +
                     "SwiftBindings.Apple into -o. Requires --apple-types-manifest; optional " +
                     "--apple-types-sequential-layout-whitelist gates sequential-layout emission.",
        getDefaultValue: () => false);

    public Option<string?> AppleTypesManifest { get; } = new(
        aliases: new[] { "--apple-types-manifest" },
        description: "Path to the Apple types manifest.json (produced by --emit-apple-types-manifest).");

    public Option<string?> AppleTypesSequentialLayoutWhitelist { get; } = new(
        aliases: new[] { "--apple-types-sequential-layout-whitelist" },
        description: "Optional path to sequential-layout-whitelist.json. Absent or empty means every " +
                     "entry emits via the default VWT-backed opaque storage path.");

    public Option<bool> AllowPartialAppleTypesManifest { get; } = new(
        aliases: new[] { "--allow-partial-apple-types-manifest" },
        description: "Dev-only opt-in for --emit-apple-types-manifest: suppress the hard failure " +
                     "when include-types.json identities are unmatched in the ABI dumps. " +
                     "Regenerate.sh never passes this by default — a ship manifest MUST cover every " +
                     "requested identity, otherwise a typo or a dropped type goes undetected until a " +
                     "consumer crashes at runtime.",
        getDefaultValue: () => false);

    public Option<bool> ValidateAppleTypesManifest { get; } = new(
        aliases: new[] { "--validate-apple-types-manifest" },
        description: "Validate-apple-types-manifest mode: probe the live Apple SDK for every manifest " +
                     "entry advertised on the host platform — load the framework dylib, dlsym the " +
                     "metadata accessor, invoke it, and read VWT size/alignment/stride. Detects drift " +
                     "vs. the manifest. Combine with --apple-types-manifest-write-back to populate " +
                     "size/align/stride in place. Requires --apple-types-manifest <path>.",
        getDefaultValue: () => false);

    public Option<bool> AppleTypesManifestWriteBack { get; } = new(
        aliases: new[] { "--apple-types-manifest-write-back" },
        description: "Used with --validate-apple-types-manifest. When set, the validator writes " +
                     "probed VWT size/alignment/stride back into the manifest at --apple-types-manifest, " +
                     "preserving the existing two-space-indent JSON format.",
        getDefaultValue: () => false);

    public Option<string?> AppleSupplementPrototypeDir { get; } = new(
        aliases: new[] { "--apple-supplement-prototype-dir" },
        description: "When set, the generator emits a trimmed SwiftBindings.Apple.Prototype.csproj into this " +
                     "directory (plus the .cs sources for the Apple-supplement types referenced by this run) " +
                     "and wires the generated consumer project to it via ProjectReference instead of a " +
                     "PackageReference to SwiftBindings.Apple. Lets developers iterate on supplement changes " +
                     "without waiting for a new NuGet publish. No-op when the generator didn't resolve any " +
                     "Apple-supplement types.");

    public Option<int> Verbose { get; } = new(
        aliases: new[] { "-v", "--verbose" },
        description: "Verbosity level. 0 = No logging, 1 = General information, 2 = Debugging information. (default: 1)",
        getDefaultValue: () => 1);

    public Option<bool> Help { get; } = new(aliases: new[] { "-h", "--help" }, "Display a help message.");

    /// <summary>
    /// Creates a RootCommand configured with all CLI options.
    /// </summary>
    public RootCommand CreateRootCommand()
    {
        return new RootCommand(description: "Swift bindings generator.")
        {
            SwiftAbi,
            Dylib,
            Tbd,
            OutputDirectory,
            XCFramework,
            Platform,
            PlatformVersion,
            PlatformTarget,
            LibraryName,
            AsyncLibrary,
            SwiftInterface,
            SymbolGraph,
            NoDocs,
            BridgeHints,
            NamespacePattern,
            SdkMode,
            PackageId,
            SwiftRuntimeVersion,
            WrapperArchitectures,
            FrameworkDependency,
            ModuleDatabase,
            NoAutoDetect,
            KeepBuiltinDatabase,
            ObjC,
            SkipWrapperCompilation,
            SkipThunkCompilation,
            CompileWrapperOnly,
            CompileBridgeOnly,
            EmitAppleTypesManifest,
            AppleAbiJson,
            AppleIncludeTypes,
            AppleVersion,
            AppleSdkTrainMajor,
            AppleSdkTrainLabel,
            AppleSdkMinIos,
            AppleSdkMinMaccatalyst,
            AppleSdkMinTvos,
            AppleSdkMinMacos,
            EmitAppleTypesCs,
            AppleTypesManifest,
            AppleTypesSequentialLayoutWhitelist,
            AllowPartialAppleTypesManifest,
            ValidateAppleTypesManifest,
            AppleTypesManifestWriteBack,
            AppleSupplementPrototypeDir,
            Config,
            Verbose,
            Help,
        };
    }
}
