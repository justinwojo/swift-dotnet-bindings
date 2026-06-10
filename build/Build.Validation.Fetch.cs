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

            if (!Force && IsCached(lib.Name, version)
                && !BehaviorTierArtifactMissing(lib)
                && !PlatformSetMissing(lib))
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

    // Behavior-tier libraries need an extra macOS xcframework that older `.version`
    // caches don't account for. If the flag is set but the slice is missing on
    // disk, treat the cache as a miss so BuildFromSource runs and produces it.
    bool BehaviorTierArtifactMissing(ValidationLibrary lib)
    {
        if (!lib.BehaviorTier || string.IsNullOrEmpty(lib.BehaviorTierMacOSScheme))
            return false;
        var libDir = LibrariesDir / lib.Name;
        return lib.Products.Any(p => !Directory.Exists(
            libDir / ".behavior-tier" / $"{p.Framework}-macos.xcframework"));
    }

    // Source-mode libs that declare `platforms: [..., "tvos", "macos", ...]` need
    // their xcframework to contain all the corresponding slices. A pre-existing
    // cache from a prior single-platform fetch reports the version match but
    // ships a stale iOS-only xcframework — `nuke pack-gate` would then fail on
    // missing tvos/macos RID coverage, with no signal pointing back at the fetch
    // cache. Treat the cache as a miss when the on-disk slice set on the primary
    // xcframework doesn't cover every declared platform.
    bool PlatformSetMissing(ValidationLibrary lib)
    {
        if (lib.Mode != "source") return false;
        var platforms = lib.Platforms ?? ["ios"];
        if (platforms.Count <= 1) return false;

        var libDir = LibrariesDir / lib.Name;
        foreach (var product in lib.Products)
        {
            var xcfw = libDir / $"{product.Framework}.xcframework";
            if (!Directory.Exists(xcfw)) return true;

            var slicePrefixes = Directory.EnumerateDirectories(xcfw)
                .Select(d => Path.GetFileName(d) ?? "")
                .ToList();

            foreach (var platform in platforms)
            {
                // Match by slice id prefix — covers both the `tvos-arm64` device id
                // and `tvos-arm64_x86_64-simulator` simulator id under one check.
                // Catalyst slices live under `ios-` prefix with `-maccatalyst` suffix
                // (not used in source mode today, but kept consistent).
                bool present = platform switch
                {
                    "ios" => slicePrefixes.Any(s => s.StartsWith("ios-", StringComparison.Ordinal)
                                               && !s.Contains("maccatalyst", StringComparison.Ordinal)),
                    "tvos" => slicePrefixes.Any(s => s.StartsWith("tvos-", StringComparison.Ordinal)),
                    "macos" => slicePrefixes.Any(s => s.StartsWith("macos-", StringComparison.Ordinal)),
                    "maccatalyst" => slicePrefixes.Any(s => s.Contains("maccatalyst", StringComparison.Ordinal)),
                    _ => true,
                };
                if (!present) return true;
            }
        }
        return false;
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

        // Honor `platforms: [...]` for source-mode libs. Default is iOS-only when the
        // field is absent so single-platform fetches stay unchanged. Each platform
        // contributes one or more framework slices into the produced xcframework —
        // device + simulator on iOS/tvOS, single fat arm64+x86_64 slice on macOS.
        var platforms = lib.Platforms ?? ["ios"];
        var unsupported = platforms
            .Where(p => p is not ("ios" or "tvos" or "macos"))
            .ToList();
        if (unsupported.Count > 0)
            throw new InvalidOperationException(
                $"Library '{lib.Name}' declares unsupported source-mode platform(s): " +
                $"[{string.Join(", ", unsupported)}]. Source-mode fetch supports ios, tvos, macos. " +
                "Catalyst is intentionally omitted — no source-mode lib in the manifest needs it today; " +
                "apple-framework mode libs flow through ExpandTargets and don't hit this path.");

        foreach (var product in lib.Products)
        {
            if (string.IsNullOrEmpty(product.Scheme))
                throw new InvalidOperationException(
                    $"Product {product.Framework} missing 'scheme' (required for source mode)");

            var sliceFrameworkPaths = new List<AbsolutePath>();

            if (platforms.Contains("ios"))
            {
                BuildProductArchives(lib, product, buildDir, archivesDir, derivedData);
                InjectSwiftModuleInterfaces(product, archivesDir, derivedData);
                sliceFrameworkPaths.Add(ResolveFrameworkInArchive(
                    archivesDir / $"{product.Framework}-ios-arm64.xcarchive", product.Framework, "iOS device"));
                sliceFrameworkPaths.Add(ResolveFrameworkInArchive(
                    archivesDir / $"{product.Framework}-ios-simulator.xcarchive", product.Framework, "iOS simulator"));
            }

            if (platforms.Contains("tvos"))
            {
                BuildProductTvosArchives(lib, product, buildDir, archivesDir, derivedData);
                InjectTvosSwiftModuleInterfaces(product, archivesDir, derivedData);
                sliceFrameworkPaths.Add(ResolveFrameworkInArchive(
                    archivesDir / $"{product.Framework}-tvos-arm64.xcarchive", product.Framework, "tvOS device"));
                sliceFrameworkPaths.Add(ResolveFrameworkInArchive(
                    archivesDir / $"{product.Framework}-tvos-simulator.xcarchive", product.Framework, "tvOS simulator"));
            }

            if (platforms.Contains("macos"))
            {
                BuildProductFatMacosArchive(lib, product, buildDir, archivesDir, derivedData);
                InjectFatMacosSwiftModuleInterface(product, archivesDir, derivedData);
                sliceFrameworkPaths.Add(ResolveFrameworkInArchive(
                    archivesDir / $"{product.Framework}-macos.xcarchive", product.Framework, "macOS"));
            }

            CreateProductXcframework(product, libDir, sliceFrameworkPaths);

            // Behavior tier opt-in: build an additional single-slice macOS xcframework
            // alongside the iOS one. The host-run consumer in `nuke validate`'s
            // behavior tier loads this slice on macOS to invoke real Swift through
            // P/Invoke. macOS scheme name comes from the manifest; we cannot reuse
            // the iOS scheme (some libraries expose a separate macOS scheme distinct
            // from the iOS scheme). The result lives under
            // `<libDir>/.behavior-tier/` (see CreateProductMacOSXcframework) so
            // Validate's sibling-xcframework auto-discovery cannot pick it up and
            // try to resolve a macOS slice as an iOS framework dependency.
            //
            // Distinct from the `platforms: [..."macos"]` branch above — behavior-tier
            // pins ARCHS=arm64 (host-runtime only on Apple Silicon CI) and writes to
            // a sibling .behavior-tier/ tree, where the main multi-platform macOS
            // slice is fat arm64+x86_64 and lives in the primary xcframework.
            if (lib.BehaviorTier && !string.IsNullOrEmpty(lib.BehaviorTierMacOSScheme))
            {
                BuildProductMacOSArchive(lib, product, buildDir, archivesDir, derivedData);
                InjectMacOSSwiftModuleInterface(product, archivesDir, derivedData);
                CreateProductMacOSXcframework(product, libDir, archivesDir);
            }
        }

        buildDir.DeleteDirectory();
        WriteCache(lib.Name, lib.Version!);
    }

    static AbsolutePath ResolveFrameworkInArchive(AbsolutePath archivePath, string framework, string label)
    {
        var productsDir = archivePath / "Products";
        var fwPath = productsDir
            .GlobDirectories($"**/{framework}.framework")
            .FirstOrDefault();
        if (fwPath == null)
            throw new InvalidOperationException(
                $"{framework}.framework not found in {label} archive at {archivePath}");
        return fwPath;
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

    void BuildProductMacOSArchive(
        ValidationLibrary lib, ValidationProduct product,
        AbsolutePath buildDir, AbsolutePath archivesDir, AbsolutePath derivedData)
    {
        var sourceDir = buildDir / "source";
        var scheme = lib.BehaviorTierMacOSScheme!;

        Log.Information("  Building {Framework} (scheme: {Scheme}) - macOS [behavior tier]",
            product.Framework, scheme);

        var macSettings = new ArchiveBuildSettings()
            .SetScheme(scheme)
            // `name=Any Mac` disambiguates from the Mac Catalyst variant of the same
            // platform — without it xcodebuild emits "Using the first of multiple
            // matching destinations" on hosts where Catalyst is installed.
            .SetDestination("generic/platform=macOS,name=Any Mac")
            .SetArchivePath(archivesDir / $"{product.Framework}-macos-arm64")
            .SetDerivedDataPath(derivedData / "macos")
            .SetLibraryDistributionDefaults()
            .AddBuildSetting("MACOSX_DEPLOYMENT_TARGET", lib.MinMacOS)
            // Pin to arm64 so the slice is produced for the host (CI is Apple Silicon).
            // x86_64 macs aren't a runtime target for the behavior tier.
            .AddBuildSetting("ARCHS", "arm64")
            .AddBuildSetting("ONLY_ACTIVE_ARCH", "NO")
            .SetQuiet()
            .SetWorkingDirectory(sourceDir);

        if (!string.IsNullOrEmpty(product.Project))
            macSettings.SetProject(product.Project);

        if (lib.BuildSettings != null)
            foreach (var (key, value) in lib.BuildSettings)
                macSettings.AddBuildSetting(key, value);

        XcodeBuild.ExecuteArchiveBuild(macSettings);
    }

    void InjectMacOSSwiftModuleInterface(
        ValidationProduct product, AbsolutePath archivesDir, AbsolutePath derivedData)
    {
        // Mirror of InjectSwiftModuleInterfaces but only for the macOS arm64 slice.
        // Some Xcode versions emit the .swiftmodule contents into DerivedData rather
        // than the archived framework; copy them across so the generator's
        // XCFrameworkResolver finds the swiftinterface where it expects it.
        var archivePath = archivesDir / $"{product.Framework}-macos-arm64.xcarchive";
        var productsDir = archivePath / "Products";
        var fwPath = productsDir.GlobDirectories($"**/{product.Framework}.framework").FirstOrDefault();
        if (fwPath == null) return;

        var modulesDir = fwPath / "Modules" / $"{product.Framework}.swiftmodule";
        if (Directory.Exists(modulesDir)) return;

        var swiftmod = (derivedData / "macos")
            .GlobDirectories($"**/ArchiveIntermediates/**/BuildProductsPath/**/{product.Framework}.swiftmodule")
            .FirstOrDefault();

        if (swiftmod != null)
        {
            Log.Debug("  Injecting Swift module interfaces for {Framework} (macOS)", product.Framework);
            (fwPath / "Modules").CreateDirectory();
            swiftmod.Copy(fwPath / "Modules" / $"{product.Framework}.swiftmodule");
        }
    }

    void CreateProductMacOSXcframework(
        ValidationProduct product, AbsolutePath libDir, AbsolutePath archivesDir)
    {
        // Single-slice macOS xcframework consumed exclusively by the behavior tier.
        // Lives under `<libDir>/.behavior-tier/` so Validate's sibling-xcframework
        // discovery (which globs `<libDir>/*.xcframework` non-recursively) cannot
        // pick it up and try to resolve it as an iOS dependency.
        var macArchive = archivesDir / $"{product.Framework}-macos-arm64.xcarchive" / "Products";
        var macFw = macArchive
            .GlobDirectories($"**/{product.Framework}.framework")
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"{product.Framework}.framework not found in macOS archive");

        Log.Information("  Creating {Framework}-macos.xcframework [behavior tier]", product.Framework);

        var behaviorTierDir = libDir / ".behavior-tier";
        behaviorTierDir.CreateDirectory();
        var xcfwPath = behaviorTierDir / $"{product.Framework}-macos.xcframework";
        if (Directory.Exists(xcfwPath)) ((AbsolutePath)xcfwPath).DeleteDirectory();

        XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings()
            .AddFrameworkPath(macFw)
            .SetOutputPath(xcfwPath));

        Log.Information("  {Framework}-macos.xcframework built", product.Framework);
    }

    void CreateProductXcframework(
        ValidationProduct product, AbsolutePath libDir,
        IReadOnlyList<AbsolutePath> sliceFrameworkPaths)
    {
        if (sliceFrameworkPaths.Count == 0)
            throw new InvalidOperationException(
                $"No framework slices collected for {product.Framework}. " +
                "BuildFromSource produced no platform slices to combine.");

        Log.Information("  Creating {Framework}.xcframework ({SliceCount} slice(s))",
            product.Framework, sliceFrameworkPaths.Count);

        var settings = new CreateXcframeworkSettings()
            .SetOutputPath(libDir / $"{product.Framework}.xcframework");
        foreach (var path in sliceFrameworkPaths)
            settings.AddFrameworkPath(path);

        XcodeBuild.ExecuteCreateXcframework(settings);

        Log.Information("  {Framework}.xcframework built", product.Framework);
    }

    // ============================================================
    // Multi-platform source-mode build helpers (tvOS + fat macOS).
    // Used when `validation-libraries.json` declares additional
    // platforms beyond iOS. Distinct from the behavior-tier macOS
    // path (which pins ARCHS=arm64 and writes to .behavior-tier/).
    // ============================================================

    void BuildProductTvosArchives(
        ValidationLibrary lib, ValidationProduct product,
        AbsolutePath buildDir, AbsolutePath archivesDir, AbsolutePath derivedData)
    {
        var sourceDir = buildDir / "source";

        Log.Information("  Building {Framework} (scheme: {Scheme}) - tvOS device",
            product.Framework, product.Scheme);

        var deviceSettings = new ArchiveBuildSettings()
            .SetScheme(product.Scheme!)
            .SetDestination("generic/platform=tvOS")
            .SetArchivePath(archivesDir / $"{product.Framework}-tvos-arm64")
            .SetDerivedDataPath(derivedData / "tvos-device")
            .SetLibraryDistributionDefaults()
            .SetTvosDeploymentTarget(lib.MinTvOS)
            .AddBuildSetting("VALID_ARCHS", "$(ARCHS_STANDARD)")
            .SetQuiet()
            .SetWorkingDirectory(sourceDir);

        if (!string.IsNullOrEmpty(product.Project))
            deviceSettings.SetProject(product.Project);

        if (lib.BuildSettings != null)
            foreach (var (key, value) in lib.BuildSettings)
                deviceSettings.AddBuildSetting(key, value);

        XcodeBuild.ExecuteArchiveBuild(deviceSettings);

        Log.Information("  Building {Framework} (scheme: {Scheme}) - tvOS simulator",
            product.Framework, product.Scheme);

        var simSettings = new ArchiveBuildSettings()
            .SetScheme(product.Scheme!)
            .SetDestination("generic/platform=tvOS Simulator")
            .SetArchivePath(archivesDir / $"{product.Framework}-tvos-simulator")
            .SetDerivedDataPath(derivedData / "tvos-simulator")
            .SetLibraryDistributionDefaults()
            .SetTvosDeploymentTarget(lib.MinTvOS)
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

    void InjectTvosSwiftModuleInterfaces(
        ValidationProduct product, AbsolutePath archivesDir, AbsolutePath derivedData)
    {
        var variants = new[]
        {
            (archivePath: archivesDir / $"{product.Framework}-tvos-arm64.xcarchive",
             ddVariant: "tvos-device"),
            (archivePath: archivesDir / $"{product.Framework}-tvos-simulator.xcarchive",
             ddVariant: "tvos-simulator")
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
                Log.Debug("  Injecting Swift module interfaces for {Framework} (tvOS {Variant})",
                    product.Framework, ddVariant);
                (fwPath / "Modules").CreateDirectory();
                swiftmod.Copy(fwPath / "Modules" / $"{product.Framework}.swiftmodule");
            }
        }
    }

    void BuildProductFatMacosArchive(
        ValidationLibrary lib, ValidationProduct product,
        AbsolutePath buildDir, AbsolutePath archivesDir, AbsolutePath derivedData)
    {
        var sourceDir = buildDir / "source";

        Log.Information("  Building {Framework} (scheme: {Scheme}) - macOS (fat arm64+x86_64)",
            product.Framework, product.Scheme);

        // Fat arm64+x86_64 slice — distinct from the behavior-tier path which pins
        // arm64. NuGet expects the upstream-style `macos-arm64_x86_64` slice id so
        // pack-time slicing can route the same archive to osx-arm64/osx-x64 RIDs.
        //
        // ARCHS is pinned explicitly (not via VALID_ARCHS or the project's
        // ARCHS_STANDARD default) so the produced slice is guaranteed fat across
        // host machines and Xcode versions — `VALID_ARCHS` is a filter applied to
        // the project-defined `ARCHS`, and an Apple Silicon host with a project
        // that sets `ARCHS=$(ARCHS_STANDARD_64_BIT)` or doesn't include x86_64 can
        // still produce a single-arch slice. `ONLY_ACTIVE_ARCH=NO` defends against
        // a future caller invoking us with Debug-like settings.
        // The destination is disambiguated to `name=Any Mac` so xcodebuild doesn't
        // emit the "Using the first of multiple matching destinations" warning on
        // hosts that also have the Mac Catalyst variant installed.
        var macSettings = new ArchiveBuildSettings()
            .SetScheme(product.Scheme!)
            .SetDestination("generic/platform=macOS,name=Any Mac")
            .SetArchivePath(archivesDir / $"{product.Framework}-macos")
            .SetDerivedDataPath(derivedData / "macos-fat")
            .SetLibraryDistributionDefaults()
            .SetMacosDeploymentTarget(lib.MinMacOS)
            .AddBuildSetting("ARCHS", "arm64 x86_64")
            .AddBuildSetting("ONLY_ACTIVE_ARCH", "NO")
            .SetQuiet()
            .SetWorkingDirectory(sourceDir);

        if (!string.IsNullOrEmpty(product.Project))
            macSettings.SetProject(product.Project);

        if (lib.BuildSettings != null)
            foreach (var (key, value) in lib.BuildSettings)
                macSettings.AddBuildSetting(key, value);

        XcodeBuild.ExecuteArchiveBuild(macSettings);
    }

    void InjectFatMacosSwiftModuleInterface(
        ValidationProduct product, AbsolutePath archivesDir, AbsolutePath derivedData)
    {
        // Mirror of InjectSwiftModuleInterfaces but for the fat macOS slice produced
        // by BuildProductFatMacosArchive.
        var archivePath = archivesDir / $"{product.Framework}-macos.xcarchive";
        var productsDir = archivePath / "Products";
        var fwPath = productsDir.GlobDirectories($"**/{product.Framework}.framework").FirstOrDefault();
        if (fwPath == null) return;

        var modulesDir = fwPath / "Modules" / $"{product.Framework}.swiftmodule";
        if (Directory.Exists(modulesDir)) return;

        var swiftmod = (derivedData / "macos-fat")
            .GlobDirectories($"**/ArchiveIntermediates/**/BuildProductsPath/**/{product.Framework}.swiftmodule")
            .FirstOrDefault();

        if (swiftmod != null)
        {
            Log.Debug("  Injecting Swift module interfaces for {Framework} (macOS fat)", product.Framework);
            (fwPath / "Modules").CreateDirectory();
            swiftmod.Copy(fwPath / "Modules" / $"{product.Framework}.swiftmodule");
        }
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
