// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Linq;
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
            AssertNuGetAuditScope(expectedFramework);
            ProcessTasks.StartProcess("python3",
                    $"-B \"{RootDirectory / "build/scripts/test_run_toolchain.py"}\" -v",
                    workingDirectory: RootDirectory)
                .AssertZeroExitCode();
        });

    /// <summary>
    /// Pins the NuGet audit contract of the NativeAOT device app's restore, the restore that meets
    /// Microsoft's ILCompiler package: a profile's declared advisories become exactly that many
    /// NuGetAuditSuppress items, nothing is suppressed without a profile, and audit stays on with
    /// NU1901-NU1904 still errors, so an advisory not on the list fails the restore. Evaluates the
    /// real project with the selected SDK under each launcher environment shape.
    /// </summary>
    void AssertNuGetAuditScope(string framework)
    {
        const string ProfileVariable = "SWIFT_BINDINGS_TOOLCHAIN_PROFILE";
        const string AdvisoryVariable = "SWIFT_BINDINGS_NUGET_AUDIT_SUPPRESS";
        var project = BindingTestsDir / "RuntimeTestsApp" / "RuntimeTestsApp.csproj";
        string[] Evaluate(string profile, string advisories)
        {
            var arguments = $"msbuild \"{project}\" -nologo -getItem:NuGetAuditSuppress " +
                "-getProperty:TreatWarningsAsErrors -getProperty:NuGetAudit -getProperty:NuGetAuditLevel " +
                "-getProperty:NuGetAuditMode -getProperty:NoWarn -getProperty:WarningsNotAsErrors -getProperty:PublishAot " +
                $"-p:RuntimeIdentifier=ios-arm64 -p:BtRuntimeEngine=nativeaot -p:BtTargetFramework={framework} " +
                $"\"-p:{ProfileVariable}={profile}\" \"-p:{AdvisoryVariable}={advisories.Replace(";", "%3B")}\"";
            var process = ProcessTasks.StartProcess("dotnet", arguments, workingDirectory: RootDirectory, logOutput: false)
                .AssertZeroExitCode();
            using var result = System.Text.Json.JsonDocument.Parse(process.Output.StdToText());
            var properties = result.RootElement.GetProperty("Properties");
            string Property(string name) => properties.GetProperty(name).GetString() ?? "";
            var label = $"{project.Name} under profile '{profile}'";
            if (!Property("PublishAot").Equals("true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{label} did not evaluate as the NativeAOT device app.");
            if (!Property("TreatWarningsAsErrors").Equals("true", StringComparison.OrdinalIgnoreCase)
                || Property("NuGetAudit").Equals("false", StringComparison.OrdinalIgnoreCase)
                || Property("NuGetAuditLevel") is not ("" or "low" or "moderate" or "high")
                || Property("NuGetAuditMode").Equals("direct", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{label} weakened NuGet audit: TreatWarningsAsErrors={Property("TreatWarningsAsErrors")}, " +
                    $"NuGetAudit={Property("NuGetAudit")}, NuGetAuditLevel={Property("NuGetAuditLevel")}, NuGetAuditMode={Property("NuGetAuditMode")}.");
            foreach (var list in new[] { "NoWarn", "WarningsNotAsErrors" })
                if (Property(list).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(code => code.StartsWith("NU190", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"{label} lists a NuGet audit code in {list}: {Property(list)}.");
            return result.RootElement.TryGetProperty("Items", out var items) &&
                   items.TryGetProperty("NuGetAuditSuppress", out var suppressed)
                ? suppressed.EnumerateArray().Select(item => item.GetProperty("Identity").GetString() ?? "").ToArray()
                : Array.Empty<string>();
        }

        var stray = "https://github.com/advisories/GHSA-p3jp-9v83-76w9";
        foreach (var manifest in Directory.GetFiles(RootDirectory / "build" / "toolchains", "*.json").OrderBy(x => x))
        {
            using var profileJson = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            var declared = profileJson.RootElement.TryGetProperty("nugetAuditSuppress", out var entries)
                ? entries.EnumerateArray().Select(entry => entry.GetProperty("advisory").GetString() ?? "").ToArray()
                : Array.Empty<string>();
            var profile = Path.GetFileNameWithoutExtension(manifest);
            var evaluated = Evaluate(profile, string.Join(";", declared));
            if (!evaluated.SequenceEqual(declared))
                throw new InvalidDataException($"Profile {profile} declares [{string.Join(", ", declared)}] but the NativeAOT restore " +
                    $"suppresses [{string.Join(", ", evaluated)}].");
            Log.Information("NuGet audit scope: {Profile} suppresses {Count} declared advisory URL(s); every other advisory fails.",
                profile, evaluated.Length);
        }
        // Outside a profile an exported advisory list is ignored, so nothing is ever suppressed.
        if (Evaluate("", stray) is { Length: > 0 } leaked)
            throw new InvalidDataException($"An unprofiled restore suppressed [{string.Join(", ", leaked)}].");
    }

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
        // The one profile-run exception is the .NET 11 runtime-identity floor (RuntimeTestLane
        // states the policy); every other seed, promotion and auto-update stays refused above.
        if (SeedRuntimeIdentityBaseline &&
            RuntimeLane.IdentitySeedRefusal(ProfileContext.IsIsolated, RuntimeLane.Configuration) is { } refusal)
            throw new InvalidOperationException("--seed-runtime-identity-baseline: " + refusal);
        if (ProfileContext.IsIsolated)
            Log.Information(SeedRuntimeIdentityBaseline
                ? "Toolchain profile: {Profile}; this run may seed only runtime-identity-net11-baseline.json."
                : "Toolchain profile: {Profile}; baseline comparisons are read-only.", ProfileContext.Profile);
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
