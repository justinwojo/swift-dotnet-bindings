// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

[DotNetVerbosityMapping]
partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Solution] readonly Solution Solution = null!;

    // --- Constants ---
    const string DotNetTfm = "net10.0";

    // --- Tool injection ---
    [PathVariable("xcrun")] readonly Tool XcRunTool = null!;

    // --- Parameters ---
    [Parameter("Target Apple platform (ios, macos, tvos)")]
    readonly string Platform = "ios";

    [Parameter("Filter libraries by name (case-insensitive)")]
    readonly string? Filter;

    [Parameter("Validation tier (1, 2, or 0 for all)")]
    readonly int Tier;

    [Parameter("Reuse cached validation output")]
    readonly bool Quick;

    [Parameter("Show detailed compile errors")]
    readonly bool Verbose;

    [Parameter("Package version for NuGet")]
    readonly string? Version;

    [Parameter("NuGet output directory")]
    readonly string OutputDir = Path.Combine(Path.GetTempPath(), "swift-nuget");

    [Parameter("Max parallel validation workers")]
    readonly int Jobs;

    [Parameter("Skip binding regeneration (incremental build)")]
    readonly bool SkipRegen;

    [Parameter("Run only one test class")]
    readonly string? ClassFilter;

    [Parameter("Test timeout in seconds")]
    readonly int Timeout = 90;

    [Parameter("Include device slice in xcframework build")]
    readonly bool IncludeDevice;

    [Parameter("Flake detection mode (run each test 3x)")]
    readonly bool FlakeDetect;

    [Parameter("Run fetch before validation")]
    readonly bool FetchFirst;

    [Parameter("Run sequentially (no parallelism)")]
    readonly bool Serial;

    [Parameter] [Secret] readonly string? NuGetApiKey;

    // --- Computed paths ---
    AbsolutePath SourceDir => RootDirectory / "src";
    AbsolutePath BindingTestsDir => RootDirectory / "BindingTests";
    AbsolutePath LibrariesDir => RootDirectory / ".libraries";
    AbsolutePath ManifestPath => RootDirectory / "build" / "validation-libraries.json";
    AbsolutePath BaselinePath => RootDirectory / ".validation-baseline.json";

    // --- Resolved platform ---
    ApplePlatform ResolvedPlatform => ApplePlatform.FromName(Platform);

    // --- Core targets ---
    Target Compile => _ => _
        .After(Clean)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration("Debug"));
        });

    Target Clean => _ => _
        .Executes(() =>
        {
            DotNetClean(s => s.SetProject(Solution.Path));
        });

    Target SmokeTest => _ => _
        .After(Clean, Compile, Pack, RuntimeTestsMacOS)
        .Executes(() =>
        {
            var platform = ResolvedPlatform;
            Log.Information("Platform: {Name}, SimTarget: {Target}, SimSdk: {Sdk}",
                platform.Name, platform.SimulatorTarget, platform.SimulatorSdkName);

            var sdkPath = XcRun.GetSdkPath(platform.SimulatorSdkName);
            Log.Information("SDK path: {SdkPath}", sdkPath);

            var swiftcPath = XcRun.FindTool("swiftc");
            Log.Information("swiftc path: {SwiftcPath}", swiftcPath);
        });
}
