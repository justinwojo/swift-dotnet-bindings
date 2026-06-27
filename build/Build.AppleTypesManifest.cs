// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.AppleTypesManifest.cs
//
// Nuke target wrapper around `dotnet run --validate-apple-types-manifest`.
// Probes the live host SDK for every entry in
// `src/Swift.Bindings.Sdk/tools/apple-types-manifest/manifest.json` and either
// detects drift (default) or writes the probed VWT size/alignment/stride back
// into the manifest (when `--write-back` is passed on the nuke command line).

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    [Parameter("Write probed VWT size/alignment/stride back into the manifest")]
    readonly bool WriteBack;

    AbsolutePath AppleTypesManifestPath =>
        RootDirectory / "src" / "Swift.Bindings.Sdk" / "tools" / "apple-types-manifest" / "manifest.json";

    Target ValidateAppleTypesManifest => _ => _
        .DependsOn(Compile)
        .Description("Probe the live Apple SDK for every Apple-types manifest entry; detect VWT drift.")
        .Executes(() =>
        {
            // The manifest probe reads the live Apple SDK, not a `.swiftinterface`, so the
            // SwiftInterfaceParser host binary isn't needed here (and Compile, a dependency of this
            // target, already stages it regardless).
            EnsureGeneratorBuilt(ensureSwiftInterfaceParser: false);

            var args = new System.Collections.Generic.List<string>
            {
                $"\"{GeneratorDll}\"",
                "--validate-apple-types-manifest",
                $"--apple-types-manifest \"{AppleTypesManifestPath}\"",
            };
            if (WriteBack)
                args.Add("--apple-types-manifest-write-back");

            Log.Information("=== Validating Apple types manifest against live host SDK ===");
            Log.Information("Manifest: {Path}", AppleTypesManifestPath);
            if (WriteBack)
                Log.Information("Write-back enabled — manifest will be updated in place.");

            var process = ProcessTasks.StartProcess(
                "dotnet", string.Join(" ", args),
                workingDirectory: RootDirectory,
                logOutput: true);
            process.WaitForExit();

            if (process.ExitCode != 0)
                Assert.Fail($"Apple types manifest validation failed (exit code {process.ExitCode}).");

            Log.Information("Apple types manifest validation passed.");
        });
}
