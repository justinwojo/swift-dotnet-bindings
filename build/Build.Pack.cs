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
        .After(RuntimeTestsMacOS)
        .Requires(() => Version)
        .Executes(() =>
        {
            var outputDir = (AbsolutePath)OutputDir;
            outputDir.CreateDirectory();

            var appleVersion = AppleVersion ?? Version!;
            if (AppleVersion is null)
                Log.Information("=== Packing SwiftBindings v{Version} ===", Version);
            else
                Log.Information("=== Packing SwiftBindings v{Version} (Apple supplement v{AppleVersion}) ===",
                    Version, appleVersion);

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
            //    SDK train. The ProjectReference to Runtime is stamped into the
            //    supplement's nuspec as a PackageReference at the main --version.
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
