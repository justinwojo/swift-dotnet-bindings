// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    Target ToolchainProfileTests => _ => _
        .Description("Verify toolchain checkout and process isolation without building Apple apps.")
        .After(Clean, Compile, WithdrawalGateTests, UnitTests, AnalyzerTests, RuntimeUnitTests)
        .Executes(() =>
        {
            var project = ProjectModelTasks.ParseProject(RootDirectory / "build" / "_build.csproj");
            using var projects = project.ProjectCollection;
            var targetFramework = project.GetPropertyValue("TargetFramework");
            var expectedFramework = Environment.GetEnvironmentVariable("SWIFT_BINDINGS_MANAGED_FRAMEWORK") == "net11.0"
                ? "net11.0" : "net10.0";
            if (targetFramework != expectedFramework)
                throw new InvalidDataException($"Nuke project evaluation returned TargetFramework='{targetFramework}', expected {expectedFramework}.");
            var expectedHost = ".NETCoreApp,Version=v" + expectedFramework[3..];
            if (AppContext.TargetFrameworkName != expectedHost)
                throw new InvalidDataException($"Nuke is running as {AppContext.TargetFrameworkName}, expected {expectedHost}.");
            var frameworks = typeof(NuGet.Frameworks.NuGetFramework).Assembly;
            var sdkFrameworks = System.Reflection.AssemblyName.GetAssemblyName(
                Path.Combine(project.GetPropertyValue("MSBuildToolsPath"), "NuGet.Frameworks.dll"));
            if (frameworks.GetName().Version != sdkFrameworks.Version)
                throw new InvalidDataException($"Nuke loaded {frameworks.FullName}; the selected SDK requires {sdkFrameworks.FullName}.");
            Log.Information("Nuke project evaluation: TargetFramework={TargetFramework}; SDK={Sdk}; MSBuild={MSBuild}; NuGet.Frameworks={Frameworks} ({Location}).",
                targetFramework, project.GetPropertyValue("NETCoreSdkVersion"), project.GetType().Assembly.GetName().Version,
                frameworks.GetName().Version, frameworks.Location);
            projects.UnloadAllProjects();
            ProcessTasks.StartProcess("python3",
                    $"-B \"{RootDirectory / "build/scripts/test_run_toolchain.py"}\" -v",
                    workingDirectory: RootDirectory)
                .AssertZeroExitCode();
        });

    ToolchainProfileContext? profileContext;
    ToolchainProfileContext ProfileContext => profileContext ??= ToolchainProfileContext.Create(
        Environment.GetEnvironmentVariable("SWIFT_BINDINGS_TOOLCHAIN_PROFILE"),
        Environment.GetEnvironmentVariable("SWIFT_BINDINGS_TOOLCHAIN_FINGERPRINT"),
        Environment.GetEnvironmentVariable("SWIFT_BINDINGS_SOURCE_FINGERPRINT"));

    protected override void OnBuildInitialized()
    {
        base.OnBuildInitialized();
        foreach (var target in InvokedTargets)
            if (ToolchainProfileContext.IsBaselineMutationTarget(target.Name))
                ProfileContext.AssertBaselineWriteAllowed(target.Name);
        if (SeedRuntimeIdentityBaseline)
            ProfileContext.AssertBaselineWriteAllowed("--seed-runtime-identity-baseline");
        if (ProfileContext.IsIsolated)
            Log.Information("Toolchain profile: {Profile}; baseline comparisons are read-only.", ProfileContext.Profile);
    }

    string? observedToolchainFingerprint;
    string ToolchainFingerprint => observedToolchainFingerprint ??= ProfileContext.IsIsolated
        ? ProfileContext.ToolchainFingerprint
        : CaptureDefaultToolchainFingerprint();

    string CaptureDefaultToolchainFingerprint()
    {
        string Capture(string executable, string arguments)
        {
            try
            {
                var process = ProcessTasks.StartProcess(executable, arguments,
                    workingDirectory: RootDirectory, logOutput: false).AssertWaitForExit();
                return $"{process.ExitCode}\n{process.Output.StdToText()}";
            }
            catch (Exception ex) { return $"unavailable:{ex.GetType().Name}"; }
        }

        var globalJson = RootDirectory / "global.json";
        var identity = Capture("dotnet", "--info") + "\n" +
            (File.Exists(globalJson) ? File.ReadAllText(globalJson) : "") + "\n" +
            Environment.GetEnvironmentVariable("DEVELOPER_DIR");
        if (OperatingSystem.IsMacOS())
            identity += "\n" + Capture("xcodebuild", "-version") + "\n" + Capture("xcrun", "--find swiftc") +
                "\n" + Capture("xcrun", "swiftc --version");
        return ToolchainProfileContext.Hash(identity);
    }

    const string ToolchainEvidenceSidecar = ".toolchain-profile-fingerprint";

    void StampToolchainEvidence(AbsolutePath outputDir)
    {
        if (ProfileContext.IsIsolated)
            File.WriteAllText(outputDir / ToolchainEvidenceSidecar, ProfileContext.EvidenceFingerprint);
    }
}
