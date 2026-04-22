// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    [Parameter("Force rebuild even if cached")] readonly bool Force;
    [Parameter("Show library status without building")] readonly bool List;

    // ============================================================
    // Fetch target — fetches/builds library xcframeworks
    // ============================================================

    Target Fetch => _ => _
        .After(Clean, Test, ValidateAppleTypesManifest)
        .Executes(() => RunFetch());

    void RunFetch()
    {
        var manifest = ValidationManifest.Load(ManifestPath);
        var libraries = manifest.Libraries
            .Where(lib => Filter == null || lib.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase))
            .Where(lib => Tier == 0 || lib.Tier == Tier)
            .ToList();

        if (List)
        {
            ShowLibraryStatus(libraries);
            return;
        }

        int fetched = 0, cached = 0, failed = 0, skipped = 0;

        foreach (var lib in libraries)
        {
            var version = lib.Version ?? "manual";

            if (!Force && IsCached(lib.Name, version))
            {
                Log.Debug("{Name}: cached ({Version})", lib.Name, version);
                cached++;
                continue;
            }

            Log.Information("{Name} ({Mode}, {Version})", lib.Name, lib.Mode, version);
            (LibrariesDir / lib.Name).CreateDirectory();

            try
            {
                switch (lib.Mode)
                {
                    case "source":
                        ValidateRequiredFields(lib);
                        BuildFromSource(lib);
                        fetched++;
                        break;
                    case "binary":
                        ValidateRequiredFields(lib);
                        ResolveBinary(lib);
                        fetched++;
                        break;
                    case "manual":
                        CheckManual(lib);
                        skipped++;
                        break;
                    default:
                        throw new InvalidOperationException(
                            $"Unknown mode '{lib.Mode}' for library '{lib.Name}'");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Failed to fetch {Name}: {Message}", lib.Name, ex.Message);
                failed++;
            }
        }

        Log.Information("=== Summary ===");
        if (fetched > 0) Log.Information("Fetched: {Count}", fetched);
        if (cached > 0) Log.Debug("Cached: {Count}", cached);
        if (skipped > 0) Log.Debug("Skipped (manual): {Count}", skipped);
        if (failed > 0) Log.Error("Failed: {Count}", failed);

        Assert.True(failed == 0, $"{failed} library fetch(es) failed");
    }

    // ============================================================
    // Fetch helpers
    // ============================================================

    static void ValidateRequiredFields(ValidationLibrary lib)
    {
        if (string.IsNullOrEmpty(lib.Repository))
            throw new InvalidOperationException(
                $"Library '{lib.Name}' (mode: {lib.Mode}) is missing required 'repository' field");
        if (string.IsNullOrEmpty(lib.Version))
            throw new InvalidOperationException(
                $"Library '{lib.Name}' (mode: {lib.Mode}) is missing required 'version' field");
    }

    bool IsCached(string name, string version)
    {
        var versionFile = LibrariesDir / name / ".version";
        if (!File.Exists(versionFile)) return false;
        return File.ReadAllText(versionFile).Trim() == version;
    }

    void WriteCache(string name, string version)
    {
        (LibrariesDir / name).CreateDirectory();
        File.WriteAllText(LibrariesDir / name / ".version", version);
    }

    void VerifyRevision(string repository, string tag, string revision)
    {
        Log.Debug("Verifying tag {Tag}...", tag);

        var remoteSha = GetRemoteTagSha(repository, tag);

        if (string.IsNullOrEmpty(remoteSha) && !tag.StartsWith("v"))
            remoteSha = GetRemoteTagSha(repository, $"v{tag}");

        if (string.IsNullOrEmpty(remoteSha))
            throw new InvalidOperationException($"Tag '{tag}' not found in {repository}");

        if (remoteSha != revision)
            throw new InvalidOperationException(
                $"Tag '{tag}' resolves to {remoteSha}, expected {revision}");
    }

    string? GetRemoteTagSha(string repository, string tag)
    {
        try
        {
            var output = ProcessTasks.StartProcess(
                    "git", $"ls-remote {repository} refs/tags/{tag} refs/tags/{tag}^{{}}",
                    logOutput: false)
                .AssertWaitForExit()
                .AssertZeroExitCode()
                .Output.StdToText().Trim();

            if (string.IsNullOrEmpty(output)) return null;

            var lastLine = output.Split('\n').Last().Trim();
            return lastLine.Split('\t')[0];
        }
        catch { return null; }
    }

    void BuildFromSource(ValidationLibrary lib)
    {
        var libDir = LibrariesDir / lib.Name;
        var buildDir = libDir / ".build-workspace";
        var archivesDir = buildDir / "archives";
        var derivedData = buildDir / "DerivedData";

        if (!string.IsNullOrEmpty(lib.Revision) && !string.IsNullOrEmpty(lib.Repository))
            VerifyRevision(lib.Repository, lib.Version!, lib.Revision);

        buildDir.CreateOrCleanDirectory();
        foreach (var xcfw in libDir.GlobDirectories("*.xcframework"))
            xcfw.DeleteDirectory();

        Log.Information("  Cloning {Repo} @ {Version}", lib.Repository, lib.Version);
        ProcessTasks.StartProcess(
                "git", $"clone --depth 1 --branch {lib.Version} {lib.Repository} {buildDir / "source"}",
                logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode();

        foreach (var product in lib.Products)
        {
            if (string.IsNullOrEmpty(product.Scheme))
                throw new InvalidOperationException(
                    $"Product {product.Framework} missing 'scheme' (required for source mode)");

            BuildProductArchives(lib, product, buildDir, archivesDir, derivedData);
            InjectSwiftModuleInterfaces(product, archivesDir, derivedData);
            CreateProductXcframework(product, libDir, archivesDir);
        }

        buildDir.DeleteDirectory();
        WriteCache(lib.Name, lib.Version!);
    }

    void BuildProductArchives(
        ValidationLibrary lib, ValidationProduct product,
        AbsolutePath buildDir, AbsolutePath archivesDir, AbsolutePath derivedData)
    {
        var sourceDir = buildDir / "source";

        Log.Information("  Building {Framework} (scheme: {Scheme}) - device",
            product.Framework, product.Scheme);

        var deviceSettings = new ArchiveBuildSettings()
            .SetScheme(product.Scheme!)
            .SetDestination("generic/platform=iOS")
            .SetArchivePath(archivesDir / $"{product.Framework}-ios-arm64")
            .SetDerivedDataPath(derivedData / "device")
            .SetLibraryDistributionDefaults()
            .SetIosDeploymentTarget(lib.MinIOS)
            .AddBuildSetting("VALID_ARCHS", "$(ARCHS_STANDARD)")
            .SetQuiet()
            .SetWorkingDirectory(sourceDir);

        if (!string.IsNullOrEmpty(product.Project))
            deviceSettings.SetProject(product.Project);

        if (lib.BuildSettings != null)
            foreach (var (key, value) in lib.BuildSettings)
                deviceSettings.AddBuildSetting(key, value);

        XcodeBuild.ExecuteArchiveBuild(deviceSettings);

        Log.Information("  Building {Framework} (scheme: {Scheme}) - simulator",
            product.Framework, product.Scheme);

        var simSettings = new ArchiveBuildSettings()
            .SetScheme(product.Scheme!)
            .SetDestination("generic/platform=iOS Simulator")
            .SetArchivePath(archivesDir / $"{product.Framework}-ios-simulator")
            .SetDerivedDataPath(derivedData / "simulator")
            .SetLibraryDistributionDefaults()
            .SetIosDeploymentTarget(lib.MinIOS)
            .AddBuildSetting("VALID_ARCHS", "$(ARCHS_STANDARD)")
            .SetQuiet()
            .SetWorkingDirectory(sourceDir);

        if (!string.IsNullOrEmpty(product.Project))
            simSettings.SetProject(product.Project);

        if (lib.BuildSettings != null)
            foreach (var (key, value) in lib.BuildSettings)
                simSettings.AddBuildSetting(key, value);

        XcodeBuild.ExecuteArchiveBuild(simSettings);
    }

    void InjectSwiftModuleInterfaces(
        ValidationProduct product, AbsolutePath archivesDir, AbsolutePath derivedData)
    {
        var variants = new[]
        {
            (archivePath: archivesDir / $"{product.Framework}-ios-arm64.xcarchive",
             ddVariant: "device"),
            (archivePath: archivesDir / $"{product.Framework}-ios-simulator.xcarchive",
             ddVariant: "simulator")
        };

        foreach (var (archivePath, ddVariant) in variants)
        {
            var productsDir = archivePath / "Products";
            var frameworkPaths = productsDir.GlobDirectories($"**/{product.Framework}.framework");
            var fwPath = frameworkPaths.FirstOrDefault();
            if (fwPath == null) continue;

            var modulesDir = fwPath / "Modules" / $"{product.Framework}.swiftmodule";
            if (Directory.Exists(modulesDir)) continue;

            var ddSearch = (derivedData / ddVariant)
                .GlobDirectories($"**/ArchiveIntermediates/**/BuildProductsPath/**/{product.Framework}.swiftmodule");
            var swiftmod = ddSearch.FirstOrDefault();

            if (swiftmod != null)
            {
                Log.Debug("  Injecting Swift module interfaces for {Framework}", product.Framework);
                (fwPath / "Modules").CreateDirectory();
                swiftmod.Copy(fwPath / "Modules" / $"{product.Framework}.swiftmodule");
            }
        }
    }

    void CreateProductXcframework(
        ValidationProduct product, AbsolutePath libDir, AbsolutePath archivesDir)
    {
        var deviceArchive = archivesDir / $"{product.Framework}-ios-arm64.xcarchive" / "Products";
        var simArchive = archivesDir / $"{product.Framework}-ios-simulator.xcarchive" / "Products";

        var deviceFw = deviceArchive
            .GlobDirectories($"**/{product.Framework}.framework")
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"{product.Framework}.framework not found in device archive");

        var simulatorFw = simArchive
            .GlobDirectories($"**/{product.Framework}.framework")
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"{product.Framework}.framework not found in simulator archive");

        Log.Information("  Creating {Framework}.xcframework", product.Framework);

        XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings()
            .AddFrameworkPath(deviceFw)
            .AddFrameworkPath(simulatorFw)
            .SetOutputPath(libDir / $"{product.Framework}.xcframework"));

        Log.Information("  {Framework}.xcframework built", product.Framework);
    }

    void ResolveBinary(ValidationLibrary lib)
    {
        var libDir = LibrariesDir / lib.Name;
        var buildDir = libDir / ".build-workspace";

        if (!string.IsNullOrEmpty(lib.Revision) && !string.IsNullOrEmpty(lib.Repository))
            VerifyRevision(lib.Repository, lib.Version!, lib.Revision);

        buildDir.CreateOrCleanDirectory();
        foreach (var xcfw in libDir.GlobDirectories("*.xcframework"))
            xcfw.DeleteDirectory();

        (buildDir / "Sources").CreateDirectory();

        var majorVersion = lib.MinIOS.Split('.')[0];
        var spmIosVer = $".v{majorVersion}";

        var packageSwift = $"""
            // swift-tools-version:5.9
            import PackageDescription
            let package = Package(
                name: "Resolver",
                platforms: [.iOS({spmIosVer})],
                dependencies: [
                    .package(url: "{lib.Repository}", exact: "{lib.Version}")
                ],
                targets: [.target(name: "Resolver", path: "Sources")]
            )
            """;
        File.WriteAllText(buildDir / "Package.swift", packageSwift);
        File.WriteAllText(buildDir / "Sources" / "Resolver.swift", "// placeholder");

        Log.Information("  Resolving SPM dependencies");
        ProcessTasks.StartProcess(
                "swift", "package resolve",
                workingDirectory: buildDir,
                logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode();

        var artifactsDir = buildDir / ".build" / "artifacts";

        foreach (var product in lib.Products)
        {
            var xcframeworkName = $"{product.Framework}.xcframework";

            var found = artifactsDir
                .GlobDirectories($"**/{xcframeworkName}")
                .Where(d => !d.ToString().Contains("__MACOSX"))
                .FirstOrDefault();

            if (found == null)
            {
                var available = artifactsDir.GlobDirectories("**/*.xcframework")
                    .Select(d => d.Name);
                throw new InvalidOperationException(
                    $"{xcframeworkName} not found in SPM artifacts. Available: {string.Join(", ", available)}");
            }

            found.Copy(libDir / xcframeworkName);
            Log.Information("  {Framework}.xcframework resolved", product.Framework);
        }

        buildDir.DeleteDirectory();
        WriteCache(lib.Name, lib.Version!);
    }

    void CheckManual(ValidationLibrary lib)
    {
        var libDir = LibrariesDir / lib.Name;
        var note = lib.Note ?? $"Place xcframework in .libraries/{lib.Name}/";
        bool allPresent = true;

        foreach (var product in lib.Products)
        {
            var xcfwPath = libDir / $"{product.Framework}.xcframework";
            if (Directory.Exists(xcfwPath))
            {
                Log.Information("  {Framework}: present", product.Framework);
            }
            else
            {
                allPresent = false;
                Log.Warning("  {Framework}: missing", product.Framework);
                Log.Debug("  {Note}", note);
            }
        }

        if (allPresent)
            WriteCache(lib.Name, "manual");
    }

    void ShowLibraryStatus(IReadOnlyList<ValidationLibrary> libraries)
    {
        int publicCount = 0, manualCount = 0;

        foreach (var lib in libraries)
        {
            var version = lib.Version ?? "manual";

            if (lib.Mode == "manual")
                manualCount++;
            else
                publicCount++;

            if (IsCached(lib.Name, version))
            {
                Log.Information("  {Name}: cached ({Version})", lib.Name, version);
            }
            else if (lib.Mode == "manual")
            {
                var libDir = LibrariesDir / lib.Name;
                bool hasXcfw = Directory.Exists(libDir)
                    && libDir.GlobDirectories("*.xcframework").Any();
                if (hasXcfw)
                    Log.Information("  {Name}: present (manual)", lib.Name);
                else
                    Log.Warning("  {Name}: missing (manual)", lib.Name);
            }
            else
            {
                Log.Information("  {Name}: not fetched ({Mode}, {Version})", lib.Name, lib.Mode, version);
            }
        }

        Log.Information("Public: {Public} libraries, Manual: {Manual} libraries",
            publicCount, manualCount);
    }
}
