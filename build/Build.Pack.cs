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
//   4. SwiftBindings.Apple (supplement for Apple Swift-only types)

using System.IO;
using System.Linq;
using System.Xml.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    Target Pack => _ => _
        .DependsOn(Compile, BuildAppleSupplementXcframework)
        .After(BindingTests, PackGate)
        .Requires(() => Version)
        .Executes(() =>
        {
            var outputDir = (AbsolutePath)OutputDir;
            outputDir.CreateDirectory();

            // Pack ships artifacts to NuGet, so --apple-version must be explicit when the
            // supplement is going to be packed — defaulting silently to the main version would
            // let a stale Apple SDK train ride out a future bump. When --skip-apple is set we
            // still need *some* Apple version to stamp into Sdk.props (SwiftAppleSupplementVersion)
            // so SDK consumers' implicit SwiftBindings.Apple PackageReference points at the right
            // floor; fall back to the value already checked into Sdk.props in that case, and log
            // it loudly so it's obvious which Apple train the shipped SDK will reference.
            string appleVersion;
            if (!string.IsNullOrWhiteSpace(AppleVersion))
            {
                appleVersion = AppleVersion!;
            }
            else if (SkipApple)
            {
                appleVersion = ReadSdkPropsAppleSupplementVersion();
                Log.Warning(
                    "--apple-version not provided; using existing Sdk.props SwiftAppleSupplementVersion '{AppleVersion}'. " +
                    "SDK consumers will reference SwiftBindings.Apple [{AppleVersion},). " +
                    "This is correct only if you intend to ship the SDK against the *already-published* Apple supplement at that version.",
                    appleVersion, appleVersion);
            }
            else
            {
                throw new System.InvalidOperationException(
                    "--apple-version is required for 'nuke pack' so the shipped SwiftBindings.Apple " +
                    "nupkg cannot silently ride an unrelated main version. Pass --skip-apple to ship " +
                    "Runtime/SDK/Templates only against an already-published Apple supplement (the " +
                    "existing Sdk.props value will be used).");
            }
            Log.Information("=== Packing SwiftBindings v{Version}{ApplePart} ===",
                Version,
                SkipApple
                    ? $" (Apple supplement skipped; SDK will reference v{appleVersion})"
                    : $" (Apple supplement v{appleVersion})");

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

            // Mark the start of packing so the Windows MAX_PATH ship gate below inspects only the
            // nupkgs this run produces — outputDir (e.g. /tmp/swift-nuget) is not cleaned between
            // runs, and a stale unsafe package from an earlier version must not fail this build.
            var packStartUtc = System.DateTime.UtcNow;

            using var scope = new VersionScope(Version!, RootDirectory, appleVersion);

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
            //    --apple-version; its Runtime ProjectReference is stamped to a floor-only
            //    range (see VersionScope) because the supplement is always brokered by the
            //    SDK, whose own bounded Runtime PackageReference is the actual contract.
            //
            //    --skip-apple short-circuits this step for Runtime/SDK/Templates-only
            //    releases where the existing Apple supplement nupkg on the feed is unchanged.
            //    Sdk.props still carries the SwiftAppleSupplementVersion stamped above, so
            //    SDK consumers continue pointing at the right Apple train.
            if (SkipApple)
            {
                Log.Information("=== [4/4] Skipping SwiftBindings.Apple pack (--skip-apple) ===");
            }
            else
            {
                Log.Information("=== [4/4] Packing SwiftBindings.Apple v{AppleVersion} ===", appleVersion);
                DotNetPack(s => s
                    .SetProject(SourceDir / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj")
                    .SetConfiguration("Release")
                    .SetOutputDirectory(outputDir)
                    .EnableNoLogo()
                    .SetVerbosity(DotNetVerbosity.quiet));
            }

            // Windows MAX_PATH ship gate (issue #40): authoritative per-entry check over every
            // nupkg THIS run produced, using each one's real layout + version (stale packages from
            // prior runs are ignored). Runs even under --skip-apple so Runtime/SDK/Templates are
            // still gated. The Apple supplement is the only deep-path package today, but guarding
            // all of them is cheap and catches a long path in any future package. (The Apple
            // xcframework also gets an earlier tripwire at build time; see Build.WindowsPathGuard.cs.)
            AssertProducedNupkgsWindowsPathSafe(outputDir, packStartUtc);

            // Summary
            var packages = Directory.GetFiles(outputDir, "*.nupkg");
            Log.Information("");
            Log.Information("=== All packages built ===");
            Log.Information("Output: {Dir}", outputDir);
            foreach (var pkg in packages)
                Log.Information("  {Package}", Path.GetFileName(pkg));
            Log.Information("{Count} package(s) created.", packages.Length);
        });

    // Reads the current SwiftAppleSupplementVersion default out of Sdk.props. Used by
    // --skip-apple when --apple-version is omitted: the SDK still has to advertise *some*
    // Apple supplement version in its props (consumers get an implicit PackageReference at
    // [$(SwiftAppleSupplementVersion),)), so we fall back to whatever is checked in.
    string ReadSdkPropsAppleSupplementVersion()
    {
        var sdkProps = SourceDir / "Swift.Bindings.Sdk" / "Sdk" / "Sdk.props";
        var doc = XDocument.Load(sdkProps);
        var element = doc.Descendants("SwiftAppleSupplementVersion").FirstOrDefault()
            ?? throw new System.InvalidOperationException(
                $"Sdk.props at '{sdkProps}' is missing <SwiftAppleSupplementVersion>. " +
                "Cannot infer an Apple supplement version for --skip-apple; pass --apple-version explicitly.");
        var value = element.Value?.Trim();
        if (string.IsNullOrEmpty(value))
            throw new System.InvalidOperationException(
                $"Sdk.props at '{sdkProps}' has an empty <SwiftAppleSupplementVersion>. " +
                "Cannot infer an Apple supplement version for --skip-apple; pass --apple-version explicitly.");
        return value;
    }
}
