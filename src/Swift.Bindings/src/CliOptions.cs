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
            Config,
            Verbose,
            Help,
        };
    }
}
