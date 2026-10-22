// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;

partial class Build
{
    [Parameter("Path to an isolated .NET 11 dotnet executable for generator-host qualification")]
    readonly string? GeneratorCandidateDotnet;

    Target VerifyGeneratorHosts => _ => _
        .Description("Qualify the net10 generator on 10-only, 11-only and mixed runtime installations.")
        .DependsOn(Compile)
        .Requires(() => GeneratorCandidateDotnet)
        .Executes(() =>
        {
            var generator = SourceDir / "Swift.Bindings/src/bin/Debug/net10.0/Swift.Bindings.dll";
            var evidence = RootDirectory / "artifacts/generator-hosts";
            ProcessTasks.StartProcess("python3",
                $"-B \"{RootDirectory / "build/scripts/verify-generator-hosts.py"}\" " +
                $"--generator \"{generator}\" --legacy-dotnet \"{DotNetHostPath}\" " +
                $"--candidate-dotnet \"{GeneratorCandidateDotnet}\" --evidence \"{evidence}\"",
                workingDirectory: RootDirectory).AssertZeroExitCode();
        });

    Target VerifyManagedCompatibility => _ => _
        .Description("Verify generator hosting and a real .NET 11 desktop consumer of the .NET 10 Runtime.")
        .DependsOn(VerifyGeneratorHosts)
        .Executes(() =>
        {
            ProcessTasks.StartProcess("python3",
                $"-B \"{RootDirectory / "build/scripts/verify-runtime-host.py"}\" " +
                $"--root \"{RootDirectory}\" --dotnet \"{GeneratorCandidateDotnet}\" " +
                $"--evidence \"{RootDirectory / "artifacts/runtime-host"}\"",
                workingDirectory: RootDirectory).AssertZeroExitCode();
        });

}
