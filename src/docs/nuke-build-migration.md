# Nuke Build Migration Design

## Motivation

The project currently relies on ~4,600 lines of bash across 15+ shell scripts for building, testing, validating, and packaging. The most complex scripts (`validate-libraries.sh` at 1,338 lines, `run-runtime-tests.sh` at 857 lines) have outgrown what bash handles well: they contain hand-rolled parallel job execution, embedded Python for JSON processing, temp-file-based result aggregation, and fragile signal/trap handling.

Moving to [Nuke](https://nuke.build/) (v10.1.0, latest) consolidates all build logic into C#, the project's primary language. Every external tool invocation (`xcodebuild`, `xcrun simctl`, `swiftc`, etc.) gets a typed C# wrapper, giving us compile-time safety, proper error handling, real parallelism, and native JSON handling with `System.Text.Json`.

## Current Shell Scripts Inventory

| Script | Lines | Complexity | Purpose |
|--------|-------|-----------|---------|
| `validate-libraries.sh` | 1,338 | High | Parallel binding generation + compilation for 90 library targets, baseline regression tracking |
| `run-runtime-tests.sh` | 857 | High | Simulator/device/macOS lifecycle management, app deployment, test result parsing |
| `build-async-wrapper.sh` | 569 | Medium | Swift wrapper compilation with broken-code stripping |
| `build-wrapper-device.sh` | 557 | Medium | Device-specific wrapper compilation |
| `build-xcframework.sh` | 445 | Medium | Multi-slice xcframework creation (simulator + device) |
| `scripts/fetch-libraries.sh` | 452 | Medium | Clone repos, build xcframeworks, version caching |
| `BindingTests/build-bridge.sh` | 196 | Low | SwiftUI bridge framework compilation |
| `pack-all.sh` | 156 | Low | Version stamping + NuGet pack in dependency order |
| `BindingTests/regenerate-bindings.sh` | 136 | Low | Generator invocation + dependency binding generation |
| `BindingTests/build-and-test.sh` | 129 | Low | Orchestrator: xcframework -> bindings -> compile check -> bridge |
| `scripts/lib.sh` | 95 | Low | Shared helpers (manifest parsing, colors, platform mappings) |
| `run-tests.sh` | 142 | Low | Test suite runner with summary |
| `src/Swift.Bindings.Sdk/build-sdk.sh` | 35 | Low | Publish generator + pack SDK NuGet |
| `build.sh` | 7 | Trivial | `dotnet build SwiftBindings.sln` |

### What These Scripts Actually Do (External Tool Calls)

A complete inventory of every external CLI tool invoked across all scripts:

| Tool | Command | Where Used | Frequency |
|------|---------|-----------|-----------|
| `dotnet` | `build`, `test`, `run`, `publish`, `pack` | Nearly everything | ~20 call sites |
| `xcrun swiftc` | Compile Swift modules, emit libraries | `build-xcframework.sh`, `build-async-wrapper.sh`, `build-bridge.sh`, `build-wrapper-device.sh`, `validate-libraries.sh` | ~12 call sites |
| `xcrun swift-frontend` | Generate ABI JSON from `.swiftinterface` | `build-xcframework.sh`, `validate-libraries.sh` | ~4 call sites |
| `xcrun tapi stubify` | Generate `.tbd` files from dylibs | `build-xcframework.sh` | ~3 call sites |
| `xcodebuild` | Create xcframeworks, SPM builds | `build-xcframework.sh`, `fetch-libraries.sh`, `validate-libraries.sh` | ~6 call sites |
| `xcrun simctl` | Simulator lifecycle (list/boot/install/launch/terminate) | `run-runtime-tests.sh`, `run-tests.sh` | ~10 call sites |
| `xcrun devicectl` | Physical device (list/install/launch/terminate) | `run-runtime-tests.sh` | ~4 call sites |
| `xcrun swift-symbolgraph-extract` | Extract symbol graphs for doc comments | `build-xcframework.sh` | 1 call site |
| `nm -g` | Verify symbol exports in dylibs | `build-async-wrapper.sh` | 1 call site |
| `xcrun --sdk` | Resolve SDK paths | Most scripts | ~8 call sites |
| `git` | SHA, branch, log | `validate-libraries.sh`, `fetch-libraries.sh` | ~5 call sites |
| `python3` | JSON processing (embedded inline) | `validate-libraries.sh`, `run-runtime-tests.sh`, `scripts/lib.sh`, `run-tests.sh` | ~20 inline calls |
| `shasum` | Source fingerprinting for caching | `validate-libraries.sh` | 1 call site |
| `codesign` | Code signing (via xcodebuild) | `fetch-libraries.sh` | Implicit |

## Nuke Framework Overview

### Version and Setup

**Nuke 10.1.0** (released 2025-12-02). Key packages: `Nuke.Build`, `Nuke.Common`. Supports .NET 10 and .NET Standard 2.0.

```bash
# One-time: install global tool
dotnet tool install Nuke.GlobalTool --global

# Setup in repo root (interactive wizard)
nuke :setup
```

The wizard creates:
- `.nuke/` directory with `build.schema.json` and `parameters.json`
- `build.cmd`, `build.ps1`, `build.sh` — cross-platform bootstrappers
- A build project directory (conventionally `build/`) with `_build.csproj` + `Build.cs`

The build project is a **regular .NET console application** — full IntelliSense, debugging, and refactoring.

### Nuke Tool Wrapper Approaches

Nuke provides three tiers of tool integration. For this project, **we use a mix** depending on the tool's complexity:

#### Tier 1: `Tool` Delegate (lightweight, for simple tools)

Best for tools we call a few times with straightforward arguments.

```csharp
// Inject from PATH
[PathVariable("xcrun")] readonly Tool XcRun;
[PathVariable("nm")] readonly Tool Nm;

// Usage — interpolated arguments
XcRun($"--sdk iphonesimulator --show-sdk-path");
Nm($"-g {binaryPath}");
```

The `Tool` delegate executes the process, captures output, and asserts zero exit code by default. Output is returned as `IReadOnlyCollection<Output>` with `StdToText()` and `StdToJson<T>()` extensions.

**Used for:** `nm`, `shasum`, `codesign`, `xcrun --find`, `xcrun --sdk ... --show-sdk-path`

#### Tier 2: Helper Class with `ProcessTasks` (medium, for tools with structured output)

Best for tools where we need to parse JSON output or manage process lifecycle.

```csharp
public static class SimCtl
{
    public static IReadOnlyList<SimDevice> ListDevices()
    {
        var output = ProcessTasks.StartProcess(
            "xcrun", "simctl list devices available -j",
            logOutput: false)
            .AssertWaitForExit()
            .Output.StdToJson<SimCtlDeviceList>();
        // Parse and return typed results
    }
}
```

`ProcessTasks.StartProcess()` returns `IProcess` with:
- `WaitForExit()` / `AssertWaitForExit()` — blocking wait
- `AssertZeroExitCode()` — fail on non-zero
- `Output` — `IReadOnlyCollection<Output>` (stdout + stderr with type tags)
- `ExitCode`, `HasExited`, `Id`, `Kill()`

**Used for:** `SimCtl`, `DeviceCtl`, `XcRun` (SDK path resolution)

#### Tier 3: Full `ToolTasks` + `ToolOptions` (heavyweight, for frequently-used tools with many flags)

Best for tools we call many times with varied, complex argument combinations. Provides compile-time validation and fluent API.

```csharp
// Settings class with Nuke's attribute-based argument building
[Command(Type = typeof(SwiftCompilerTasks), Command = nameof(SwiftCompilerTasks.Swiftc))]
public partial class SwiftcSettings : ToolOptions
{
    [Argument(Format = "-target {value}")]
    public string Target => Get<string>(() => Target);

    [Argument(Format = "-module-name {value}")]
    public string ModuleName => Get<string>(() => ModuleName);

    [Argument(Format = "-emit-module")]
    public bool? EmitModule => Get<bool?>(() => EmitModule);

    [Argument(Format = "-F {value}")]
    public IReadOnlyList<string> FrameworkSearchPaths => Get<List<string>>(() => FrameworkSearchPaths);
}

// Usage:
SwiftCompilerTasks.Swiftc(_ => _
    .SetTarget("arm64-apple-ios15.0-simulator")
    .SetModuleName("SwiftBindingsTestLib")
    .EnableEmitModule()
    .AddFrameworkSearchPaths(buildDir));
```

Nuke auto-generates fluent extension methods per property type:
- **string**: `SetXxx(value)`, `ResetXxx()`
- **bool?**: `SetXxx(value)`, `EnableXxx()`, `DisableXxx()`, `ToggleXxx()`, `ResetXxx()`
- **IReadOnlyList**: `SetXxx(params)`, `AddXxx(params)`, `RemoveXxx(params)`, `ClearXxx()`
- **IReadOnlyDictionary**: `SetXxx(dict)`, `AddXxx(key, value)`, `RemoveXxx(key)`, `ClearXxx()`

All setters return immutable copies (Nuke deep-clones via JSON serialization).

**Used for:** `SwiftCompiler`, `SwiftFrontend`, `XcodeBuild`, `SymbolGraphExtract`

### Nuke Parallelism

**Between targets:** Nuke runs targets sequentially by default (parallel execution is available in CI environments). For our use case, parallelism within targets is more important.

**Within targets:** Use `CombineWith` + `degreeOfParallelism`:

```csharp
// Run binding generation for all 90 targets, 8 at a time
SwiftCompilerTasks.Swiftc(_ => _
    .SetBaseSettings(...)
    .CombineWith(validationTargets, (s, target) => s
        .SetModuleName(target.Framework)
        .SetOutputPath(outputDir / target.Framework)),
    degreeOfParallelism: 8,
    continueOnFailure: true);
```

This replaces the 100+ lines of hand-rolled PID management in `validate-libraries.sh`. The `continueOnFailure: true` parameter collects all exceptions into `AggregateException` after completion — matching our current behavior where validation continues past individual failures.

For non-tool-invocation parallelism (e.g., our validation pipeline where we need to collect results), use standard `Task.WhenAll` with `SemaphoreSlim`:

```csharp
var semaphore = new SemaphoreSlim(maxJobs);
var tasks = targets.Select(async target =>
{
    await semaphore.WaitAsync();
    try { return await GenerateAndCompileAsync(target); }
    finally { semaphore.Release(); }
});
var results = await Task.WhenAll(tasks);
```

### Nuke Parameters

Parameters map CLI arguments to strongly-typed fields:

```csharp
[Parameter("Target Apple platform")] readonly string Platform = "ios";
[Parameter("Filter libraries by name")] readonly string Filter;
[Parameter("Validation tier (1, 2, or 0 for all)")] readonly int Tier;
[Parameter("Reuse cached output")] readonly bool Quick;
[Parameter("Show detailed errors")] readonly bool Verbose;
[Parameter("Package version for NuGet")] readonly string Version;
[Parameter("NuGet output directory")] readonly string OutputDir = "/tmp/swift-nuget/";
[Parameter("Max parallel workers")] readonly int Jobs;
[Parameter("Skip binding regeneration")] readonly bool SkipRegen;
[Parameter("Run only one test class")] readonly string ClassFilter;
[Parameter("Test timeout in seconds")] readonly int Timeout = 90;
[Parameter] [Secret] readonly string NuGetApiKey;
```

Resolved from (in priority order):
1. CLI: `nuke --filter Nuke --verbose --tier 1`
2. Parameter files: `.nuke/parameters.json`
3. Environment variables: `FILTER=Nuke` or `NUKE_FILTER=Nuke`
4. Profiles: `.nuke/parameters.myprofile.json` via `nuke --profile myprofile`

Required parameters with fail-fast:
```csharp
Target Pack => _ => _
    .Requires(() => Version)  // Fails at build init, before any target runs
    .Executes(() => { ... });
```

### Partial Class Organization

Nuke fully supports and encourages splitting the `Build` class across multiple files. Nuke's own repository uses 15+ partial class files. Each file contains related targets:

```csharp
// Build.cs — entry point, parameters, simple targets
partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);
    [Solution] readonly Solution Solution;
    // parameters...
    Target Compile => _ => _...;
}

// Build.Validation.cs — library validation
partial class Build
{
    Target ValidateLibraries => _ => _...;
    Target FetchLibraries => _ => _...;
    // helper methods...
}

// Build.RuntimeTests.cs — simulator/device testing
partial class Build
{
    Target RuntimeTestsSimulator => _ => _...;
    Target RuntimeTestsDevice => _ => _...;
}
```

### Logging

Nuke uses Serilog. Output is auto-classified by log level patterns:

```csharp
Log.Information("Building {Project}...", projectName);  // Structured logging
Log.Warning("Skipping {Framework}: xcframework not found", fw);
Log.Error("Compile failed for {Target}", target);
```

Tool output is automatically logged. Custom log level patterns classify tool output lines:
```csharp
[LogLevelPattern(LogEventLevel.Warning, @"warning:")]
[LogLevelPattern(LogEventLevel.Error, @"error:")]
partial class SwiftCompilerTasks;
```

Verbosity control: `nuke --verbosity Verbose|Normal|Minimal|Quiet`

### Target Failure Handling

```csharp
Target ValidateLibraries => _ => _
    .ProceedAfterFailure()     // Continue even if individual libraries fail
    .Executes(() => { ... });

Target Cleanup => _ => _
    .AssuredAfterFailure()     // Runs regardless (like bash trap EXIT)
    .Executes(() => { ... });
```

### Build Events

Cross-cutting lifecycle hooks:
```csharp
protected override void OnBuildInitialized() { /* print git SHA, worker count */ }
protected override void OnTargetFailed(string target) { /* crash diagnostics */ }
protected override void OnBuildFinished() { /* summary report */ }
```

### Solution Model

```csharp
[Solution] readonly Solution Solution;

// Access projects by name:
var generator = Solution.GetProject("Swift.Bindings");
var runtime = Solution.GetProject("Swift.Runtime");
```

---

## External Tool Wrappers

### Design Decision: Wrapper Tier Per Tool

| Tool | Tier | Rationale |
|------|------|-----------|
| `xcrun swiftc` | **Tier 3** (full `ToolTasks`) | Called ~12 times with many flag combinations, most complex tool |
| `xcrun swift-frontend` | **Tier 3** (full `ToolTasks`) | Called ~4 times, similar flag pattern to swiftc |
| `xcodebuild` | **Tier 3** (full `ToolTasks`) | Two distinct modes (create-xcframework, SPM build) |
| `xcrun simctl` | **Tier 2** (helper class) | Needs JSON parsing, process lifecycle management |
| `xcrun devicectl` | **Tier 2** (helper class) | Similar to simctl but simpler |
| `xcrun swift-symbolgraph-extract` | **Tier 2** (helper class) | Single-purpose, predictable args |
| `xcrun tapi stubify` | **Tier 1** (`Tool` delegate) | Simple: one input, one output, one flag |
| `nm` | **Tier 1** (`Tool` delegate) | Single-purpose: `nm -g <path>` |
| `xcrun --sdk/--find` | **Tier 2** (helper class) | SDK path resolution, used by other tools |
| `dotnet` | **Built-in** | Nuke provides `DotNetBuild`, `DotNetTest`, `DotNetPack`, `DotNetPublish`, `DotNetRun` |
| `git` | **Built-in** | Nuke provides `GitTasks` |

### 1. `XcRun` — SDK and Tool Resolution (Tier 2)

Foundation helper used by other tool wrappers.

```csharp
/// Resolves Apple SDK paths and tool locations via xcrun.
public static class XcRun
{
    /// Returns the SDK path for a given SDK name.
    /// Example: GetSdkPath("iphonesimulator") → "/Applications/Xcode.app/.../iPhoneSimulator.sdk"
    public static AbsolutePath GetSdkPath(string sdkName)
    {
        var output = ProcessTasks.StartProcess(
                "xcrun", $"--sdk {sdkName} --show-sdk-path",
                logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode()
            .Output.StdToText().Trim();
        return (AbsolutePath)output;
    }

    /// Returns the full path to a developer tool.
    /// Example: FindTool("swiftc") → "/usr/bin/swiftc"
    public static string FindTool(string toolName)
    {
        var output = ProcessTasks.StartProcess(
                "xcrun", $"--find {toolName}",
                logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode()
            .Output.StdToText().Trim();
        return output;
    }
}
```

### 2. `SwiftCompilerTasks` — Swift Compiler (Tier 3)

The most frequently invoked tool. Full fluent API.

**Current bash pattern:**
```bash
xcrun swiftc \
    -target "arm64-apple-ios15.0-simulator" \
    -sdk "$SIM_SDK" \
    -emit-module -emit-library -enable-library-evolution -emit-module-interface \
    -module-name "SwiftBindingsTestLib" \
    -F ".build/ios-arm64-simulator" \
    -Xlinker -install_name -Xlinker "@rpath/Lib.framework/Lib" \
    -o "framework/Lib" \
    -emit-module-path "path/to/module.swiftmodule" \
    -emit-module-interface-path "path/to/module.swiftinterface" \
    $SWIFT_FILES
```

**Nuke wrapper:**
```csharp
[PathTool(Executable = "xcrun")]
[LogLevelPattern(LogEventLevel.Warning, @"warning:")]
[LogLevelPattern(LogEventLevel.Error, @"error:")]
public partial class SwiftCompilerTasks : ToolTasks, IRequirePathTool
{
    public const string PathExecutable = "xcrun";

    public static IReadOnlyCollection<Output> Swiftc(Configure<SwiftcSettings> configurator)
        => new SwiftCompilerTasks().Run<SwiftcSettings>(configurator.Invoke(new SwiftcSettings()));

    // Combinatorial overload for parallel compilation
    public static IEnumerable<(SwiftcSettings Settings, IReadOnlyCollection<Output> Output)>
        Swiftc(CombinatorialConfigure<SwiftcSettings> configurator,
               int degreeOfParallelism = 1, bool completeOnFailure = false)
        => configurator.Invoke(Swiftc, degreeOfParallelism, completeOnFailure);
}

[Command(Type = typeof(SwiftCompilerTasks), Command = nameof(SwiftCompilerTasks.Swiftc),
         Arguments = "swiftc")]
public partial class SwiftcSettings : ToolOptions
{
    [Argument(Format = "-target {value}")]
    public string Target => Get<string>(() => Target);

    [Argument(Format = "-sdk {value}")]
    public string Sdk => Get<string>(() => Sdk);

    [Argument(Format = "-module-name {value}")]
    public string ModuleName => Get<string>(() => ModuleName);

    [Argument(Format = "-emit-module")]
    public bool? EmitModule => Get<bool?>(() => EmitModule);

    [Argument(Format = "-emit-library")]
    public bool? EmitLibrary => Get<bool?>(() => EmitLibrary);

    [Argument(Format = "-enable-library-evolution")]
    public bool? EnableLibraryEvolution => Get<bool?>(() => EnableLibraryEvolution);

    [Argument(Format = "-emit-module-interface")]
    public bool? EmitModuleInterface => Get<bool?>(() => EmitModuleInterface);

    [Argument(Format = "-o {value}")]
    public string OutputPath => Get<string>(() => OutputPath);

    [Argument(Format = "-emit-module-path {value}")]
    public string ModulePath => Get<string>(() => ModulePath);

    [Argument(Format = "-emit-module-interface-path {value}")]
    public string ModuleInterfacePath => Get<string>(() => ModuleInterfacePath);

    // -Xlinker -install_name -Xlinker <value> — compound flag
    [Argument(Format = "-Xlinker -install_name -Xlinker {value}")]
    public string InstallName => Get<string>(() => InstallName);

    // Each -F path is emitted separately
    [Argument(Format = "-F {value}")]
    public IReadOnlyList<string> FrameworkSearchPaths => Get<List<string>>(() => FrameworkSearchPaths);

    // Source files as positional arguments at the end
    [Argument(Format = "{value}", Position = 100)]
    public IReadOnlyList<string> SourceFiles => Get<List<string>>(() => SourceFiles);
}

// Auto-generated fluent extensions (Nuke generates these):
public static partial class SwiftcSettingsExtensions
{
    [Pure] public static T SetTarget<T>(this T o, string v) where T : SwiftcSettings
        => o.Modify(b => b.Set(() => o.Target, v));
    [Pure] public static T SetModuleName<T>(this T o, string v) where T : SwiftcSettings
        => o.Modify(b => b.Set(() => o.ModuleName, v));
    [Pure] public static T EnableEmitModule<T>(this T o) where T : SwiftcSettings
        => o.Modify(b => b.Set(() => o.EmitModule, true));
    [Pure] public static T EnableEmitLibrary<T>(this T o) where T : SwiftcSettings
        => o.Modify(b => b.Set(() => o.EmitLibrary, true));
    [Pure] public static T EnableLibraryEvolution<T>(this T o) where T : SwiftcSettings
        => o.Modify(b => b.Set(() => o.EnableLibraryEvolution, true));
    [Pure] public static T AddFrameworkSearchPaths<T>(this T o, params string[] v) where T : SwiftcSettings
        => o.Modify(b => b.AddCollection(() => o.FrameworkSearchPaths, v));
    [Pure] public static T SetSourceFiles<T>(this T o, params string[] v) where T : SwiftcSettings
        => o.Modify(b => b.Set(() => o.SourceFiles, v));
    // ... etc
}
```

**Usage in a target:**
```csharp
var sdkPath = XcRun.GetSdkPath(platform.SimulatorSdkName);

SwiftCompilerTasks.Swiftc(_ => _
    .SetTarget(platform.SimulatorTarget)
    .SetSdk(sdkPath)
    .SetModuleName("SwiftBindingsTestLib")
    .EnableEmitModule()
    .EnableEmitLibrary()
    .EnableLibraryEvolution()
    .EnableEmitModuleInterface()
    .SetOutputPath(frameworkDir / "SwiftBindingsTestLib")
    .SetModulePath(moduleDir / $"{platform.SimulatorModuleSuffix}.swiftmodule")
    .SetModuleInterfacePath(moduleDir / $"{platform.SimulatorModuleSuffix}.swiftinterface")
    .SetInstallName($"@rpath/SwiftBindingsTestLib.framework/SwiftBindingsTestLib")
    .AddFrameworkSearchPaths(buildDir)
    .SetSourceFiles(swiftFiles.ToArray())
    .SetProcessWorkingDirectory(RootDirectory / "BindingTests"));
```

### 3. `SwiftFrontendTasks` — ABI Generation (Tier 3)

**Current bash pattern:**
```bash
xcrun swift-frontend \
    -compile-module-from-interface "module.swiftinterface" \
    -target "arm64-apple-ios15.0-simulator" \
    -module-name "ModuleName" \
    -sdk "$SIM_SDK" \
    -F "$BUILD_DIR" \
    -emit-abi-descriptor-path "abi.json"
```

```csharp
[Command(Type = typeof(SwiftFrontendTasks), Command = nameof(SwiftFrontendTasks.SwiftFrontend),
         Arguments = "swift-frontend -compile-module-from-interface")]
public partial class SwiftFrontendSettings : ToolOptions
{
    [Argument(Format = "{value}", Position = 1)]
    public string SwiftInterfacePath => Get<string>(() => SwiftInterfacePath);

    [Argument(Format = "-target {value}")]
    public string Target => Get<string>(() => Target);

    [Argument(Format = "-module-name {value}")]
    public string ModuleName => Get<string>(() => ModuleName);

    [Argument(Format = "-sdk {value}")]
    public string Sdk => Get<string>(() => Sdk);

    [Argument(Format = "-F {value}")]
    public IReadOnlyList<string> FrameworkSearchPaths => Get<List<string>>(() => FrameworkSearchPaths);

    [Argument(Format = "-emit-abi-descriptor-path {value}")]
    public string AbiDescriptorPath => Get<string>(() => AbiDescriptorPath);
}
```

### 4. `XcodeBuildTasks` — Xcframework Creation and SPM Builds (Tier 3)

Two distinct modes, expressed as separate methods on the same tasks class:

```csharp
[PathTool(Executable = "xcodebuild")]
public partial class XcodeBuildTasks : ToolTasks, IRequirePathTool
{
    public const string PathExecutable = "xcodebuild";

    /// Create an xcframework from one or more framework slices
    public static IReadOnlyCollection<Output> CreateXcframework(
        Configure<CreateXcframeworkSettings> configurator)
        => new XcodeBuildTasks().Run<CreateXcframeworkSettings>(
            configurator.Invoke(new CreateXcframeworkSettings()));

    /// Build via SPM / Xcode scheme (used by fetch-libraries)
    public static IReadOnlyCollection<Output> SpmBuild(Configure<SpmBuildSettings> configurator)
        => new XcodeBuildTasks().Run<SpmBuildSettings>(
            configurator.Invoke(new SpmBuildSettings()));
}

[Command(Type = typeof(XcodeBuildTasks), Command = nameof(XcodeBuildTasks.CreateXcframework),
         Arguments = "-create-xcframework")]
public partial class CreateXcframeworkSettings : ToolOptions
{
    // Each -framework <path> is emitted separately
    [Argument(Format = "-framework {value}")]
    public IReadOnlyList<string> FrameworkPaths => Get<List<string>>(() => FrameworkPaths);

    [Argument(Format = "-output {value}")]
    public string OutputPath => Get<string>(() => OutputPath);
}

[Command(Type = typeof(XcodeBuildTasks), Command = nameof(XcodeBuildTasks.SpmBuild))]
public partial class SpmBuildSettings : ToolOptions
{
    [Argument(Format = "-scheme {value}")]
    public string Scheme => Get<string>(() => Scheme);

    [Argument(Format = "-destination {value}")]
    public string Destination => Get<string>(() => Destination);

    [Argument(Format = "-derivedDataPath {value}")]
    public string DerivedDataPath => Get<string>(() => DerivedDataPath);

    [Argument(Format = "-configuration {value}")]
    public string Configuration => Get<string>(() => Configuration);

    // KEY=VALUE build settings (e.g., BUILD_LIBRARY_FOR_DISTRIBUTION=YES)
    [Argument(Format = "{key}={value}")]
    public IReadOnlyDictionary<string, string> BuildSettings
        => Get<Dictionary<string, string>>(() => BuildSettings);
}
```

**Usage:**
```csharp
// Create xcframework from sim + device slices
XcodeBuildTasks.CreateXcframework(_ => _
    .AddFrameworkPaths(simFrameworkDir, deviceFrameworkDir)
    .SetOutputPath(xcframeworkDir));

// SPM build for a validation library
XcodeBuildTasks.SpmBuild(_ => _
    .SetScheme("Nuke")
    .SetDestination("platform=iOS Simulator,name=iPhone 16")
    .SetDerivedDataPath(derivedData)
    .SetConfiguration("Release")
    .AddBuildSettings("BUILD_LIBRARY_FOR_DISTRIBUTION", "YES")
    .AddBuildSettings("SKIP_INSTALL", "NO"));
```

### 5. `SimCtl` — iOS Simulator Management (Tier 2)

The most complex helper class. Replaces ~200 lines of bash including:
- JSON parsing of `simctl list devices` (currently done with embedded Python)
- Simulator boot + wait-for-boot
- App install + launch with console output capture
- Poll-sleep-kill-grep pattern for test completion detection
- Crash log detection and diagnostic extraction

```csharp
/// Manages iOS Simulator lifecycle via xcrun simctl.
/// Replaces the embedded Python + bash polling pattern in run-runtime-tests.sh.
public static class SimCtl
{
    // --- Device Models ---
    public record SimDevice(string Udid, string Name, string State, bool IsAvailable, string Runtime);

    public enum TestResult { Success, Failure, Crash, LaunchFailure, Timeout }

    public record LaunchResult(TestResult Result, string Output, int? ExitCode, string? CrashLogPath);

    // --- Preferred simulators (matches current bash logic) ---
    private static readonly string[] PreferredDevices = ["iPhone 16", "iPhone 15 Pro", "iPhone 15"];

    // --- Device Discovery ---

    /// Lists all available devices, optionally filtered by runtime.
    /// Replaces: python3 -c "import json; data = json.load(sys.stdin)..."
    public static IReadOnlyList<SimDevice> ListDevices(string? runtimeFilter = null)
    {
        var json = ProcessTasks.StartProcess(
                "xcrun", "simctl list devices available -j",
                logOutput: false)
            .AssertWaitForExit()
            .Output.StdToText();

        var doc = JsonDocument.Parse(json);
        var devices = new List<SimDevice>();

        foreach (var runtime in doc.RootElement.GetProperty("devices").EnumerateObject())
        {
            if (runtimeFilter != null &&
                !runtime.Name.Contains(runtimeFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var device in runtime.Value.EnumerateArray())
            {
                devices.Add(new SimDevice(
                    Udid: device.GetProperty("udid").GetString()!,
                    Name: device.GetProperty("name").GetString()!,
                    State: device.GetProperty("state").GetString()!,
                    IsAvailable: device.GetProperty("isAvailable").GetBoolean(),
                    Runtime: runtime.Name));
            }
        }
        return devices;
    }

    /// Finds first booted simulator, or boots a preferred one.
    public static SimDevice EnsureBootedDevice()
    {
        // Check for already-booted
        var booted = ListDevices("iOS")
            .FirstOrDefault(d => d.State == "Booted");
        if (booted != null) return booted;

        // Find preferred device to boot
        var available = ListDevices("iOS")
            .Where(d => d.IsAvailable && d.Name.Contains("iPhone"))
            .ToList();

        var device = PreferredDevices
            .Select(pref => available.FirstOrDefault(d => d.Name == pref))
            .FirstOrDefault(d => d != null)
            ?? available.FirstOrDefault()
            ?? throw new InvalidOperationException("No available iPhone simulator found");

        Boot(device.Udid);
        return device;
    }

    // --- Lifecycle ---

    public static void Boot(string udid)
    {
        ProcessTasks.StartProcess("xcrun", $"simctl boot {udid}")
            .AssertWaitForExit();
        ProcessTasks.StartProcess("xcrun", $"simctl bootstatus {udid} -b")
            .AssertWaitForExit();
    }

    public static void Install(string udid, string appPath)
    {
        ProcessTasks.StartProcess("xcrun", $"simctl install {udid} {appPath}")
            .AssertWaitForExit();
    }

    /// Launches app, captures console output, detects test completion or crash.
    /// Replaces the 80-line poll-sleep-kill-grep pattern in run-runtime-tests.sh.
    public static LaunchResult Launch(
        string udid, string bundleId, string[] args, TimeSpan timeout)
    {
        var launchArgs = string.Join(" ", args);
        var process = ProcessTasks.StartProcess(
            "xcrun",
            $"simctl launch --console --terminate-running-process {udid} {bundleId} {launchArgs}",
            logOutput: false,
            timeout: (int)timeout.TotalMilliseconds);

        // Collect output, check for success/failure/crash markers
        var output = new StringBuilder();
        var result = TestResult.Timeout;

        try
        {
            process.WaitForExit();
            var text = process.Output.StdToText();
            output.Append(text);

            if (text.Contains("TEST SUCCESS")) result = TestResult.Success;
            else if (text.Contains("TEST FAILURE")) result = TestResult.Failure;
            else if (text.Contains("SIGABRT") || text.Contains("SIGSEGV") ||
                     text.Contains("EXC_BAD_ACCESS")) result = TestResult.Crash;
            else result = TestResult.LaunchFailure;
        }
        catch (ProcessException)
        {
            result = TestResult.Timeout;
        }
        finally
        {
            // Terminate with timeout to avoid hangs
            Terminate(udid, bundleId);
        }

        // Check crash logs if no clear result
        string? crashLog = null;
        if (result is TestResult.Crash or TestResult.LaunchFailure or TestResult.Timeout)
        {
            crashLog = FindLatestCrashLog("RuntimeTestsApp");
        }

        return new LaunchResult(result, output.ToString(), process.ExitCode, crashLog);
    }

    public static void Terminate(string udid, string bundleId)
    {
        try
        {
            var proc = ProcessTasks.StartProcess(
                "xcrun", $"simctl terminate {udid} {bundleId}",
                logOutput: false, timeout: 5000);
            proc.WaitForExit();
        }
        catch { /* Best-effort termination */ }
    }

    /// Reads device log for crash diagnostics.
    public static string ReadLog(string udid, TimeSpan interval, string processName)
    {
        var minutes = (int)Math.Ceiling(interval.TotalMinutes);
        var output = ProcessTasks.StartProcess(
                "xcrun",
                $"simctl spawn {udid} log show --last {minutes}m " +
                $"--predicate 'process == \"{processName}\"' --style compact",
                logOutput: false, timeout: 10000)
            .AssertWaitForExit()
            .Output.StdToText();
        return output;
    }

    private static string? FindLatestCrashLog(string appName)
    {
        var crashDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library/Logs/DiagnosticReports");
        return Directory.GetFiles(crashDir, $"{appName}*.ips")
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
    }
}
```

### 6. `DeviceCtl` — Physical Device Management (Tier 2)

Similar to `SimCtl` but for physical devices via `xcrun devicectl`.

```csharp
public static class DeviceCtl
{
    public record PhysicalDevice(string Udid, string Name);

    /// Finds connected iOS devices.
    /// Replaces: xcrun devicectl list devices | grep -i "iphone" | grep -oE '[0-9A-Fa-f]{8,}-...'
    public static IReadOnlyList<PhysicalDevice> ListDevices()
    {
        var output = ProcessTasks.StartProcess(
                "xcrun", "devicectl list devices",
                logOutput: false)
            .AssertWaitForExit()
            .Output.StdToText();
        // Parse output for UDIDs and device names
        return ParseDeviceList(output);
    }

    public static void Install(string udid, string appPath)
    {
        ProcessTasks.StartProcess(
                "xcrun", $"devicectl device install app --device {udid} {appPath}")
            .AssertWaitForExit();
    }

    public static SimCtl.LaunchResult Launch(
        string udid, string bundleId, string[] args, TimeSpan timeout)
    {
        var launchArgs = string.Join(" ", args);
        var process = ProcessTasks.StartProcess(
            "xcrun",
            $"devicectl device process launch --device {udid} --console {bundleId} {launchArgs}",
            logOutput: false,
            timeout: (int)timeout.TotalMilliseconds);
        // Same completion detection pattern as SimCtl.Launch
        // ...
    }

    public static void Terminate(string udid, string bundleId)
    {
        try
        {
            ProcessTasks.StartProcess(
                "xcrun", $"devicectl device process terminate --device {udid} {bundleId}",
                logOutput: false, timeout: 5000)
                .WaitForExit();
        }
        catch { }
    }
}
```

### 7. `SymbolGraphExtract` — Doc Comment Extraction (Tier 2)

```csharp
public static class SymbolGraphExtract
{
    /// Extracts symbol graph JSON files for a Swift module.
    /// Returns the count of extracted .symbols.json files.
    public static int Extract(string moduleName, string target, string sdk,
        string buildDir, AbsolutePath outputDir)
    {
        Directory.CreateDirectory(outputDir);
        try
        {
            ProcessTasks.StartProcess(
                    "xcrun",
                    $"swift-symbolgraph-extract " +
                    $"-module-name {moduleName} " +
                    $"-target {target} " +
                    $"-sdk {sdk} " +
                    $"-I {buildDir} -F {buildDir} " +
                    $"-output-dir {outputDir} " +
                    $"-pretty-print")
                .AssertWaitForExit();
        }
        catch
        {
            Log.Warning("swift-symbolgraph-extract failed — doc comments won't be available");
            return 0;
        }

        return Directory.GetFiles(outputDir, "*.symbols.json").Length;
    }
}
```

### 8. Simple Tools (Tier 1)

```csharp
// In Build.cs — inject from PATH
[PathVariable("xcrun")] readonly Tool XcRunTool;
[PathVariable("nm")] readonly Tool NmTool;

// Tapi stubify — just a few calls, not worth a full wrapper
void TapiStubify(string inputDylib, string outputTbd)
{
    XcRunTool($"tapi stubify --filetype=tbd-v4 {inputDylib} -o {outputTbd}");
}

// nm — verify symbol exports
bool HasSymbol(string binaryPath, string symbolName)
{
    var output = NmTool($"-g {binaryPath}", logOutput: false);
    return output.Any(line => line.Text.Contains(symbolName));
}
```

---

## Shared Models

### `ApplePlatform` — Platform Configuration

Replaces the ~50 lines of `case "$PLATFORM" in` that appears in 6 scripts:

```csharp
public record ApplePlatform
{
    public required string Name { get; init; }

    // Simulator
    public required string SimulatorSdkName { get; init; }
    public required string SimulatorTarget { get; init; }
    public required string SimulatorSliceId { get; init; }
    public required string SimulatorModuleSuffix { get; init; }
    public required string SimulatorPlistPlatform { get; init; }

    // Device (null for macOS)
    public string? DeviceSdkName { get; init; }
    public string? DeviceTarget { get; init; }
    public string? DeviceSliceId { get; init; }
    public string? DeviceModuleSuffix { get; init; }
    public string? DevicePlistPlatform { get; init; }

    public required string MinOsVersion { get; init; }
    public required string TfmSuffix { get; init; }
    public bool HasSimulator => DeviceSdkName != null;

    public string GetTfm() => $"net10.0-{TfmSuffix}";

    public static ApplePlatform IOS { get; } = new()
    {
        Name = "ios",
        SimulatorSdkName = "iphonesimulator",
        SimulatorTarget = "arm64-apple-ios15.0-simulator",
        SimulatorSliceId = "ios-arm64-simulator",
        SimulatorModuleSuffix = "arm64-apple-ios-simulator",
        SimulatorPlistPlatform = "iPhoneSimulator",
        DeviceSdkName = "iphoneos",
        DeviceTarget = "arm64-apple-ios15.0",
        DeviceSliceId = "ios-arm64",
        DeviceModuleSuffix = "arm64-apple-ios",
        DevicePlistPlatform = "iPhoneOS",
        MinOsVersion = "15.0",
        TfmSuffix = "ios",
    };

    public static ApplePlatform MacOS { get; } = new()
    {
        Name = "macos",
        SimulatorSdkName = "macosx",
        SimulatorTarget = "arm64-apple-macos12.0",
        SimulatorSliceId = "macos-arm64",
        SimulatorModuleSuffix = "arm64-apple-macos",
        SimulatorPlistPlatform = "MacOSX",
        // macOS has no simulator/device distinction
        DeviceSdkName = null,
        DeviceTarget = null,
        DeviceSliceId = null,
        DeviceModuleSuffix = null,
        DevicePlistPlatform = null,
        MinOsVersion = "12.0",
        TfmSuffix = "macos",
    };

    public static ApplePlatform TvOS { get; } = new()
    {
        Name = "tvos",
        SimulatorSdkName = "appletvsimulator",
        SimulatorTarget = "arm64-apple-tvos15.0-simulator",
        SimulatorSliceId = "tvos-arm64-simulator",
        SimulatorModuleSuffix = "arm64-apple-tvos-simulator",
        SimulatorPlistPlatform = "AppleTVSimulator",
        DeviceSdkName = "appletvos",
        DeviceTarget = "arm64-apple-tvos15.0",
        DeviceSliceId = "tvos-arm64",
        DeviceModuleSuffix = "arm64-apple-tvos",
        DevicePlistPlatform = "AppleTVOS",
        MinOsVersion = "15.0",
        TfmSuffix = "tvos",
    };

    public static ApplePlatform FromName(string name) => name switch
    {
        "ios" => IOS,
        "macos" => MacOS,
        "tvos" => TvOS,
        _ => throw new ArgumentException($"Unknown platform: {name}")
    };
}
```

### `ValidationManifest` — Typed JSON Model

Replaces all `python3 -c "import json; ..."` calls:

```csharp
public record ValidationManifest
{
    [JsonPropertyName("libraries")]
    public IReadOnlyList<ValidationLibrary> Libraries { get; init; } = [];

    public static ValidationManifest Load(AbsolutePath path)
        => JsonSerializer.Deserialize<ValidationManifest>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    /// Expands libraries × products × platforms into flat validation targets.
    /// Replaces: manifest_expand_targets() in lib.sh (~30 lines of Python)
    public IReadOnlyList<ValidationTarget> ExpandTargets(
        string? filter = null, int tier = 0, AbsolutePath? librariesDir = null)
    {
        var targets = new List<ValidationTarget>();
        foreach (var lib in Libraries)
        {
            if (tier > 0 && lib.Tier != tier) continue;
            var platforms = lib.Platforms ?? ["ios"];
            foreach (var product in lib.Products)
            {
                if (filter != null && !product.Framework.Contains(filter,
                    StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var platform in platforms)
                {
                    var name = platform == "ios" ? product.Framework
                                                 : $"{product.Framework}@{platform}";
                    targets.Add(new ValidationTarget(
                        Name: name,
                        LibraryName: lib.Name,
                        XcframeworkPath: librariesDir / lib.Name / $"{product.Framework}.xcframework",
                        Mode: lib.Mode,
                        KnownErrors: product.KnownErrors,
                        Platform: platform,
                        Tier: lib.Tier,
                        Dependencies: product.Dependencies ?? [],
                        WrapperDeps: product.WrapperDeps ?? []));
                }
            }
        }
        return targets;
    }
}

public record ValidationLibrary
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("repository")] public string? Repository { get; init; }  // NOT "repo"
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("revision")] public string? Revision { get; init; }      // Git SHA for verification
    [JsonPropertyName("mode")] public string Mode { get; init; } = "source";   // "source", "binary", "manual"
    [JsonPropertyName("minIOS")] public string MinIOS { get; init; } = "15.0";
    [JsonPropertyName("tier")] public int Tier { get; init; } = 1;
    [JsonPropertyName("platforms")] public IReadOnlyList<string>? Platforms { get; init; }
    [JsonPropertyName("buildSettings")] public IDictionary<string, string>? BuildSettings { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }              // Manual mode instructions
    [JsonPropertyName("products")] public IReadOnlyList<ValidationProduct> Products { get; init; } = [];
}

public record ValidationProduct
{
    [JsonPropertyName("framework")] public string Framework { get; init; } = "";
    [JsonPropertyName("scheme")] public string? Scheme { get; init; }          // Xcode scheme (source mode)
    [JsonPropertyName("project")] public string? Project { get; init; }        // .xcodeproj path (optional)
    [JsonPropertyName("knownErrors")] public int KnownErrors { get; init; }
    [JsonPropertyName("dependencies")] public IReadOnlyList<string>? Dependencies { get; init; }
    [JsonPropertyName("wrapper_deps")] public IReadOnlyList<string>? WrapperDeps { get; init; }
}

public record ValidationTarget(
    string Name, string LibraryName, AbsolutePath XcframeworkPath,
    string Mode, int KnownErrors, string Platform, int Tier,
    IReadOnlyList<string> Dependencies, IReadOnlyList<string> WrapperDeps);
```

### `ValidationBaseline` — Regression Detection

Must match the actual `.validation-baseline.json` shape: `{ git_sha, compile_gate: { libraries: { <name>: { compile, errors, lines, dep_compile, swift_compile } } } }`.

```csharp
public record ValidationBaseline
{
    [JsonPropertyName("git_sha")] public string GitSha { get; init; } = "";

    [JsonPropertyName("compile_gate")]
    public CompileGate Gate { get; init; } = new();

    public record CompileGate
    {
        [JsonPropertyName("libraries")]
        public IDictionary<string, LibraryResult> Libraries { get; init; }
            = new Dictionary<string, LibraryResult>();
    }

    public record LibraryResult
    {
        [JsonPropertyName("compile")] public string Compile { get; init; } = "unknown";
        // "ok", "fail", "known_errors", "regressed", "skip", "no_csproj", "infra_fail"
        [JsonPropertyName("errors")] public int Errors { get; init; }
        [JsonPropertyName("lines")] public int Lines { get; init; }
        [JsonPropertyName("dep_compile")] public string DepCompile { get; init; } = "none";
        // "ok", "none", "fail"
        [JsonPropertyName("swift_compile")] public string SwiftCompile { get; init; } = "unknown";
        // "ok", "fail", "no_wrapper", "unknown"
    }

    public static ValidationBaseline Load(AbsolutePath path)
        => File.Exists(path)
            ? JsonSerializer.Deserialize<ValidationBaseline>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            : new();

    public void Save(AbsolutePath path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));

    /// Compares current results against baseline, returns regressions and improvements.
    /// A library "passes" if it compiles standalone (ok/known_errors) OR via dep gate (dep_compile=ok).
    /// This matches the bash regression detector in validate-libraries.sh Phase 5.
    public (IReadOnlyList<string> Regressions, IReadOnlyList<string> Improvements,
            IReadOnlyList<string> Drift) Compare(
        IDictionary<string, LibraryResult> currentResults, bool isFullRun)
    {
        var regressions = new List<string>();
        var improvements = new List<string>();
        var drift = new List<string>();

        foreach (var (name, prev) in Gate.Libraries)
        {
            if (!currentResults.TryGetValue(name, out var curr)) {
                if (isFullRun)
                    regressions.Add($"{name}: {prev.Compile}(present) -> MISSING");
                continue;
            }

            // A library passes if standalone compile is ok/known_errors OR dep_compile is ok
            bool prevOk = prev.Compile is "ok" or "known_errors" || prev.DepCompile == "ok";
            bool currOk = curr.Compile is "ok" or "known_errors" || curr.DepCompile == "ok";

            if (prevOk && !currOk)
                regressions.Add($"{name}: {prev.Compile}({prev.Errors}) -> {curr.Compile}({curr.Errors})");
            else if (!prevOk && currOk)
                improvements.Add($"{name}: {prev.Compile}({prev.Errors}) -> {curr.Compile}({curr.Errors})");
            else if (prevOk && currOk && prev.Errors == 0 && curr.Errors > 0)
                regressions.Add($"{name}: ok(0) -> {curr.Compile}({curr.Errors})");
            else if (prevOk && currOk && prev.Errors > 0 && curr.Errors == 0)
                improvements.Add($"{name}: {prev.Compile}({prev.Errors}) -> ok(0)");

            // Swift wrapper regression
            if (prev.SwiftCompile == "ok" && curr.SwiftCompile == "fail")
                regressions.Add($"{name}: swift:ok -> swift:fail");
            else if (prev.SwiftCompile == "fail" && curr.SwiftCompile == "ok")
                improvements.Add($"{name}: swift:fail -> swift:ok");

            // Line drift (>10% change)
            if (prev.Lines > 0)
            {
                double pct = Math.Abs(curr.Lines - prev.Lines) / (double)prev.Lines * 100;
                if (pct > 10)
                    drift.Add($"{name}: {prev.Lines} -> {curr.Lines} ({pct:F0}%)");
            }
        }

        return (regressions, improvements, drift);
    }
}
```

### `PlistGenerator` — Framework Info.plist

Replaces the 20+ heredoc `cat > Info.plist << 'PLIST'` blocks:

```csharp
public static class PlistGenerator
{
    public static void WriteFrameworkPlist(
        string outputPath, string bundleId, string bundleName,
        string executableName, string minOs, string plistPlatform)
    {
        var content = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>CFBundleExecutable</key>
                <string>{executableName}</string>
                <key>CFBundleIdentifier</key>
                <string>{bundleId}</string>
                <key>CFBundleInfoDictionaryVersion</key>
                <string>6.0</string>
                <key>CFBundleName</key>
                <string>{bundleName}</string>
                <key>CFBundlePackageType</key>
                <string>FMWK</string>
                <key>CFBundleVersion</key>
                <string>1.0</string>
                <key>CFBundleShortVersionString</key>
                <string>1.0</string>
                <key>MinimumOSVersion</key>
                <string>{minOs}</string>
                <key>CFBundleSupportedPlatforms</key>
                <array>
                    <string>{plistPlatform}</string>
                </array>
            </dict>
            </plist>
            """;
        File.WriteAllText(outputPath, content);
    }
}
```

### `VersionScope` — Version Stamping with Cleanup

Replaces the backup-restore-on-trap pattern in `pack-all.sh`:

```csharp
/// Temporarily stamps version numbers in project files.
/// Restores originals on Dispose (even on exception).
public sealed class VersionScope : IDisposable
{
    private readonly Dictionary<string, string> _originals = new();

    public VersionScope(string version, AbsolutePath repoRoot)
    {
        var files = new[]
        {
            repoRoot / "src" / "Swift.Runtime" / "src" / "Swift.Runtime.csproj",
            repoRoot / "src" / "Swift.Bindings.Sdk" / "Swift.Bindings.Sdk.csproj",
            repoRoot / "src" / "Swift.Bindings.Templates" / "Swift.Bindings.Templates.csproj",
            repoRoot / "src" / "Swift.Bindings.Sdk" / "Sdk" / "Sdk.props",
            repoRoot / "src" / "Swift.Bindings.Templates" / "content" / "swift-binding" / "ProjectName.csproj",
        };

        foreach (var file in files)
        {
            _originals[file] = File.ReadAllText(file);
        }

        // Apply version stamps (same sed patterns as pack-all.sh, but in C#)
        StampVersion(files[0], version); // Runtime PackageVersion
        StampVersion(files[1], version); // SDK PackageVersion
        StampVersion(files[2], version); // Templates PackageVersion
        StampSdkProps(files[3], version); // _SwiftBindingSdkVersion + SwiftRuntimeVersion
        StampTemplateSdk(files[4], version); // Sdk="SwiftBindings.Sdk/..."
    }

    public void Dispose()
    {
        foreach (var (file, content) in _originals)
            File.WriteAllText(file, content);
    }

    private static void StampVersion(string file, string version)
    {
        var content = File.ReadAllText(file);
        content = Regex.Replace(content,
            @"<PackageVersion>[^<]*</PackageVersion>",
            $"<PackageVersion>{version}</PackageVersion>");
        File.WriteAllText(file, content);
    }

    private static void StampSdkProps(string file, string version)
    {
        var content = File.ReadAllText(file);
        content = Regex.Replace(content,
            @"<_SwiftBindingSdkVersion>[^<]*</_SwiftBindingSdkVersion>",
            $"<_SwiftBindingSdkVersion>{version}</_SwiftBindingSdkVersion>");
        content = Regex.Replace(content,
            @"(<SwiftRuntimeVersion Condition=""[^""]*"">)[^<]*(</SwiftRuntimeVersion>)",
            $"${{1}}{version}${{2}}");
        File.WriteAllText(file, content);
    }

    private static void StampTemplateSdk(string file, string version)
    {
        var content = File.ReadAllText(file);
        content = Regex.Replace(content,
            @"Sdk=""SwiftBindings\.Sdk/[^""]*""",
            $@"Sdk=""SwiftBindings.Sdk/{version}""");
        File.WriteAllText(file, content);
    }
}
```

---

## Nuke Target Graph

```
Compile
  |
  +-- Test (= run-tests.sh equivalent)
  |     DependsOn:
  |       +-- UnitTests (+ DotNetHostPath workaround)
  |       +-- RuntimeUnitTests (Swift.Runtime tests)
  |       +-- AnalyzerTests
  |     Executes (conditional, macOS + BindingTests/ exists only):
  |       +-- BindingTestsRegression (shared methods, not DependsOn)
  |       |     +-- RegenerateBindings --strict
  |       |     +-- CompileCheck, BuildAsyncWrapper, BuildBridge
  |       |     +-- GenerateCoverageReport
  |       |     +-- CheckBaselines (baselines.json gate)
  |       +-- RuntimeTestsOnSimulator --skip-regen (if xcrun + sim available)
  |
  +-- RuntimeTestsSimulator (standalone, NO DependsOn — manages pipeline internally)
  |     Step 1: Binding pipeline (skipped by --skip-regen)
  |     Step 2: Build app + inject 4 native artifacts (skipped by --skip-build)
  |     Step 3: SimulatorDeploy + RunAndParseResults (crash diagnostics, Mono JIT detection)
  |
  +-- RuntimeTestsDevice (standalone, NO DependsOn)
  |     Step 1: Build xcfw with device slice + device wrappers (skipped by --skip-regen)
  |     Step 2: Publish NativeAOT (skipped by --skip-build)
  |     Step 3: DeviceDeploy + RunAndParseResults
  |
  +-- RuntimeTestsMacOS (standalone, NO DependsOn)
  |     Step 1: Build macOS xcfw + generate macOS bindings (skipped by --skip-regen)
  |     Step 2: Build macOS app + inject native libs (skipped by --skip-build)
  |     Step 3: Run natively
  |
  +-- ValidateLibraries (independent of Test)
  |     +-- FetchLibraries (opt-in via --fetch)
  |     +-- Phase 3a: GenerateAll (parallel, longest-first scheduling)
  |     +-- Phase 3b: CompileWrappersAll (parallel)
  |     +-- Phase 3c-standalone: CompileCSharpAll (parallel, non-dep targets)
  |     +-- Phase 3c-dependency: CascadingDependencyGate (round-based)
  |     +-- Phase 4-5: BaselineComparison + RegressionDetection
  |
  +-- Pack (requires --version)
        +-- VersionScope (stamp + auto-restore)
        +-- PackRuntime
        +-- PublishGenerator + PackSdk
        +-- PackTemplates
```

## Build Class Structure

```csharp
// Build.cs — entry point, parameters, solution model, simple targets
[DotNetVerbosityMapping]
partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Solution] readonly Solution Solution;

    // --- Tool injection ---
    [PathVariable("xcrun")] readonly Tool XcRunTool;
    [PathVariable("nm")] readonly Tool NmTool;

    // --- Parameters ---
    [Parameter("Target Apple platform (ios, macos, tvos)")]
    readonly string Platform = "ios";

    [Parameter("Filter libraries by name (case-insensitive)")]
    readonly string Filter;

    [Parameter("Validation tier (1, 2, or 0 for all)")]
    readonly int Tier;

    [Parameter("Reuse cached validation output")]
    readonly bool Quick;

    [Parameter("Show detailed compile errors")]
    readonly bool Verbose;

    [Parameter("Package version for NuGet")]
    readonly string Version;

    [Parameter("NuGet output directory")]
    readonly string OutputDir = "/tmp/swift-nuget/";

    [Parameter("Max parallel validation workers")]
    readonly int Jobs;

    [Parameter("Skip binding regeneration (incremental build)")]
    readonly bool SkipRegen;

    [Parameter("Run only one test class")]
    readonly string ClassFilter;

    [Parameter("Test timeout in seconds")]
    readonly int Timeout = 90;

    [Parameter("Include device slice in xcframework build")]
    readonly bool IncludeDevice;

    [Parameter("Flake detection mode (run each test 3x)")]
    readonly bool FlakeDetect;

    [Parameter] [Secret] readonly string NuGetApiKey;

    // --- Computed paths ---
    AbsolutePath SourceDir => RootDirectory / "src";
    AbsolutePath BindingTestsDir => RootDirectory / "BindingTests";
    AbsolutePath LibrariesDir => RootDirectory / ".libraries";
    AbsolutePath ManifestPath => RootDirectory / "validation-libraries.json";
    AbsolutePath BaselinePath => RootDirectory / ".validation-baseline.json";

    // --- Resolved platform ---
    ApplePlatform ResolvedPlatform => ApplePlatform.FromName(Platform);

    // --- Core targets ---
    Target Compile => _ => _
        .Executes(() =>
        {
            DotNetBuild(_ => _
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration.Debug));
        });

    Target Clean => _ => _
        .Executes(() =>
        {
            DotNetClean(_ => _.SetProjectFile(Solution));
        });
}
```

### Partial Class Files

```csharp
// Build.Test.cs — unit test targets
//
// IMPORTANT: Must match full run-tests.sh behavior, which includes:
// 1. DotNetHostPath workaround on all dotnet test calls
// 2. BindingTests regression suite with --strict
// 3. Coverage matrix generation + check-baselines.sh
// 4. Must-pass feature degradation detection
// 5. Runtime tests on iOS Simulator (when available)
partial class Build
{
    // DotNetHostPath workaround (run-tests.sh passes this to every dotnet test)
    string DotNetHostPath => ToolPathResolver.GetPathExecutable("dotnet");

    Target UnitTests => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(_ => _
                .SetProjectFile(SourceDir / "Swift.Bindings" / "tests" / "UnitTests")
                .EnableNoBuild()
                .SetConfiguration(Configuration.Debug)
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath={DotNetHostPath}"));
        });

    Target AnalyzerTests => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(_ => _
                .SetProjectFile(SourceDir / "Swift.Analyzers.Tests")
                .EnableNoBuild()
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath={DotNetHostPath}"));
        });

    Target RuntimeUnitTests => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetTest(_ => _
                .SetProjectFile(SourceDir / "Swift.Runtime" / "tests")
                .EnableNoBuild()
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath={DotNetHostPath}"));
        });

    // BindingTestsRegression: forces strict mode on regeneration, runs coverage + baselines.
    // This is the regression gate equivalent of: build-and-test.sh --strict + generate-coverage-report.sh + check-baselines.sh
    // IMPORTANT: This target hardcodes ForceStrict=true for its regeneration step,
    // overriding the user-facing Strict parameter. A plain `nuke test` must fail on
    // non-zero generator exits, matching how run-tests.sh hardcodes --strict.
    Target BindingTestsRegression => _ => _
        .DependsOn(BindingTestsStrict)  // Uses strict-mode binding pipeline (not regular BindingTests)
        .Executes(() =>
        {
            // generate-coverage-report.sh produces output/coverage-matrix.json
            // check-baselines.sh compares against BindingTests/baselines.json:
            //   - generator_exit_code
            //   - must_pass_degraded count
            //   - must_pass_compiled_out count
            //   - known_unsupported_total count
            //   - wrapper_stripped_count (with +2 tolerance)
        });

    // BindingTestsStrict: same as BindingTests but forces strict generator exit checking.
    // Exists so that BindingTestsRegression always uses strict mode, while standalone
    // BindingTests can be invoked without --strict for debugging.
    Target BindingTestsStrict => _ => _
        .Executes(() =>
        {
            // Runs the same pipeline as BindingTests, but with ForceStrict=true passed
            // to RegenerateBindings. This ensures non-zero generator exits fail the build.
            // Implementation: call the shared pipeline methods with strict=true.
        });

    // Full test suite: matches run-tests.sh EXACTLY.
    // run-tests.sh runs these suites in order:
    //   1. Unit Tests (dotnet test UnitTests)
    //   2. Runtime Tests (dotnet test Swift.Runtime/tests)
    //   3. Analyzer Tests (dotnet test Swift.Analyzers.Tests)
    //   4. BindingTests Regression Suite — ONLY IF: macOS + BindingTests/ exists
    //   5. BindingTests Runtime Tests — ONLY IF: above + xcrun + iOS Simulator available
    //
    // All suites run even if earlier ones fail (ProceedAfterFailure). Summary at end.
    //
    // IMPORTANT: BindingTestsRegression is NOT a DependsOn — it's conditional.
    // run-tests.sh skips the entire BindingTests suite on non-macOS or if the
    // directory doesn't exist (lines 83-88). Using DependsOn would unconditionally
    // run it, breaking parity on Linux CI or minimal checkouts.
    Target Test => _ => _
        .DependsOn(UnitTests, RuntimeUnitTests, AnalyzerTests)
        .ProceedAfterFailure()
        .Executes(() =>
        {
            // BindingTests suite (macOS + Xcode only) — run-tests.sh lines 83-88
            if (!EnvironmentInfo.IsOsx)
            {
                Log.Information("Skipping BindingTests (requires macOS with Xcode)");
                return;
            }
            if (!Directory.Exists(BindingTestsDir))
            {
                Log.Information("BindingTests directory not found, skipping");
                return;
            }

            // Suite 4: BindingTests regression (build-and-test.sh --strict + coverage + baselines)
            RunBindingTestsRegression(); // Calls shared methods, not a target dependency

            // Suite 5: Conditional simulator runtime tests (run-tests.sh lines 112-139)
            if (!HasXcrun())
            {
                Log.Information("Skipping BindingTests Runtime Tests (xcrun not available)");
                return;
            }
            if (!HasAvailableSimulator())
            {
                Log.Information("Skipping BindingTests Runtime Tests (no available iPhone simulator)");
                return;
            }

            // Run simulator runtime tests with --skip-regen (bindings already generated
            // by the regression suite above). This matches:
            //   run-tests.sh → run-runtime-tests.sh --skip-regen --timeout 90
            RunRuntimeTestsOnSimulator(skipRegen: true, timeout: 90);
        });
}
```

```csharp
// Build.BindingTests.cs — BindingTests pipeline
partial class Build
{
    Target BuildXcframework => _ => _
        .Executes(() =>
        {
            var platform = ResolvedPlatform;
            var sdkPath = XcRun.GetSdkPath(platform.SimulatorSdkName);
            var buildDir = BindingTestsDir / ".build";
            // ... compile dependency module, main module, create xcframework
            // Uses SwiftCompilerTasks.Swiftc, SwiftFrontendTasks, TapiStubify, XcodeBuild
        });

    // RegenerateBindings must replicate regenerate-bindings.sh which does:
    // 1. Generate main module bindings with --async-library, --symbolgraph, --framework-dependency
    // 2. Generate dependency module bindings (SwiftBindingsTestLibDependency)
    // 3. Move dependency .cs file alongside main bindings
    // 4. Preserve dependency wrapper xcframework for runtime linking
    // 5. Track generator exit code (saved to output/generator-exit-code for baseline checks)
    // 6. Support strict mode (fail on non-zero generator exit)
    //
    // ForceStrict is set by BindingTestsStrict; the user-facing --strict parameter
    // only applies when invoking regenerate-bindings standalone.
    [Parameter("Fail on non-zero generator exit (standalone use)")] readonly bool Strict;
    bool ForceStrict; // Set by BindingTestsStrict target

    Target RegenerateBindings => _ => _
        .DependsOn(BuildXcframework)
        .Executes(() =>
        {
            var outputDir = BindingTestsDir / "output";
            var xcfwDir = BindingTestsDir / ".build" / "SwiftBindingsTestLib.xcframework";
            var depXcfwDir = BindingTestsDir / ".build" / "SwiftBindingsTestLibDependency.xcframework";
            var symbolgraphDir = BindingTestsDir / ".build" / "symbolgraph";

            // Clean output
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            Directory.CreateDirectory(outputDir);

            // Build generator arguments
            var args = $"--xcframework {xcfwDir} -o {outputDir} --async-library SwiftBindings";
            if (Directory.Exists(symbolgraphDir))
                args += $" --symbolgraph {symbolgraphDir}";
            if (Directory.Exists(depXcfwDir))
                args += $" --framework-dependency {depXcfwDir}";

            // Run generator (may exit non-zero for unsupported features)
            var genProcess = ProcessTasks.StartProcess("dotnet", $"run --project {SourceDir / "Swift.Bindings" / "src"} -- {args}");
            genProcess.WaitForExit();
            var exitCode = genProcess.ExitCode;
            File.WriteAllText(outputDir / "generator-exit-code", exitCode.ToString());

            if (exitCode != 0 && (Strict || ForceStrict))
                throw new Exception($"Generator exited with code {exitCode} (strict mode)");

            // Generate dependency module bindings
            if (Directory.Exists(depXcfwDir))
            {
                // ... run generator for dep module, move .cs + wrapper xcframework
            }
        });

    Target CompileCheckBindings => _ => _
        .DependsOn(RegenerateBindings)
        .Executes(() =>
        {
            DotNetBuild(_ => _
                .SetProjectFile(BindingTestsDir / "CompileCheck" / "CompileCheck.csproj")
                .SetConfiguration(Configuration.Debug));
        });

    Target BuildAsyncWrapper => _ => _
        .DependsOn(RegenerateBindings)
        .Executes(() => { /* SwiftCompilerTasks with wrapper stripping logic */ });

    Target BuildBridge => _ => _
        .DependsOn(RegenerateBindings)
        .Executes(() => { /* SwiftCompilerTasks for SwiftUI bridge */ });

    Target BindingTests => _ => _
        .DependsOn(CompileCheckBindings, BuildAsyncWrapper, BuildBridge);
}
```

```csharp
// Build.RuntimeTests.cs — simulator/device/macOS test execution
//
// DESIGN DECISION: Skip modes vs target dependencies
//
// Problem: --skip-regen means "don't rebuild bindings, just install + run" (~5s).
// --skip-build means "don't even rebuild the .NET app, just install + run" (~2s).
// If RuntimeTestsSimulator unconditionally DependsOn(BindingTests), Nuke runs
// the full pipeline before the target body even executes — the skip flags can't work.
//
// Solution: The runtime test targets do NOT depend on BindingTests. Instead:
//   - Default behavior (no skip flags): the target body calls the binding pipeline
//     methods directly, then builds the app, then runs tests.
//   - --skip-regen: skips binding pipeline, just builds app + runs tests.
//   - --skip-build: skips everything, just installs + runs.
//   - Staleness detection: if --skip-regen but Swift sources are newer than bindings,
//     refuse to run (prevents confusing stale-binding failures).
//
// This matches run-runtime-tests.sh which is a self-contained script that
// conditionally calls build-and-test.sh, not a dependency chain.
//
// CRITICAL BEHAVIORS from run-runtime-tests.sh that must be preserved:
//
// 1. FOUR distinct native artifacts injected into the app bundle:
//    a. libSwiftBindingsRuntime.dylib (from src/Swift.Runtime/native/iossimulator/)
//    b. SwiftBindings.framework/SwiftBindings (async wrapper xcframework slice)
//    c. SwiftBindingsTestLibDependency.framework (dependency module)
//    d. SwiftBindingsTestLibDependencySwiftBindings.framework (dependency wrapper)
//    Each with proper framework directory structure + Info.plist
//
// 2. Staleness detection: if Swift sources are newer than generated bindings,
//    refuse to run with --skip-regen (prevents confusing stale-binding failures)
//
// 3. Crash diagnostics (simulator path):
//    - Check ~/Library/Logs/DiagnosticReports for RuntimeTestsApp*.ips crash logs
//    - Compare crash log count before/after run
//    - Read device log via `xcrun simctl spawn <udid> log show --last 3m`
//    - Detect Mono JIT crash signatures (jit-info.c:818, ReleaseHandle, etc.)
//    - Extract pass/fail counts from partial output before crash
//
// 4. Process lifecycle:
//    - 0.25s polling interval for fast response
//    - simctl terminate with its own timeout (simctl can hang on GHA runners)
//    - kill -9 fallback for stuck processes
//    - Don't check crash signals during active polling (Mono malloc assertion
//      fires during background cleanup but the app continues running)
//
// 5. Three distinct platform paths with SEPARATE build steps:
//    - simulator: build for iossimulator-arm64, deploy via simctl
//    - device: build NativeAOT for ios-arm64, deploy via devicectl,
//      uses build-wrapper-device.sh (separate from sim wrapper)
//    - macos: build for osx-arm64, generate macOS-specific bindings,
//      run natively (no simulator/device)

partial class Build
{
    [Parameter("Skip all builds, just install + run")] readonly bool SkipBuild;
    [Parameter("Pre-booted simulator or device UDID")] readonly string DeviceUdid;

    // --skip-build implies --skip-regen (matches run-runtime-tests.sh line 56-59).
    // This is a computed property, not a parameter — the user sets --skip-build,
    // and skip-regen is automatically true.
    bool EffectiveSkipRegen => SkipRegen || SkipBuild;

    // RuntimeTestsSimulator: NO DependsOn — manages its own pipeline internally.
    // This allows --skip-regen and --skip-build to actually skip work.
    Target RuntimeTestsSimulator => _ => _
        .Executes(() =>
        {
            // Step 1: Conditionally run binding pipeline
            if (!EffectiveSkipRegen)
            {
                // Calls the same methods as BindingTests, inline:
                RunBuildXcframework();
                RunRegenerateBindings(strict: false);
                RunCompileCheck();
                RunBuildAsyncWrapper();
                RunBuildBridge();
            }
            else
            {
                // Staleness check: refuse if Swift sources newer than bindings
                AssertBindingsNotStale();
            }

            // Step 2: Build RuntimeTestsApp (unless --skip-build)
            if (!SkipBuild)
            {
                DotNetBuild(_ => _
                    .SetProjectFile(BindingTestsDir / "RuntimeTestsApp")
                    .SetConfiguration(Configuration.Debug));

                // Inject all 4 native artifacts into app bundle Frameworks/
                var appFrameworks = BindingTestsDir / "RuntimeTestsApp" / "bin" / "Debug" /
                    "net10.0-ios" / "iossimulator-arm64" / "RuntimeTestsApp.app" / "Frameworks";
                InjectRuntimeDylib(appFrameworks);
                InjectAsyncWrapper(appFrameworks);
                InjectDependencyFramework(appFrameworks);
                InjectDependencyWrapper(appFrameworks);
            }

            // Step 3: Install + run on simulator
            RunOnSimulator();
        });

    // RuntimeTestsDevice: also NO DependsOn — manages its own pipeline.
    // Device path has its OWN wrapper build step (build-wrapper-device.sh),
    // separate from the simulator wrapper path.
    Target RuntimeTestsDevice => _ => _
        .Executes(() =>
        {
            var device = !string.IsNullOrEmpty(DeviceUdid)
                ? new DeviceCtl.PhysicalDevice(DeviceUdid, "specified")
                : DeviceCtl.ListDevices().FirstOrDefault()
                    ?? throw new InvalidOperationException("No connected iOS device found");

            if (!EffectiveSkipRegen)
            {
                // Device path: build xcframework with --include-device slice
                RunBuildXcframework(includeDevice: true);
                RunRegenerateBindings(strict: false);
                // Build device-specific wrappers (build-wrapper-device.sh)
                RunBuildDeviceWrappers();
                RunBuildBridge(target: "device");
            }
            else
            {
                AssertBindingsNotStale();
            }

            if (!SkipBuild)
            {
                // Publish NativeAOT (takes several minutes)
                DotNetPublish(_ => _
                    .SetProject(BindingTestsDir / "RuntimeTestsApp.Device")
                    .SetConfiguration(Configuration.Release));
            }

            // Install and launch on physical device
            DeviceCtl.Install(device.Udid, appPath);
            var result = DeviceCtl.Launch(device.Udid, bundleId, args,
                TimeSpan.FromSeconds(Timeout));
            // ... result handling (same crash diagnostics pattern)
        });

    // RuntimeTestsMacOS: also NO DependsOn.
    // macOS has its own xcframework build (--platform macos) and generates
    // macOS-specific bindings, not shared with the iOS paths.
    Target RuntimeTestsMacOS => _ => _
        .Executes(() =>
        {
            if (!EffectiveSkipRegen)
            {
                RunBuildXcframework(platform: "macos");
                // Generate macOS-specific bindings (different from iOS)
                RunRegenerateMacOSBindings();
                RunBuildAsyncWrapper(platform: "macos");
            }
            else
            {
                AssertBindingsNotStale();
            }

            if (!SkipBuild)
            {
                DotNetBuild(_ => _
                    .SetProjectFile(BindingTestsDir / "RuntimeTestsApp.Mac")
                    .SetConfiguration(Configuration.Debug));
                InjectMacOSNativeLibraries(); // Into output dir, not app bundle
            }

            // Run natively on macOS (no simulator)
            RunOnMacOS();
        });

    // --- Shared helper: simulator execution with full crash diagnostics ---
    void RunOnSimulator()
    {
        var device = !string.IsNullOrEmpty(DeviceUdid)
            ? new SimCtl.SimDevice(DeviceUdid, "pre-booted", "Booted", true, "")
            : SimCtl.EnsureBootedDevice();
        Log.Information("Using simulator: {Name} ({Udid})", device.Name, device.Udid);

        var crashLogsBefore = CountCrashLogs("RuntimeTestsApp");

        var appPath = BindingTestsDir / "RuntimeTestsApp" / "bin" / "Debug" /
            "net10.0-ios" / "iossimulator-arm64" / "RuntimeTestsApp.app";
        SimCtl.Install(device.Udid, appPath);

        var args = new List<string> { "--platform", "simulator" };
        if (FlakeDetect) args.AddRange(["--flake-detect"]);
        if (!string.IsNullOrEmpty(ClassFilter)) args.AddRange(["--class", ClassFilter]);

        var result = SimCtl.Launch(
            device.Udid, "com.swiftbindings.runtimetestsapp",
            args.ToArray(), TimeSpan.FromSeconds(Timeout));

        Log.Information(result.Output);

        // Crash diagnostics
        if (result.Result is SimCtl.TestResult.Crash or SimCtl.TestResult.LaunchFailure
            or SimCtl.TestResult.Timeout)
        {
            var crashLogsAfter = CountCrashLogs("RuntimeTestsApp");
            if (crashLogsAfter > crashLogsBefore)
            {
                var crashLog = FindLatestCrashLog("RuntimeTestsApp");
                Log.Error("Crash log: {Path}", crashLog);
            }

            var deviceLog = SimCtl.ReadLog(device.Udid, TimeSpan.FromMinutes(3), "RuntimeTestsApp");
            if (deviceLog.Contains("jit-info") || deviceLog.Contains("ReleaseHandle") ||
                deviceLog.Contains("EXC_BAD_ACCESS"))
            {
                Log.Error("Mono JIT crash detected — diagnose root cause (see CLAUDE.md)");
            }
        }

        Assert.True(result.Result == SimCtl.TestResult.Success,
            $"Runtime tests {result.Result}");
    }

    // --- Shared helper: staleness check ---
    void AssertBindingsNotStale()
    {
        var bindingsFile = BindingTestsDir / "output" / "SwiftBindingsTestLib.cs";
        if (!File.Exists(bindingsFile))
            throw new InvalidOperationException("Bindings not found. Run without --skip-regen first.");

        var bindingsTime = File.GetLastWriteTimeUtc(bindingsFile);
        var newerSource = Directory.GetFiles(BindingTestsDir / "Sources" / "SwiftBindingsTestLib",
                "*.swift", SearchOption.AllDirectories)
            .FirstOrDefault(f => File.GetLastWriteTimeUtc(f) > bindingsTime);

        if (newerSource != null)
            throw new InvalidOperationException(
                $"Bindings are stale. Swift source newer than bindings: {newerSource}. " +
                "Run without --skip-regen to regenerate.");
    }
}
```

```csharp
// Build.Validation.cs — library validation pipeline
partial class Build
{
    // --- FetchLibraries must support all 3 modes from fetch-libraries.sh ---
    //
    // "source" mode:
    //   1. Verify revision via `git ls-remote` (if revision field present)
    //   2. `git clone --depth 1 --branch <version> <repository>` into temp dir
    //   3. For each product: `xcodebuild archive` twice (device + simulator)
    //      with BUILD_LIBRARY_FOR_DISTRIBUTION=YES, SKIP_INSTALL=NO, MACH_O_TYPE=mh_dylib
    //      plus any library-level buildSettings from the manifest
    //   4. Inject Swift module interfaces from DerivedData if missing
    //   5. `xcodebuild -create-xcframework` from both archive slices
    //   6. Write version cache file (.libraries/<name>/.version)
    //
    // "binary" mode:
    //   1. Create minimal Package.swift with dependency on the repo
    //   2. `swift package resolve` to download binary xcframeworks
    //   3. Copy xcframeworks from .build/artifacts/ to .libraries/<name>/
    //   4. Write version cache
    //
    // "manual" mode:
    //   1. Check if xcframeworks exist in .libraries/<name>/
    //   2. Print status (present/missing) with the `note` field from manifest
    //   3. No fetch — user must place xcframeworks manually
    //
    // Additional behaviors to preserve:
    //   - Version caching: skip fetch if .libraries/<name>/.version matches
    //   - --force flag: rebuild even if cached
    //   - --list flag: show status without building
    //   - Revision verification: `git ls-remote` to confirm tag SHA matches
    //   - Product-level scheme + optional project fields for xcodebuild

    [Parameter("Force rebuild even if cached")] readonly bool Force;
    [Parameter("Show library status without building")] readonly bool List;

    Target FetchLibraries => _ => _
        .Executes(() =>
        {
            var manifest = ValidationManifest.Load(ManifestPath);
            var libraries = manifest.Libraries
                .Where(lib => Filter == null || lib.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))
                .Where(lib => Tier == 0 || lib.Tier == Tier);

            foreach (var lib in libraries)
            {
                // Check cache (skip if version matches and not --force)
                if (!Force && IsCached(lib.Name, lib.Version ?? "manual"))
                {
                    Log.Debug("{Name}: cached", lib.Name);
                    continue;
                }

                switch (lib.Mode)
                {
                    case "source":
                        BuildFromSource(lib);   // git clone + xcodebuild archive
                        break;
                    case "binary":
                        ResolveBinary(lib);     // swift package resolve
                        break;
                    case "manual":
                        CheckManual(lib);       // verify xcframeworks exist
                        break;
                }
            }
        });

    [Parameter("Run sequentially (no parallelism)")] readonly bool Serial;

    Target ValidateLibraries => _ => _
        .DependsOn(Compile)
        .Executes(async () =>
        {
            var manifest = ValidationManifest.Load(ManifestPath);
            var targets = manifest.ExpandTargets(Filter, Tier, LibrariesDir);
            var maxJobs = Serial ? 1 : (Jobs > 0 ? Jobs : Math.Max(1, Environment.ProcessorCount - 2));
            maxJobs = Math.Min(maxJobs, 16);

            // Branch-scoped output directory (prevents parallel worktree conflicts)
            var branch = GitTasks.GitCurrentBranch()?.Replace('/', '-') ?? "default";
            var outputBase = (AbsolutePath)$"/tmp/binding-validation-{branch}";

            // Source fingerprinting: skip build if generator hasn't changed
            if (!Quick) BuildGeneratorIfChanged(outputBase);

            // Longest-first scheduling: sort by baseline lines count (descending)
            // to ensure slow targets start first, reducing total wall-clock time
            var baseline = ValidationBaseline.Load(BaselinePath);
            var sortedTargets = targets
                .OrderByDescending(t =>
                    baseline.Gate.Libraries.TryGetValue(t.Name, out var bl) ? bl.Lines : 0)
                .ToList();
            var displayTargets = targets; // Preserve manifest order for output

            Log.Information("Validating {Count} targets with {Jobs} workers",
                targets.Count, maxJobs);

            var semaphore = new SemaphoreSlim(maxJobs);
            var results = new ConcurrentDictionary<string, ValidationBaseline.LibraryResult>();

            // Phase 3a: Generate bindings (parallel, sorted longest-first)
            await Task.WhenAll(sortedTargets.Select(async target =>
            {
                await semaphore.WaitAsync();
                try { await GenerateBindingsAsync(target, outputBase, results); }
                finally { semaphore.Release(); }
            }));

            // Phase 3b: Compile Swift wrappers (parallel)
            await Task.WhenAll(sortedTargets.Select(async target =>
            {
                await semaphore.WaitAsync();
                try { await CompileWrapperAsync(target, outputBase, results); }
                finally { semaphore.Release(); }
            }));

            // Phase 3c-standalone: Compile non-dependency targets (parallel)
            var (nonDep, dep) = PartitionByDependencies(sortedTargets);
            await Task.WhenAll(nonDep.Select(async target =>
            {
                await semaphore.WaitAsync();
                try { await CompileCSharpAsync(target, outputBase, results); }
                finally { semaphore.Release(); }
            }));

            // Phase 3c-dependency: Cascading dependency gate
            // Libraries with cross-module dependencies compile in rounds.
            // Each round: find targets whose transitive deps all have compiled DLLs,
            // compile them, their output DLLs unlock the next round.
            // Repeat until no progress (remaining targets have unresolvable deps).
            var closures = ComputeTransitiveDependencyClosures(manifest);
            await CompileDependencyGateCascading(dep, closures, outputBase, results);

            // Phase 4: Baseline comparison
            bool isFullRun = Filter == null && Tier == 0;
            var (regressions, improvements, drift) = baseline.Compare(results, isFullRun);

            foreach (var r in improvements) Log.Information("IMPROVED: {R}", r);
            foreach (var d in drift) Log.Warning("LINE DRIFT: {D}", d);

            if (regressions.Any())
            {
                foreach (var r in regressions) Log.Error("REGRESSION: {R}", r);
                Assert.Fail($"{regressions.Count} regression(s) detected");
            }

            // Update baseline only on full unfiltered runs
            if (isFullRun)
            {
                var newBaseline = new ValidationBaseline
                {
                    GitSha = GitTasks.GitCurrentCommit(),
                    Gate = new() { Libraries = results.ToDictionary(r => r.Key, r => r.Value) }
                };
                newBaseline.Save(BaselinePath);
            }
        });
}
```

```csharp
// Build.Pack.cs — NuGet packaging
partial class Build
{
    Target Pack => _ => _
        .DependsOn(Compile)
        .Requires(() => Version)
        .Executes(() =>
        {
            var outputDir = (AbsolutePath)OutputDir;
            Directory.CreateDirectory(outputDir);

            using var scope = new VersionScope(Version, RootDirectory);

            // 1. Runtime
            Log.Information("Packing SwiftBindings.Runtime...");
            DotNetPack(_ => _
                .SetProject(SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj")
                .SetConfiguration(Configuration.Release)
                .SetOutputDirectory(outputDir)
                .EnableNoLogo());

            // 2. SDK (publish generator first)
            Log.Information("Publishing generator...");
            DotNetPublish(_ => _
                .SetProject(SourceDir / "Swift.Bindings" / "src" / "Swift.Bindings.csproj")
                .SetConfiguration(Configuration.Release)
                .SetOutput(SourceDir / "Swift.Bindings.Sdk" / "tools" / "net10.0" / "any")
                .EnableNoLogo());

            Log.Information("Packing SwiftBindings.Sdk...");
            DotNetPack(_ => _
                .SetProject(SourceDir / "Swift.Bindings.Sdk" / "Swift.Bindings.Sdk.csproj")
                .SetConfiguration(Configuration.Release)
                .SetOutputDirectory(outputDir)
                .EnableNoLogo());

            // 3. Templates
            Log.Information("Packing SwiftBindings.Templates...");
            DotNetPack(_ => _
                .SetProject(SourceDir / "Swift.Bindings.Templates" / "Swift.Bindings.Templates.csproj")
                .SetConfiguration(Configuration.Release)
                .SetOutputDirectory(outputDir)
                .EnableNoLogo());

            // Summary
            var packages = Directory.GetFiles(outputDir, "*.nupkg");
            Log.Information("{Count} package(s) created in {Dir}", packages.Length, outputDir);
            foreach (var pkg in packages)
                Log.Information("  {Package}", Path.GetFileName(pkg));
        });
}
```

---

## Project Structure

```
build/
  _build.csproj              # .NET 10 console app, references Nuke.Build 10.1.0
  Build.cs                    # Entry point, parameters, Compile/Clean targets
  Build.Test.cs               # UnitTests, AnalyzerTests, RuntimeUnitTests, Test
  Build.BindingTests.cs       # BuildXcframework → RegenerateBindings → CompileCheck → Wrappers
  Build.Validation.cs         # ValidateLibraries, FetchLibraries
  Build.RuntimeTests.cs       # RuntimeTestsSimulator, RuntimeTestsDevice
  Build.Pack.cs               # Pack target with VersionScope
  Tools/
    XcRun.cs                  # SDK path + tool resolution (Tier 2)
    SwiftCompilerTasks.cs     # xcrun swiftc fluent wrapper (Tier 3)
    SwiftFrontendTasks.cs     # xcrun swift-frontend fluent wrapper (Tier 3)
    XcodeBuildTasks.cs        # xcodebuild fluent wrapper (Tier 3)
    SimCtl.cs                 # xcrun simctl lifecycle manager (Tier 2)
    DeviceCtl.cs              # xcrun devicectl lifecycle manager (Tier 2)
    SymbolGraphExtract.cs     # swift-symbolgraph-extract (Tier 2)
  Models/
    ApplePlatform.cs          # Platform configuration (replaces case/esac)
    ValidationManifest.cs     # Typed validation-libraries.json
    ValidationBaseline.cs     # Typed .validation-baseline.json with regression detection
    TestResult.cs             # Test outcome enum and result models
  Helpers/
    PlistGenerator.cs         # Framework Info.plist generation
    VersionScope.cs           # IDisposable version stamping for pack
    SwiftSourceStripper.cs    # Strip broken wrapper code before compilation
```

**`_build.csproj`:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <RootNamespace></RootNamespace>
    <NoWarn>CS0649;CS0169</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Nuke.Build" Version="10.1.0" />
  </ItemGroup>
</Project>
```

---

## Migration Strategy

### Phase 1: Scaffolding + Foundations (estimated: 1 session)

1. Run `nuke :setup` in repo root
2. Create `_build.csproj` with `Nuke.Build 10.1.0`
3. Implement shared models: `ApplePlatform`, `PlistGenerator`
4. Implement `XcRun` helper (other tools depend on SDK resolution)
5. Implement `Compile` target (replaces 7-line `build.sh`)
6. Implement `UnitTests`, `AnalyzerTests`, `RuntimeUnitTests` targets
7. Verify: `nuke compile`, `nuke unit-tests` work

**Exit criteria:** `nuke unit-tests`, `nuke analyzer-tests`, and `nuke runtime-unit-tests` pass. (Full `nuke test` parity requires Phases 3-4.)

### Phase 2: Validation Pipeline (estimated: 2-3 sessions)

Highest-value migration — replaces 1,790 lines of bash+Python.

1. Implement `ValidationManifest`, `ValidationBaseline` models
2. Implement `SwiftCompilerTasks` (Tier 3 fluent wrapper)
3. Implement `SwiftFrontendTasks` (Tier 3)
4. Implement `XcodeBuildTasks` (Tier 3, both modes)
5. Implement `ValidateLibraries` target with parallel pipeline
6. Implement `FetchLibraries` target
7. Verify: `nuke validate-libraries --filter Nuke --verbose` matches bash output

**Exit criteria:** `nuke validate-libraries` produces identical pass/fail results to `./validate-libraries.sh` for all 90 targets.

### Phase 3: BindingTests Pipeline (estimated: 2 sessions)

1. Port `build-xcframework.sh` logic into `BuildXcframework` target
2. Port `regenerate-bindings.sh` into `RegenerateBindings` target
3. Port `build-async-wrapper.sh` including Swift source stripping logic
4. Port `build-bridge.sh` into `BuildBridge` target
5. Implement `SymbolGraphExtract` helper
6. Verify: `nuke binding-tests` produces same output as `cd BindingTests && ./build-and-test.sh`

**Exit criteria:** `nuke binding-tests` compiles all generated bindings, wrappers, and bridge.

### Phase 4: Runtime Tests (estimated: 2 sessions)

Replaces the most fragile bash — process lifecycle management.

1. Implement `SimCtl` helper class (Tier 2)
2. Implement `DeviceCtl` helper class (Tier 2)
3. Port simulator path from `run-runtime-tests.sh` into `RuntimeTestsSimulator`
4. Port device path into `RuntimeTestsDevice`
5. Port macOS native path
6. Verify: `nuke runtime-tests-simulator` runs tests on iOS Simulator

**Exit criteria:** `nuke runtime-tests-simulator --class ClosureTests` runs and reports same results as `./run-runtime-tests.sh --class ClosureTests`.

### Phase 5: Packaging + Cleanup (estimated: 1 session)

1. Implement `VersionScope` helper
2. Port `pack-all.sh` into `Pack` target
3. Port `build-sdk.sh` (absorbed into Pack)
4. Update `CLAUDE.md` with new Nuke commands
5. Convert old shell scripts to thin shims (see Migration Safety below)
6. Update CI if applicable

**Exit criteria:** `nuke pack --version 0.1.0` produces identical NuGet packages.

### Migration Safety

**Keep shell compatibility wrappers.** Don't delete the old scripts until feature parity is proven. Instead, convert them to thin shims that delegate to Nuke:

```bash
#!/bin/bash
# validate-libraries.sh — thin wrapper over Nuke (preserved for compatibility)
exec nuke validate-libraries "$@"
```

This reduces CI and documentation churn. Old command invocations continue to work. Remove shims only after a full release cycle confirms Nuke equivalence.

**`build.sh` collision.** Nuke's setup wizard creates `build.sh` in the repo root, which collides with the existing `build.sh`. Resolution: rename Nuke's bootstrapper to `nuke.sh` (or use `dotnet nuke` directly, which doesn't need a bootstrapper). Alternatively, since Nuke is installed as a global tool, the `build.sh` bootstrapper may not be needed at all.

**Use a repo-local tool manifest** instead of requiring `dotnet tool install --global`:
```bash
dotnet new tool-manifest  # creates .config/dotnet-tools.json
dotnet tool install Nuke.GlobalTool
```
Then `dotnet nuke` works without global install.

### Operational Details to Preserve

These behaviors from the bash system are easy to overlook but important for correctness:

1. **Branch-scoped validation cache.** `validate-libraries.sh` uses `/tmp/binding-validation-${branch}` so parallel worktrees don't clobber each other's cached output. The Nuke equivalent should derive the temp directory from the git branch name.

2. **Longest-first scheduling.** Validation sorts targets by baseline `lines` count (descending) before dispatching to parallel workers. This ensures the slowest targets start first, reducing total wall-clock time. Preserve this in the Nuke parallel dispatch.

3. **Generator source fingerprinting.** `validate-libraries.sh` hashes all generator + runtime source files (`shasum -a 256`) and skips the build step if the fingerprint hasn't changed. Port this as a Nuke build cache check.

4. **Full-run vs filtered-run baseline semantics.** Only full, unfiltered, all-tier runs update `.validation-baseline.json`. Filtered or single-tier runs compare against the existing baseline but don't overwrite it (prevents partial corruption).

5. **Cascading dependency gate.** Libraries with dependencies compile in rounds: each round's successful DLL output unlocks the next round's compilations (e.g., StripeCore -> StripePayments -> Stripe). This is not simple parallelism — it's topological-sort with progressive resolution.

6. **All CLI flags from the bash scripts.** Ensure parity for: `--strict`, `--skip-build`, `--device-udid`, `--force`, `--list`, `--serial`, `--quick`, `--fetch`.

---

## CLI Usage (Post-Migration)

```bash
# Equivalent to current scripts:
nuke compile                                       # was: ./build.sh
nuke test                                          # was: ./run-tests.sh (ALL suites + conditional sim runtime)
nuke validate-libraries                            # was: ./validate-libraries.sh
nuke validate-libraries --filter Nuke --verbose    # was: ./validate-libraries.sh --filter Nuke --verbose
nuke validate-libraries --tier 1                   # was: ./validate-libraries.sh --tier 1
nuke validate-libraries --quick                    # was: ./validate-libraries.sh --quick
nuke validate-libraries --jobs 4                   # was: ./validate-libraries.sh --jobs 4
nuke fetch-libraries                               # was: scripts/fetch-libraries.sh
nuke fetch-libraries --filter Nuke                 # was: scripts/fetch-libraries.sh --filter Nuke
nuke pack --version 0.1.0                          # was: ./pack-all.sh --version 0.1.0
nuke binding-tests                                 # was: cd BindingTests && ./build-and-test.sh
nuke runtime-tests-simulator                       # was: cd BindingTests && ./run-runtime-tests.sh
nuke runtime-tests-simulator --skip-regen          # was: ./run-runtime-tests.sh --skip-regen
nuke runtime-tests-simulator --class ClosureTests  # was: ./run-runtime-tests.sh --class ClosureTests
nuke runtime-tests-device                          # was: ./run-runtime-tests.sh --platform device
nuke runtime-tests-macos                           # was: ./run-runtime-tests.sh --platform macos
nuke runtime-tests-simulator --flake-detect        # was: ./run-runtime-tests.sh --flake-detect
nuke runtime-tests-simulator --skip-build          # was: ./run-runtime-tests.sh --skip-build
nuke runtime-tests-simulator --device-udid ABC123  # was: ./run-runtime-tests.sh --device-udid ABC123
nuke fetch-libraries --force                       # was: scripts/fetch-libraries.sh --force
nuke fetch-libraries --list                        # was: scripts/fetch-libraries.sh --list
nuke validate-libraries --serial                   # was: ./validate-libraries.sh --serial
nuke validate-libraries --fetch --filter Nuke      # was: ./validate-libraries.sh --fetch --filter Nuke

# Nuke extras (free):
nuke --plan                                        # Interactive HTML dependency graph
nuke --help                                        # List all targets + parameters with descriptions
nuke --verbosity verbose                           # Detailed logging
nuke --skip binding-tests                          # Run test but skip binding tests sub-target
nuke --continue                                    # Resume from first failure
nuke --profile ci                                  # Load CI-specific parameters from .nuke/parameters.ci.json
```

---

## Key Benefits Over Shell Scripts

1. **No embedded Python.** All JSON (validation manifest, baseline, binding report, simulator device list, coverage matrix) handled by `System.Text.Json` with typed models. The 20+ inline `python3 -c "import json; ..."` calls become `JsonSerializer.Deserialize<T>()`.

2. **Real parallelism.** `validate-libraries.sh` has 100+ lines of PID management, job slot tracking, and `wait` polling. Nuke uses `CombineWith` with `degreeOfParallelism` for tool invocations, and `SemaphoreSlim` + `Task.WhenAll` for custom parallel logic.

3. **Typed platform configuration.** The ~50 lines of `case "$PLATFORM" in` that appears in 6 scripts becomes `ApplePlatform.FromName("ios")` with all 15 properties pre-computed.

4. **Process management.** The 80-line poll-sleep-kill-grep pattern in `run-runtime-tests.sh` becomes `ProcessTasks.StartProcess()` with built-in timeout support and `CancellationTokenSource`.

5. **Version management.** The `pack-all.sh` backup-sed-restore-on-trap pattern becomes `using var scope = new VersionScope(version, root)` with deterministic cleanup.

6. **Shared code.** Tool wrappers and models are reused across targets. `PlistGenerator.WriteFrameworkPlist()` replaces 6 identical 20-line heredoc blocks.

7. **IDE support.** The `build/` project is a normal C# project with full IntelliSense, debugging (attach debugger to build!), refactoring support, and go-to-definition.

8. **Testability.** Tool wrapper logic (argument construction, result parsing, manifest expansion, baseline comparison) can be unit tested. Can't unit test bash scripts.

9. **Built-in features for free.** `nuke --plan` (dependency graph visualization), `nuke --help` (auto-generated from `[Parameter]` descriptions), `--continue` (resume from failure), profiles, secrets management.

10. **Consistent error handling.** No more `set -e` with `set +e` / `set -e` toggles, `|| true` suppression, or `trap ... EXIT` cleanup. C# `try/catch/finally`, `using`, and Nuke's `ProceedAfterFailure()` / `AssuredAfterFailure()`.

---

## Implementation Sessions

Concrete session breakdown for the orchestrator. Each session is one worker, one commit.

**Working directory:** `/Users/wojo/Dev/nuke-build` (git worktree on `nuke-build` branch).

**Important:** This worktree does NOT have `CLAUDE.md` test gates (no `run-tests.sh` expectations, no validation baselines to maintain). The build project is new code — validate by compiling and running targets against the existing repo content.

### Session 1: Nuke Scaffolding + Shared Models + Unit Test Targets ✅ `d529d381`

**Goal:** Get the Nuke build project compiling and running the three unit test suites.

**Deliverables:**
1. Create `build/` directory with `_build.csproj` referencing `Nuke.Build 10.1.0` (target `net10.0`)
2. Create `Build.cs` — entry point, `[Solution]`, parameters, `Compile` and `Clean` targets
3. Create `build/Models/ApplePlatform.cs` — full `ApplePlatform` record with `IOS`, `MacOS`, `TvOS` static instances (copy values from design doc)
4. Create `build/Helpers/PlistGenerator.cs` — `WriteFrameworkPlist` method
5. Create `build/Tools/XcRun.cs` — `GetSdkPath` and `FindTool` helper methods
6. Create `Build.Test.cs` — `UnitTests`, `AnalyzerTests`, `RuntimeUnitTests` targets with `DotNetHostPath` workaround
7. Add `.nuke/` config directory with `parameters.json` pointing to solution
8. Verify: `dotnet build build/_build.csproj` compiles, `nuke compile` builds the solution, `nuke unit-tests` passes

**Exit criteria:** `nuke unit-tests`, `nuke analyzer-tests`, `nuke runtime-unit-tests` all pass.

**Notes:**
- Do NOT use `nuke :setup` — it's interactive. Create the files manually following the design doc's `_build.csproj` and project structure.
- The solution file is `SwiftBindings.sln` at the repo root.
- Nuke global tool may need to be installed: `dotnet tool install Nuke.GlobalTool --global` (or use `dotnet run --project build/_build.csproj`).
- If `nuke` global tool doesn't work, targets can be verified with `dotnet run --project build/_build.csproj -- compile` etc.

### Session 2: Tool Wrappers ✅ `e38cc975`

**Goal:** Implement all CLI tool wrappers that later sessions depend on.

**Deliverables:**
1. Create `build/Tools/SwiftCompiler.cs` — wrapper for `xcrun swiftc` (use `ProcessTasks.StartProcess` with argument building, not full `ToolTasks` — Tier 3 code generation is complex and a Tier 2 helper with a fluent-style settings class is more practical for this project)
2. Create `build/Tools/SwiftFrontend.cs` — wrapper for `xcrun swift-frontend` (ABI JSON generation)
3. Create `build/Tools/XcodeBuild.cs` — wrapper for `xcodebuild` with two modes: `CreateXcframework` and `SpmBuild`/`ArchiveBuild`
4. Create `build/Tools/SymbolGraphExtract.cs` — wrapper for `xcrun swift-symbolgraph-extract`
5. Verify: all wrappers compile, and at minimum `XcRun.GetSdkPath("iphonesimulator")` returns a valid path when called from a simple test target

**Exit criteria:** `dotnet build build/_build.csproj` compiles with all wrappers. A smoke-test target that calls `XcRun.GetSdkPath("iphonesimulator")` and logs the result succeeds.

**Notes:**
- Use Tier 2 approach (helper classes with `ProcessTasks`) for all wrappers. The design doc shows Tier 3 `ToolTasks`/`ToolOptions` patterns, but implementing the full Nuke code generation pipeline is overkill. Build fluent-style settings classes manually — the ergonomics are what matter, not the implementation tier.
- Study the actual bash invocations in `build-xcframework.sh`, `build-async-wrapper.sh`, `validate-libraries.sh` for the exact flags each tool needs.
- For `XcodeBuild.ArchiveBuild`, model the source-mode pattern from `fetch-libraries.sh`: `xcodebuild archive -scheme X -destination Y BUILD_LIBRARY_FOR_DISTRIBUTION=YES ...`

### Session 3: Validation Models + FetchLibraries Target ✅ `03563425`

**Goal:** Implement the typed JSON models for the validation pipeline and the `FetchLibraries` target.

**Deliverables:**
1. Create `build/Models/ValidationManifest.cs` — `ValidationManifest`, `ValidationLibrary`, `ValidationProduct`, `ValidationTarget` records matching the actual `validation-libraries.json` schema (field names: `repository`, `revision`, `mode` = source/binary/manual, `minIOS`, `scheme`, `project`, etc.)
2. Create `build/Models/ValidationBaseline.cs` — `ValidationBaseline` record matching `.validation-baseline.json` shape (`git_sha`, `compile_gate.libraries.<name>.{compile,errors,lines,dep_compile,swift_compile}`), with the full `Compare()` method for regression detection
3. Implement `FetchLibraries` target in `Build.Validation.cs` — all 3 modes (source, binary, manual), version caching, revision verification, `--force`, `--list` flags
4. Verify: `nuke fetch-libraries --filter Nuke` fetches and builds the Nuke xcframework into `.libraries/Nuke/`

**Exit criteria:** `nuke fetch-libraries --list` shows library statuses. `nuke fetch-libraries --filter CryptoSwift` builds the xcframework.

**Notes:**
- Read `validation-libraries.json` and `.validation-baseline.json` carefully — the models must match exactly.
- The source mode in `fetch-libraries.sh` does `xcodebuild archive` (not `xcodebuild build`) twice (device + simulator), then `xcodebuild -create-xcframework`. Study lines 103-246.
- Binary mode creates a minimal `Package.swift`, runs `swift package resolve`, and copies xcframeworks from `.build/artifacts/`. Study lines 250-324.

### Session 4: ValidateLibraries Target ✅ `9c469592`

**Goal:** Implement the full validation pipeline — the highest-value migration target.

**Deliverables:**
1. Implement `ValidateLibraries` target in `Build.Validation.cs`:
   - Phase 3a: Parallel binding generation (longest-first scheduling from baseline)
   - Phase 3b: Parallel Swift wrapper compilation (using `--compile-wrapper-only` generator flag)
   - Phase 3c-standalone: Parallel C# compilation for non-dependency targets (with fallback csproj, runtime DLL patching)
   - Phase 3c-dependency: Cascading dependency gate (topological rounds, transitive closure)
   - Phase 4-5: Baseline comparison + regression detection
2. Branch-scoped output directory (`/tmp/binding-validation-{branch}`)
3. Generator source fingerprinting (skip build when unchanged)
4. `--quick`, `--tier`, `--filter`, `--verbose`, `--jobs`, `--serial`, `--fetch` flags
5. Baseline update only on full unfiltered runs
6. Verify: `nuke validate-libraries --filter Nuke --verbose` produces same result as `./validate-libraries.sh --filter Nuke --verbose`

**Exit criteria:** `nuke validate-libraries --tier 1` produces the same pass/fail counts as `./validate-libraries.sh --tier 1`. No regressions vs the existing `.validation-baseline.json`.

**Notes:**
- This is the most complex session. Study `validate-libraries.sh` thoroughly (all 1,338 lines).
- The dependency gate (lines 864-1090) is non-trivial: transitive closure computation, cascading rounds, DLL-based assembly references between compilations.
- The csproj patching (lines 620-628) replaces `PackageReference` for `SwiftBindings.Runtime` with a local `Reference` to the debug DLL.
- `write_fallback_csproj` (in `lib.sh` lines 158-196) creates a minimal test csproj when the generator's emitted csproj fails.

### Session 5: BindingTests Pipeline

**Goal:** Port the full BindingTests build pipeline — xcframework through bridge compilation.

**Deliverables:**
1. Implement `BuildXcframework` target in `Build.BindingTests.cs` — port `build-xcframework.sh` (dependency module + main module compilation, symbol graph extraction, TBD/ABI generation, xcframework creation, optional device slice)
2. Implement `RegenerateBindings` target — port `regenerate-bindings.sh` (main + dependency module, generator exit code tracking, strict mode via `ForceStrict`)
3. Implement `CompileCheckBindings` target — `dotnet build CompileCheck/CompileCheck.csproj`
4. Implement `BuildAsyncWrapper` target — port `build-async-wrapper.sh` (Swift source stripping of broken wrapper code, swiftc compilation, xcframework creation)
5. Implement `BuildBridge` target — port `build-bridge.sh` (SwiftUI bridge compilation with framework dependencies)
6. Create `build/Helpers/SwiftSourceStripper.cs` — the broken-code stripping logic from `build-async-wrapper.sh`
7. Implement `BindingTests` and `BindingTestsStrict` aggregate targets
8. Verify: `nuke binding-tests` produces same output as `cd BindingTests && ./build-and-test.sh`

**Exit criteria:** `nuke binding-tests` compiles all generated bindings, async wrappers, and SwiftUI bridge without errors. Output file counts match.

**Notes:**
- `build-xcframework.sh` is 445 lines. The platform-dependent variables (lines 48-90) become `ApplePlatform` lookups.
- `build-async-wrapper.sh` has a non-trivial post-processing step that strips broken Swift wrapper functions before compilation. Read lines ~80-200 carefully.
- The bridge build (`build-bridge.sh`) does a smoke-check for expected `@_cdecl` entrypoints before compiling (lines 127-138).

### Session 6: SimCtl + DeviceCtl + Runtime Tests

**Goal:** Implement simulator/device management and the three runtime test targets.

**Deliverables:**
1. Create `build/Tools/SimCtl.cs` — full simulator lifecycle (ListDevices with JSON parsing, EnsureBootedDevice with preferred device list, Boot, Install, Launch with timeout + completion detection, Terminate with its own timeout, ReadLog for crash diagnostics, crash log delta detection)
2. Create `build/Tools/DeviceCtl.cs` — physical device management (ListDevices, Install, Launch, Terminate)
3. Create `build/Models/TestResult.cs` — `TestResult` enum (Success, Failure, Crash, LaunchFailure, Timeout) and `LaunchResult` record
4. Implement `RuntimeTestsSimulator` target in `Build.RuntimeTests.cs` — NO `DependsOn`, manages pipeline internally, `EffectiveSkipRegen` logic, staleness detection, 4 native artifact injection steps, crash diagnostics with Mono JIT detection
5. Implement `RuntimeTestsDevice` target — device wrapper build path, NativeAOT publish, devicectl deployment
6. Implement `RuntimeTestsMacOS` target — macOS xcframework + bindings, native execution
7. All shared helpers: `RunOnSimulator()`, `AssertBindingsNotStale()`, `InjectRuntimeDylib/AsyncWrapper/DependencyFramework/DependencyWrapper()`
8. Verify: `nuke runtime-tests-simulator --skip-regen --timeout 90` runs and reports results

**Exit criteria:** `nuke runtime-tests-simulator` runs tests on iOS Simulator and reports pass/fail matching `./BindingTests/run-runtime-tests.sh`. `--skip-regen` and `--skip-build` fast paths work correctly.

**Notes:**
- `SimCtl.Launch` replaces ~80 lines of poll-sleep-kill-grep bash. Use `ProcessTasks.StartProcess` with timeout, then scan output for `TEST SUCCESS`/`TEST FAILURE`/crash markers.
- The 4 native artifacts to inject into the app bundle (lines 522-628 of `run-runtime-tests.sh`): runtime dylib, async wrapper framework, dependency framework, dependency wrapper framework. Each needs proper framework directory structure.
- Crash diagnostics (lines 771-853): check crash log count delta, read device log via `simctl spawn`, detect Mono JIT crash signatures.

### Session 7: Test Target + Pack Target + Finalization

**Goal:** Implement the full `Test` target (matching `run-tests.sh`), the `Pack` target, and finalize.

**Deliverables:**
1. Implement the `Test` target in `Build.Test.cs` — `DependsOn(UnitTests, RuntimeUnitTests, AnalyzerTests)` + conditional BindingTests in `Executes()` (macOS check, directory check, xcrun check, simulator availability), then conditional simulator runtime tests
2. Implement `BindingTestsRegression` shared method — strict regen + coverage report generation + baselines check (port `check-baselines.sh` logic: generator exit code, must_pass_degraded, compiled_out, known_unsupported, wrapper_stripped_count)
3. Implement `Pack` target in `Build.Pack.cs` with `VersionScope` (version stamping + auto-restore)
4. Create `build/Helpers/VersionScope.cs` — `IDisposable` that stamps 5 version files and restores on dispose
5. Shell compatibility shims: convert `validate-libraries.sh`, `run-tests.sh`, `BindingTests/build-and-test.sh`, `BindingTests/run-runtime-tests.sh` to thin `exec nuke ...` wrappers
6. Rename Nuke's `build.sh` bootstrapper to avoid collision with existing `build.sh`
7. Verify: `nuke test` matches `./run-tests.sh` behavior, `nuke pack --version 0.1.0-test` produces 3 NuGet packages

**Exit criteria:** `nuke test` runs all suites (conditionally including BindingTests and runtime tests on macOS). `nuke pack --version 0.1.0-test` produces `SwiftBindings.Runtime`, `SwiftBindings.Sdk`, and `SwiftBindings.Templates` nupkg files. Shell shims delegate correctly.
