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

            // Pack ships artifacts to NuGet, so --apple-version must always be explicit — even
            // under --skip-apple. The supplement version is baked into the SDK's staged Sdk.props
            // (SwiftAppleSupplementVersion) so SDK consumers' implicit SwiftBindings.Apple
            // PackageReference floors at [appleVersion,); there is no source default to fall back
            // to (Sdk.props carries only a 0.0.0-dev sentinel), so omitting it would silently bake
            // [0.0.0-dev,) into the shipped SDK. The release pipeline already resolves the latest
            // published apple-v* tag and passes it even on the SDK-only lane.
            if (string.IsNullOrWhiteSpace(AppleVersion))
            {
                throw new System.InvalidOperationException(SkipApple
                    ? "--apple-version is required even with --skip-apple: the SDK's Sdk.props must " +
                      "advertise the already-published SwiftBindings.Apple version so consumers' " +
                      "implicit PackageReference floors at the right supplement. Pass the latest " +
                      "published apple-v* version."
                    : "--apple-version is required for 'nuke pack' so the shipped SwiftBindings.Apple " +
                      "nupkg cannot silently ride an unrelated main version.");
            }
            var appleVersion = AppleVersion!;
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

            // Capture the source-controlled version files' bytes before any packing so we can prove
            // the pack rewrote none of them. F6 single-sources every shipped version through MSBuild
            // properties and gitignored staged copies (see VersionScope), so the working tree must be
            // byte-identical afterward. This is the structural counterpart to the retired in-place
            // version stamping: it turns "the pack must not mutate source" from an architectural claim
            // into a gate that fails the pack the instant a regression reintroduces source rewriting.
            var versionFileSnapshot = SnapshotVersionFiles();

            using var scope = new VersionScope(Version!, RootDirectory, appleVersion);

            // 1. Runtime
            Log.Information("=== [1/4] Packing SwiftBindings.Runtime ===");
            DotNetPack(s => scope.Apply(s
                .SetProject(SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(outputDir)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet)));

            // 2. SDK (publish generator first, then pack)
            Log.Information("=== [2/4] Packing SwiftBindings.Sdk ===");
            Log.Information("  Publishing generator...");
            DotNetPublish(s => scope.Apply(s
                .SetProject(SourceDir / "Swift.Bindings" / "src" / "Swift.Bindings.csproj")
                .SetConfiguration("Release")
                .SetOutput(SourceDir / "Swift.Bindings.Sdk" / "tools" / DotNetTfm / "any")
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet)));

            Log.Information("  Packing SDK...");
            DotNetPack(s => scope.Apply(s
                .SetProject(SourceDir / "Swift.Bindings.Sdk" / "Swift.Bindings.Sdk.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(outputDir)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet)));

            // 3. Templates
            Log.Information("=== [3/4] Packing SwiftBindings.Templates ===");
            DotNetPack(s => scope.Apply(s
                .SetProject(SourceDir / "Swift.Bindings.Templates" / "Swift.Bindings.Templates.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(outputDir)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet)));

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
                DotNetPack(s => scope.Apply(s
                    .SetProject(SourceDir / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj")
                    .SetConfiguration("Release")
                    .SetOutputDirectory(outputDir)
                    .EnableNoLogo()
                    .SetVerbosity(DotNetVerbosity.quiet)));
            }

            // The four packs are complete and the VersionScope is still alive: every source-controlled
            // version file must be byte-for-byte what it was before packing. Asserting here — before the
            // scope is disposed — catches not only a regression that rewrites a file and leaves it dirty,
            // but also one that stamps a version in place for the pack and restores it afterward (the
            // retired backup/restore dance), which a snapshot taken after disposal would miss.
            AssertVersionFilesUnmutated(versionFileSnapshot);

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

            // Structural truth gate: open the produced SDK + Templates nupkgs and assert the
            // version baked into their shipped files is the real version, not the 0.0.0-dev
            // sentinel left in source. A single-source regression (a missing property, a renamed
            // element) would otherwise ship a coherent-looking nupkg that silently pins consumers
            // to a dev sentinel — exactly the kind of quiet packaging lie this pack pipeline must
            // never produce.
            AssertProducedNupkgsCarryRealVersion(outputDir, Version!, appleVersion, SkipApple);
        });

    // Opens the produced SDK and Templates nupkgs (and, unless skipped, the Apple supplement) and
    // asserts that the version baked into the files that ship verbatim — Sdk/Sdk.props and the
    // template's template.json — is the real shipped version, never the 0.0.0-dev source sentinel.
    // Entries are matched by path suffix because NuGet packs the template content into more than
    // one folder (content/ and contentFiles/) and we assert every shipped copy.
    void AssertProducedNupkgsCarryRealVersion(AbsolutePath outputDir, string version, string appleVersion, bool skipApple)
    {
        // Contract-gate truth: the runtime derives its load-time contract epoch from this same
        // version (RuntimeContract.Version = major*1000 + minor). The dev sentinel maps to epoch 0,
        // which the handshake treats as always-compatible — so shipping a runtime whose version
        // parses to epoch 0 would silently disable the gate for EVERY consumer. A released version
        // must carry a real, non-zero epoch. (The same minor is the bounded NuGet range's fracture
        // boundary, so the load gate and the restore gate stay tied to one parse of one source.)
        if (BindingsGeneration.RuntimeVersionRange.Epoch(version) == 0)
            throw new System.InvalidOperationException(
                $"Pack version '{version}' parses to runtime-contract epoch 0 (the dev sentinel). A " +
                "released SwiftBindings.Runtime must carry a real, non-zero contract epoch or its " +
                "load-time handshake would bypass for every consumer.");

        var sdkNupkg = outputDir / $"SwiftBindings.Sdk.{version}.nupkg";
        var templatesNupkg = outputDir / $"SwiftBindings.Templates.{version}.nupkg";

        // SDK: Sdk.props must carry the real version in its single-sourced version elements and a
        // bounded Runtime range, with no dev sentinel left anywhere in the file.
        foreach (var (entryPath, sdkProps) in ReadNupkgEntriesBySuffix(sdkNupkg, "Sdk/Sdk.props"))
        {
            var propsDoc = XDocument.Parse(sdkProps);
            AssertElementValue(propsDoc, "_SwiftBindingSdkVersion", version, sdkNupkg, entryPath);
            AssertElementValue(propsDoc, "SwiftRuntimeVersion", version, sdkNupkg, entryPath);
            AssertElementValue(propsDoc, "SwiftRuntimePackageVersionRange",
                BindingsGeneration.RuntimeVersionRange.Build(version), sdkNupkg, entryPath);
            AssertElementValue(propsDoc, "SwiftAppleSupplementVersion", appleVersion, sdkNupkg, entryPath);
            if (sdkProps.Contains("0.0.0-dev"))
                throw new System.InvalidOperationException(
                    $"Packed Sdk.props ('{entryPath}') in '{sdkNupkg}' still contains the 0.0.0-dev sentinel.");
        }

        // Templates: template.json's sdkVersion defaultValue must be the real version, while its
        // `replaces` token stays the 0.0.0-dev sentinel (it matches the verbatim ProjectName.csproj
        // token the template engine swaps at `dotnet new` time).
        foreach (var (entryPath, templateJson) in
                 ReadNupkgEntriesBySuffix(templatesNupkg, ".template.config/template.json"))
        {
            var sdkVersionSymbol = System.Text.Json.Nodes.JsonNode.Parse(templateJson)!
                ["symbols"]!["sdkVersion"]!;
            var defaultValue = (string?)sdkVersionSymbol["defaultValue"];
            var replaces = (string?)sdkVersionSymbol["replaces"];
            if (defaultValue != version)
                throw new System.InvalidOperationException(
                    $"Packed template.json ('{entryPath}') in '{templatesNupkg}' has sdkVersion " +
                    $"defaultValue '{defaultValue}', expected '{version}'.");
            if (replaces != "0.0.0-dev")
                throw new System.InvalidOperationException(
                    $"Packed template.json ('{entryPath}') in '{templatesNupkg}' has sdkVersion " +
                    $"replaces '{replaces}', expected the 0.0.0-dev sentinel that matches the verbatim " +
                    "ProjectName.csproj token.");
        }

        // Apple supplement: its nuspec dependency on SwiftBindings.Runtime must be the floor the
        // VersionScope passes, never the dev sentinel.
        if (!skipApple)
        {
            var appleNupkg = outputDir / $"SwiftBindings.Apple.{appleVersion}.nupkg";
            foreach (var (entryPath, nuspec) in ReadNupkgEntriesBySuffix(appleNupkg, ".nuspec"))
                if (nuspec.Contains("0.0.0-dev"))
                    throw new System.InvalidOperationException(
                        $"Packed Apple supplement nuspec ('{entryPath}') in '{appleNupkg}' still " +
                        "contains the 0.0.0-dev sentinel.");
        }

        Log.Information("Version truth gate passed: produced nupkgs carry v{Version} (Apple v{AppleVersion}), no dev sentinel.",
            version, appleVersion);
    }

    // The source-controlled files that carry version information — the ones the pack's version
    // mechanism reaches through MSBuild properties (the four <PackageVersion> csprojs + the generator
    // csproj that bakes DefaultSwiftRuntimeVersion) or through gitignored staged copies (Sdk.props,
    // template.json), plus the two files that ship a verbatim 0.0.0-dev sentinel (Directory.Build.props'
    // default, ProjectName.csproj's template token). A pack must leave every one byte-unchanged.
    // SnapshotVersionFiles hard-fails on a missing entry, so a rename forces this list to be updated
    // rather than silently dropping a file from the guarantee.
    static readonly string[] VersionSourceFiles =
    {
        "Directory.Build.props",
        "src/Swift.Runtime/src/Swift.Runtime.csproj",
        "src/Swift.Bindings/src/Swift.Bindings.csproj",
        "src/Swift.Bindings.Sdk/Swift.Bindings.Sdk.csproj",
        "src/Swift.Bindings.Sdk/Sdk/Sdk.props",
        "src/Swift.Bindings.Templates/Swift.Bindings.Templates.csproj",
        "src/Swift.Bindings.Templates/content/swift-binding/.template.config/template.json",
        "src/Swift.Bindings.Templates/content/swift-binding/ProjectName.csproj",
        "src/Swift.Bindings.Apple/Swift.Bindings.Apple.csproj",
    };

    // Reads the current bytes of every version source file. A missing file is a hard error, not a
    // silent skip — the snapshot must cover the whole set for the post-pack byte check to be meaningful.
    System.Collections.Generic.Dictionary<string, byte[]> SnapshotVersionFiles()
    {
        var snapshot = new System.Collections.Generic.Dictionary<string, byte[]>();
        foreach (var rel in VersionSourceFiles)
        {
            var path = Path.Combine(RootDirectory, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                throw new System.InvalidOperationException(
                    $"Pack no-source-mutation gate: expected version source file '{path}' is missing. " +
                    "If it was intentionally moved or renamed, update VersionSourceFiles in Build.Pack.cs.");
            snapshot[rel] = File.ReadAllBytes(path);
        }
        return snapshot;
    }

    // Asserts every version source file is byte-identical to its pre-pack snapshot. A mismatch means
    // the pack rewrote a checked-in file — the F6 single-source contract is that versions reach
    // artifacts only via MSBuild properties and gitignored staged copies, never by mutating source.
    void AssertVersionFilesUnmutated(System.Collections.Generic.Dictionary<string, byte[]> snapshot)
    {
        foreach (var (rel, before) in snapshot)
        {
            var after = File.ReadAllBytes(Path.Combine(RootDirectory, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!before.SequenceEqual(after))
                throw new System.InvalidOperationException(
                    $"Pack mutated the source-controlled version file '{rel}'. Shipped versions must reach " +
                    "artifacts through MSBuild properties and the gitignored VersionScope staging copies, " +
                    "never by rewriting a checked-in file. Restore the no-source-mutation packaging path " +
                    "instead of stamping the version in place.");
        }
        Log.Information("No-source-mutation gate passed: {Count} version source files byte-unchanged by pack.",
            snapshot.Count);
    }

    static void AssertElementValue(XDocument doc, string name, string expected, AbsolutePath nupkg, string entryPath)
    {
        var element = doc.Descendants(name).FirstOrDefault()
            ?? throw new System.InvalidOperationException($"<{name}> not found in packed '{entryPath}' ('{nupkg}').");
        var actual = element.Value.Trim();
        if (actual != expected)
            throw new System.InvalidOperationException(
                $"Packed '{entryPath}' in '{nupkg}' has <{name}> = '{actual}', expected '{expected}'.");
    }

    // Returns every entry in the nupkg whose path ends with the given suffix, as (path, text)
    // pairs. Throws when the nupkg is missing or no entry matches — a packaging regression, not a
    // benign skip.
    static System.Collections.Generic.List<(string Path, string Text)> ReadNupkgEntriesBySuffix(
        AbsolutePath nupkg, string suffix)
    {
        if (!File.Exists(nupkg))
            throw new System.InvalidOperationException($"Expected produced nupkg not found: '{nupkg}'.");
        using var archive = System.IO.Compression.ZipFile.OpenRead(nupkg);
        var matches = new System.Collections.Generic.List<(string, string)>();
        foreach (var entry in archive.Entries)
        {
            if (!entry.FullName.EndsWith(suffix, System.StringComparison.Ordinal))
                continue;
            using var reader = new StreamReader(entry.Open());
            matches.Add((entry.FullName, reader.ReadToEnd()));
        }
        if (matches.Count == 0)
            throw new System.InvalidOperationException(
                $"nupkg '{nupkg}' has no entry ending with '{suffix}'.");
        return matches;
    }
}
