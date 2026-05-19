// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.Pack.cs — NuGet packaging
//
// Ports pack-all.sh into a Nuke target with VersionScope for automatic
// version stamping and restoration. Builds all 4 packages in dependency order:
//   1. SwiftBindings.Runtime
//   2. SwiftBindings.Sdk (publish generator + pack)
//   3. SwiftBindings.Templates
//   4. SwiftBindings.Apple (Phase 2 supplement for Apple Swift-only types)

using System.IO;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    Target Pack => _ => _
        .DependsOn(Compile)
        .After(BindingTests, PackGate)
        .Requires(() => Version)
        .Requires(() => AppleVersion)
        .Executes(() =>
        {
            var outputDir = (AbsolutePath)OutputDir;
            outputDir.CreateDirectory();

            // Pack ships artifacts to NuGet, so --apple-version must be explicit — defaulting
            // silently to the main version would let a stale Apple SDK train ride out a future
            // bump. Required() above already fails the build when omitted; this guard is just
            // paranoia in case someone passes an empty string.
            if (string.IsNullOrWhiteSpace(AppleVersion))
                throw new System.InvalidOperationException(
                    "--apple-version is required for 'nuke pack' so the shipped SwiftBindings.Apple " +
                    "nupkg cannot silently ride an unrelated main version.");
            var appleVersion = AppleVersion!;
            Log.Information("=== Packing SwiftBindings v{Version} (Apple supplement v{AppleVersion}) ===",
                Version, appleVersion);

            // Hard-fail when the SwiftInterfaceParser binary is missing. CompileSwiftInterfaceParser
            // logs a warning and returns silently when xcrun can't find the swift toolchain (so a
            // dev without Xcode isn't blocked from running .NET-only targets), but a Pack run that
            // ships an SDK without the host binary advertises `--interface-facts-producer swift-syntax`
            // as supported when it isn't. Either skip Pack or fail loudly — the SDK's contract
            // is to ship the binary alongside the generator.
            var stagedBinary = SwiftInterfaceParserStagingDir / "SwiftInterfaceParser";
            if (!File.Exists(stagedBinary))
            {
                throw new System.InvalidOperationException(
                    $"Pack: expected SwiftInterfaceParser binary at '{stagedBinary}' but it is missing. " +
                    "Run `nuke compile` on a macOS host with the Swift toolchain installed " +
                    "(Xcode or the Command Line Tools) before packing.");
            }
            // Independent integrity check on what we ship: a single-arch binary would fail
            // with "Bad CPU type" on whichever developer host doesn't match.
            AssertUniversal2(stagedBinary);

            using var scope = new VersionScope(Version!, RootDirectory, AppleVersion);

            // 1. Runtime
            Log.Information("=== [1/4] Packing SwiftBindings.Runtime ===");
            DotNetPack(s => s
                .SetProject(SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(outputDir)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            // 2. SDK (publish generator first, then pack)
            Log.Information("=== [2/4] Packing SwiftBindings.Sdk ===");
            Log.Information("  Publishing generator...");
            DotNetPublish(s => s
                .SetProject(SourceDir / "Swift.Bindings" / "src" / "Swift.Bindings.csproj")
                .SetConfiguration("Release")
                .SetOutput(SourceDir / "Swift.Bindings.Sdk" / "tools" / DotNetTfm / "any")
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            Log.Information("  Packing SDK...");
            DotNetPack(s => s
                .SetProject(SourceDir / "Swift.Bindings.Sdk" / "Swift.Bindings.Sdk.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(outputDir)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            // 3. Templates
            Log.Information("=== [3/4] Packing SwiftBindings.Templates ===");
            DotNetPack(s => s
                .SetProject(SourceDir / "Swift.Bindings.Templates" / "Swift.Bindings.Templates.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(outputDir)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            // 4. Apple supplement — versioned independently so it can ship per Apple
            //    SDK train. Pack stamps the supplement's own PackageVersion from
            //    --apple-version; its Runtime ProjectReference is stamped separately
            //    to the main --version's bounded range (see VersionScope).
            Log.Information("=== [4/4] Packing SwiftBindings.Apple v{AppleVersion} ===", appleVersion);
            DotNetPack(s => s
                .SetProject(SourceDir / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(outputDir)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            // Summary
            var packages = Directory.GetFiles(outputDir, "*.nupkg");
            Log.Information("");
            Log.Information("=== All packages built ===");
            Log.Information("Output: {Dir}", outputDir);
            foreach (var pkg in packages)
                Log.Information("  {Package}", Path.GetFileName(pkg));
            Log.Information("{Count} package(s) created.", packages.Length);
        });
}
