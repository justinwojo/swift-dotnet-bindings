// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;

partial class Build
{
    [Parameter("Isolated .NET 11 dotnet executable with all four Apple workloads installed")]
    readonly string? Net11ConsumerDotnet;

    [Parameter("Cache containing the pinned, retained .NET 10 mixed-pack nupkgs")]
    readonly string? Net11ConsumerPackageCache;

    Target VerifyNet11PackageConsumer => _ => _
        .Description("Restore .NET 11 Apple consumers of unchanged .NET 10 packages and compile their public APIs.")
        .Requires(() => Net11ConsumerDotnet)
        .Executes(() =>
        {
            var packages = Net11ConsumerPackageCache ??
                (RootDirectory / "artifacts/toolchains/net10-xcode26/packages").ToString();
            ProcessTasks.StartProcess("python3",
                $"-B \"{RootDirectory / "build/scripts/verify-net11-package-consumer.py"}\" " +
                $"--root \"{RootDirectory}\" --dotnet \"{Net11ConsumerDotnet}\" " +
                $"--package-cache \"{packages}\" --evidence \"{RootDirectory / "artifacts/net11-package-consumer"}\"",
                workingDirectory: RootDirectory).AssertZeroExitCode();
        });

    Target Net11PackageConsumerTests => _ => _
        .Description("Test the .NET 11 package consumer's fail-closed evidence checks without Apple workloads.")
        .Executes(() => ProcessTasks.StartProcess("python3",
            $"-B \"{RootDirectory / "build/scripts/test_net11_package_consumer.py"}\" -v",
            workingDirectory: RootDirectory).AssertZeroExitCode());
}
