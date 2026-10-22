// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;

partial class Build
{
    [Parameter("Isolated .NET 11 dotnet executable for the focused CoreCLR package canary")]
    readonly string? CoreClrCanaryDotnet;

    [Parameter("Local NuGet feed containing the Runtime package under test")]
    readonly string? CoreClrCanaryFeed;

    [Parameter("Exact SwiftBindings.Runtime package version for the CoreCLR canary")]
    readonly string? CoreClrCanaryRuntimeVersion;

    [Parameter("Explicit Xcode Developer directory for the CoreCLR canary native fixture")]
    readonly string? CoreClrCanaryDeveloperDir;

    Target CoreClrCanary => _ => _
        .Description("Run a package-only .NET 11 CoreCLR native interop canary in Debug and trimmed Release.")
        .Requires(() => CoreClrCanaryDotnet)
        .Requires(() => CoreClrCanaryFeed)
        .Requires(() => CoreClrCanaryRuntimeVersion)
        .Requires(() => CoreClrCanaryDeveloperDir)
        .Executes(() =>
        {
            ProcessTasks.StartProcess("python3",
                $"-B \"{RootDirectory / "build/scripts/run-coreclr-canary.py"}\" " +
                $"--root \"{RootDirectory}\" --dotnet \"{CoreClrCanaryDotnet}\" " +
                $"--feed \"{CoreClrCanaryFeed}\" --runtime-version \"{CoreClrCanaryRuntimeVersion}\" " +
                $"--developer-dir \"{CoreClrCanaryDeveloperDir}\" " +
                $"--evidence \"{RootDirectory / "artifacts/coreclr-canary"}\"",
                workingDirectory: RootDirectory).AssertZeroExitCode();
        });
}
