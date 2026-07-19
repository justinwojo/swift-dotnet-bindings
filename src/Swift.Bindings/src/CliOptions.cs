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
                     "Note: If the name starts with '@' (e.g., @rpath/...), escape it with backslash: '\\@rpath/MyFramework.framework/MyFramework'");

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

    public Option<string?> AssemblyName { get; } = new(
        aliases: new[] { "--assembly-name" },
        description: "Name of the .NET assembly the generated C# compiles into. Used as the " +
                     "<assembly fullname> of the emitted ILLink trimmer descriptor so ILC roots " +
                     "the open generics in the right assembly. In SDK mode the generated code is " +
                     "compiled into the consuming project's own assembly ($(AssemblyName)), which " +
                     "may differ from the module-derived default, so the SDK passes this explicitly. " +
                     "When omitted (CLI/pack mode) the descriptor falls back to the emitted csproj's " +
                     "name ('{Module}.Swift.{platform}'), which equals that assembly's name.");

    public Option<string?> SwiftRuntimeVersion { get; } = new(
        aliases: new[] { "--swift-runtime-version" },
        description: "Version of the SwiftBindings.Runtime NuGet package to reference from the emitted .csproj. " +
                     "Default '0.0.0-dev' is a local-dev sentinel: it binds against the in-tree Swift.Runtime " +
                     "via SwiftBindingsRepoRoot and marks the project IsPackable=false. Pass a published " +
                     "version (e.g. '0.8.0') to emit a normal PackageReference and enable 'dotnet pack'.");

    public Option<string> WrapperArchitectures { get; } = new(
        aliases: new[] { "--wrapper-architectures" },
        description: "Wrapper compilation scope: 'simulator' (default), 'device', or 'all' (both slices). " +
                     "This is the slice TYPE (simulator vs device), NOT the CPU architecture — see --target-architectures.",
        getDefaultValue: () => "simulator");

    public Option<string?> TargetArchitectures { get; } = new(
        aliases: new[] { "--target-architectures" },
        description: "CPU architectures to compile the wrapper for. 'auto' matches the source slice's arch " +
                     "coverage — a fat (arm64+x86_64) wrapper iff the source is fat, arm64-only otherwise — " +
                     "and never fails on an arm64-only source. A comma-separated list (e.g. 'arm64,x86_64') " +
                     "compiles exactly those and fails loud (SWIFTBIND052) if the source lacks one. Unset " +
                     "keeps the historical single-pass arm64 preference. More than one arch fattens the " +
                     "wrapper xcframework via lipo so a single runtimes/<rid>/native/ tree serves both Apple " +
                     "Silicon and Intel/Rosetta consumers.");

    public Option<string[]> FrameworkDependency { get; } = new(
        aliases: new[] { "--framework-dependency" },
        description: "Path to a dependency xcframework. Repeatable. Adds -F search paths for wrapper compilation " +
                     "and PackageReference entries in the emitted .csproj. Requires --xcframework.")
    { AllowMultipleArgumentsPerToken = false };

    public Option<string[]> LinkFramework { get; } = new(
        aliases: new[] { "--link-framework" },
        description: "Apple system framework to link into the wrapper (e.g. 'CoreVideo'). Repeatable. Emits " +
                     "'-framework <name>' on the wrapper link so a force-loaded static-archive source can " +
                     "resolve its system-framework dependencies (which carry no autolink hints and are not " +
                     "discoverable from the binary). Requires --xcframework.")
    { AllowMultipleArgumentsPerToken = false };

    public Option<string[]> LinkLibrary { get; } = new(
        aliases: new[] { "--link-library" },
        description: "System library to link into the wrapper, by linker name (e.g. 'c++' for libc++). Repeatable. " +
                     "Emits '-l<name>' on the wrapper link. Use alongside --link-framework when a static-archive " +
                     "source pulls in C++/library symbols. Requires --xcframework.")
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

    public Option<bool> StrictInputs { get; } = new(
        aliases: new[] { "--strict-inputs" },
        description: "Finding 50: fail-closed on input-edge degradation. When set, any resolver " +
                     "fallback that substitutes a different input than requested (device slice -> " +
                     "simulator slice, arch-specific artifact -> any, degraded auto-detected " +
                     "dependency, etc.) becomes a fatal SWIFTBIND027 error instead of a warning. " +
                     "Wired from the CI compile gate (nuke binding-tests --compile-only / --strict).",
        getDefaultValue: () => false);

    public Option<bool> NoVerifyCSharp { get; } = new(
        aliases: new[] { "--no-verify-csharp" },
        description: "Opt out of the in-generator C# verification gate. By default a standalone, " +
                     "wrapper-compiling generation builds the emitted csproj and fails publication " +
                     "(SWIFTBIND113) when the generated C# does not compile, rather than shipping a " +
                     "binding whose consumer build breaks. Consumers that already compile the binding " +
                     "downstream (the SDK two-pass, the BindingTests app build) pass this to skip the " +
                     "redundant build; it is inert in SDK mode and in --compile-only (no wrapper compile).",
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

    public Option<string?> DetectAppleCrossModuleDeps { get; } = new(
        aliases: new[] { "--detect-apple-cross-module-deps" },
        description: "Detect-apple-cross-module-deps mode: parse a .swiftinterface file's " +
                     "import lines and emit one 'MODULE|PACKAGE_ID|VERSION_RANGE' line per " +
                     "registered cross-Apple-framework dep edge to stdout. Used by the " +
                     "apple-framework-mode SDK target to auto-inject PackageReference items " +
                     "for transitive Apple binding-package deps (e.g. RealityKit -> " +
                     "RealityFoundation). Requires --apple-version. The current module name " +
                     "(used to filter self-references) is derived from the swiftinterface " +
                     "path's parent '<Module>.swiftmodule/' directory. Modules without a " +
                     "registered packageId in apple-frameworks.json are silently skipped.");

    public Option<bool> SliceXcframework { get; } = new(
        aliases: new[] { "--slice-xcframework" },
        description: "Slice-xcframework mode: copy a source xcframework to -o, retaining only " +
                     "the slices a given NuGet RID can consume (the rest of the slices are " +
                     "dropped from the staged copy and from a pruned root Info.plist). " +
                     "Requires --xcframework, --rid, and -o. Used by the SDK pack pipeline so " +
                     "per-RID runtimes/<rid>/native/ directories no longer ship slices the RID " +
                     "cannot use. Slice copies use ditto to preserve symlinks, xattrs, " +
                     "executable bits, and per-framework _CodeSignature/.",
        getDefaultValue: () => false);

    public Option<string?> Rid { get; } = new(
        aliases: new[] { "--rid" },
        description: "NuGet runtime identifier (e.g. ios-arm64, tvos-arm64, osx-arm64, " +
                     "maccatalyst-arm64). Used by --slice-xcframework to pick which slices " +
                     "of a source xcframework to retain.");

    public Option<bool> ResolveAutoDeps { get; } = new(
        aliases: new[] { "--resolve-auto-deps" },
        description: "Resolve-auto-deps mode: read the percent-encoded auto-dependency spec " +
                     "from --auto-dep-spec (semicolon-delimited 'Module|PackageId|Version|" +
                     "XCFrameworkPath' records), dedup against --explicit-deps, probe for a " +
                     "sibling binding csproj, and emit one 'PROJREF|<absolute-csproj>' or " +
                     "'WARN|<module>|<packageId>|<version>|<xcframework>' line per record to " +
                     "stdout. Used by the SDK's _ResolveSwiftAutoDetectedDependencies target " +
                     "to auto-inject ProjectReference items and surface SWIFTBIND080.",
        getDefaultValue: () => false);

    public Option<string?> AutoDepSpec { get; } = new(
        aliases: new[] { "--auto-dep-spec" },
        description: "Percent-encoded auto-dependency spec for --resolve-auto-deps: " +
                     "semicolon-delimited 'Module|PackageId|Version|XCFrameworkPath' records. " +
                     "Literal |/;/% inside a field are encoded as %7C/%3B/%25.");

    public Option<string?> ExplicitDeps { get; } = new(
        aliases: new[] { "--explicit-deps" },
        description: "Semicolon-delimited module names already declared via " +
                     "SwiftFrameworkDependency. Used by --resolve-auto-deps to skip " +
                     "auto-detected dependencies the project already declares explicitly.");

    public Option<bool> EmitAppleTypesManifest { get; } = new(
        aliases: new[] { "--emit-apple-types-manifest" },
        description: "Emit-apple-types-manifest mode: ingest one or more Apple Xcode SDK ABI JSON dumps and write the " +
                     "SwiftBindings.Apple metadata manifest to -o. Requires --apple-abi-json and --apple-include-types. " +
                     "Pairs with --apple-include-types and --apple-abi-json.",
        getDefaultValue: () => false);

    public Option<string[]> AppleAbiJson { get; } = new(
        aliases: new[] { "--apple-abi-json" },
        description: "Path to an Apple Xcode SDK ABI JSON dump. Repeatable; one dump per (module, platform). Used only by --emit-apple-types-manifest.")
    { AllowMultipleArgumentsPerToken = false };

    public Option<string?> AppleIncludeTypes { get; } = new(
        aliases: new[] { "--apple-include-types" },
        description: "Path to an include-types.json file listing Swift identities (and optional typealiases) to emit into the Apple types manifest. " +
                     "Positive-list only so the supplement never shadows Runtime-owned canonical types.");

    /// <summary>
    /// Default <c>--apple-version</c>: the stamped <c>SwiftBindings.Apple</c> PackageReference
    /// floor (<c>[version,)</c>) when a caller doesn't pass one. This is the MINIMUM published
    /// supplement version whose public surface satisfies everything the emitter currently emits
    /// against SwiftBindings.Apple — a NuGet lower bound / API contract floor, NOT a pin to the
    /// latest release (a floor of the latest would over-constrain consumers on every Apple patch).
    /// A floor `[X,)` resolves to the LOWEST applicable published package, so this must move UP
    /// whenever the emitter starts emitting Apple surface newer than X — otherwise the stamped
    /// floor resolves to a package that lacks it and consumers hit CS1739/CS1061. 26.2.4 is the
    /// first published supplement carrying the `AnyError(ExistentialContainer1, ownsContainer:)`
    /// constructor the OptionalProjection/ExistentialHandler paths emit.
    /// </summary>
    public const string DefaultAppleSupplementVersion = "26.2.4";

    public Option<string> AppleVersion { get; } = new(
        aliases: new[] { "--apple-version" },
        description: "Apple SDK train / SwiftBindings.Apple supplement version (e.g. 26.2.4). " +
                     "Drives the generated PackageReference floor, binding-metadata.props, and — when " +
                     "--apple-sdk-train-major is not set explicitly — the manifest sdk_train.major " +
                     "(parsed from the leading numeric component). Package major tracks Apple SDK train.",
        getDefaultValue: () => DefaultAppleSupplementVersion);

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

    public Option<bool> RegenStdlibConformances { get; } = new(
        aliases: new[] { "--regen-stdlib-conformances" },
        description: "Regenerate/verify the embedded stdlib-conformances.json fact table against a Swift " +
                     "stdlib swift-api-digester dump (--stdlib-dump). Prunes any curated conformance the " +
                     "live stdlib no longer declares (does not widen — the curated type/protocol set is the " +
                     "file's existing entries). Drift-detect by default (non-zero exit when an entry is " +
                     "stale); combine with --stdlib-conformances-write-back to rewrite in place. Requires " +
                     "--stdlib-dump <path> and --stdlib-conformances <path>.",
        getDefaultValue: () => false);

    public Option<string?> StdlibDump { get; } = new(
        aliases: new[] { "--stdlib-dump" },
        description: "Path to a `swift-api-digester -dump-sdk -module Swift` JSON dump. Used with " +
                     "--regen-stdlib-conformances.");

    public Option<string?> StdlibConformances { get; } = new(
        aliases: new[] { "--stdlib-conformances" },
        description: "Path to the stdlib-conformances.json fact table. Used with --regen-stdlib-conformances.");

    public Option<bool> StdlibConformancesWriteBack { get; } = new(
        aliases: new[] { "--stdlib-conformances-write-back" },
        description: "Used with --regen-stdlib-conformances. When set, prunes and rewrites the table at " +
                     "--stdlib-conformances in place, preserving the existing two-space-indent JSON format.",
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

    public Option<string> InterfaceFactsProducer { get; } = new(
        aliases: new[] { "--interface-facts-producer" },
        description: "Producer used to extract supplementary facts from .swiftinterface files. " +
                     "'auto' (default) and 'swift-syntax' both shell out to the SwiftInterfaceParser " +
                     "host binary (built by `nuke compile`) for the full fact set. This generator is " +
                     "macOS-only by design: both values hard-fail on non-Darwin or when the host " +
                     "binary cannot be located — there is no fallback producer. Hard-fails on any " +
                     "host-binary invocation or deserialization error rather than emitting " +
                     "half-correct bindings. The legacy 'regex' producer was removed.",
        getDefaultValue: () => "auto");

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
            AssemblyName,
            SwiftRuntimeVersion,
            WrapperArchitectures,
            TargetArchitectures,
            FrameworkDependency,
            LinkFramework,
            LinkLibrary,
            ModuleDatabase,
            NoAutoDetect,
            StrictInputs,
            NoVerifyCSharp,
            KeepBuiltinDatabase,
            ObjC,
            SkipWrapperCompilation,
            SkipThunkCompilation,
            CompileWrapperOnly,
            CompileBridgeOnly,
            DetectAppleCrossModuleDeps,
            SliceXcframework,
            Rid,
            ResolveAutoDeps,
            AutoDepSpec,
            ExplicitDeps,
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
            RegenStdlibConformances,
            StdlibDump,
            StdlibConformances,
            StdlibConformancesWriteBack,
            AppleSupplementPrototypeDir,
            InterfaceFactsProducer,
            Config,
            Verbose,
            Help,
        };
    }
}
