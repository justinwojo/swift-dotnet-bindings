// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BehaviorTier.cs — runtime regression gate for `nuke validate`.
//
// `nuke validate`'s standard tier proves bindings *compile*. The behavior tier
// proves they *run*: pack Runtime + SDK + Apple at a throwaway version,
// scaffold a fresh net10.0-macos console app, instantiate one type from each
// chosen library, call one Swift function across the P/Invoke boundary, and
// assert the round-trip return value.
//
// Two fixtures today:
//   - Foundation (always): exercises Swift.Foundation.Data via the
//     SwiftBindings.Apple supplement. No JSON opt-in — Foundation isn't a
//     validation library, it ships in the supplement.
//   - Alamofire (opt-in via `behaviorTier: true` + `behaviorTierMacOSScheme`
//     in validation-libraries.json): scaffolds a SwiftBindings.Sdk bindings
//     library against `.libraries/Alamofire/.behavior-tier/Alamofire-macos.xcframework`
//     (built by the extended fetch in Build.Validation.Fetch.cs; the
//     `.behavior-tier/` subdirectory hides it from Validate's sibling
//     xcframework auto-discovery), then runs a consumer app that round-trips
//     an HTTPMethod through Swift.
//
// macOS-only on purpose: we run binaries on the host instead of paying for
// a sim launch. The unique value is "the pipeline glues together at runtime,"
// which is platform-agnostic; iOS-sim/device runtime coverage already lives
// in `nuke binding-tests --sim --device`. PackGate established this pattern.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    const string BehaviorTierVersion = "0.0.0-behaviortier";
    // Apple supplement version must lead with an integer major (the generator
    // parses leading digits via ParseAppleVersionMajor and rejects '0'). Mirror
    // the PackGate pattern: pin to the live Apple train; the suffix keeps the
    // scratch nupkg from colliding with a shipped one.
    const string BehaviorTierAppleVersion = "26.2.0-behaviortier";
    // Single-TFM projects: the version suffix (e.g. -macos26.2) breaks the
    // SDK's `_SwiftBindingPlatform` detection in single-TFM mode. The SDK
    // template uses the unsuffixed form for the same reason; mirror it here.
    const string BehaviorTierConsumerTfm = "net10.0-macos";

    AbsolutePath BehaviorTierScratch => RootDirectory / "artifacts" / "behavior-tier";

    // .After(RegenerateStoreKitSnapshot) is a pure ordering edge: that target and
    // BehaviorTier are otherwise co-equal final sinks (RegenerateAppleSnapshot is already
    // non-sink because RegenerateStoreKitSnapshot depends on it), and Nuke --strict
    // requires a total peel order.
    Target BehaviorTier => _ => _
        .DependsOn(Compile)
        .After(Validate, PackGate, RegenerateStoreKitSnapshot)
        .Executes(() =>
        {
            var scratch = BehaviorTierScratch;
            if (Directory.Exists(scratch)) scratch.DeleteDirectory();
            scratch.CreateDirectory();
            var nupkgDir = scratch / "packages";
            nupkgDir.CreateDirectory();

            Log.Information("=== BehaviorTier: packing fixtures at {Version} ===", BehaviorTierVersion);

            using var scope = new VersionScope(BehaviorTierVersion, RootDirectory, BehaviorTierAppleVersion);

            // 1. Publish generator into the SDK's tools/ directory so the SDK's
            //    tools/**/* pack glob picks it up. Mirrors PackGate step 1.
            Log.Information("  [1/4] Publishing generator");
            DotNetPublish(s => s
                .SetProject(SourceDir / "Swift.Bindings" / "src" / "Swift.Bindings.csproj")
                .SetConfiguration("Release")
                .SetOutput(SourceDir / "Swift.Bindings.Sdk" / "tools" / DotNetTfm / "any")
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            // 2. Pack Runtime + SDK + Apple at the throwaway version.
            Log.Information("  [2/4] Packing Runtime + Sdk + Apple");
            foreach (var csproj in new[]
            {
                SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj",
                SourceDir / "Swift.Bindings.Sdk" / "Swift.Bindings.Sdk.csproj",
                SourceDir / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj",
            })
            {
                DotNetPack(s => s
                    .SetProject(csproj)
                    .SetConfiguration("Release")
                    .SetOutputDirectory(nupkgDir)
                    .EnableNoLogo()
                    .SetVerbosity(DotNetVerbosity.quiet));
            }

            // 3. Clear NuGet cache for the throwaway-version nupkgs so a stale
            //    entry from a prior run doesn't shadow the freshly-packed copy.
            Log.Information("  [3/4] Clearing NuGet cache");
            ProcessTasks.StartProcess("dotnet", "nuget locals http-cache --clear", logOutput: false)
                .AssertWaitForExit();
            var nugetCacheDir = (AbsolutePath)(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages"));
            foreach (var (pkg, ver) in new[]
            {
                ("swiftbindings.runtime", BehaviorTierVersion),
                ("swiftbindings.sdk", BehaviorTierVersion),
                ("swiftbindings.apple", BehaviorTierAppleVersion),
            })
            {
                var pkgDir = nugetCacheDir / pkg / ver;
                if (Directory.Exists(pkgDir)) pkgDir.DeleteDirectory();
            }

            // 4. Run fixtures.
            Log.Information("  [4/4] Running fixtures");
            var ran = 0;
            var failures = new List<string>();

            // Foundation always runs — it's the canonical "supplement works" check.
            try
            {
                RunFoundationBehaviorFixture(scratch, nupkgDir);
                ran++;
            }
            catch (Exception ex)
            {
                failures.Add($"Foundation: {ex.Message}");
            }

            // Library fixtures — opt-in via behaviorTier flag in the manifest.
            // Filtered by --filter so a focused dev loop (`nuke validate --filter Alamofire`)
            // exercises only the matching fixture. The flag declares eligibility, the
            // C# switch below wires the assertion. A flagged library with no wired
            // fixture is a hard failure (see default arm) so claimed runtime coverage
            // can't silently regress to none.
            var manifest = ValidationManifest.Load(ManifestPath);
            foreach (var lib in manifest.Libraries)
            {
                if (!lib.BehaviorTier) continue;
                if (Filter != null && !lib.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)) continue;
                // Mirror Validate/Fetch tier-scoping: a `--tier 2` run skips tier-1
                // libraries everywhere else, so the behavior tier must skip them too —
                // otherwise BehaviorTier tries to run a fixture whose xcframework was
                // never fetched.
                if (Tier > 0 && lib.Tier != Tier) continue;

                try
                {
                    switch (lib.Name)
                    {
                        case "Alamofire":
                            RunAlamofireBehaviorFixture(scratch, nupkgDir, lib);
                            ran++;
                            break;
                        default:
                            // `behaviorTier: true` declares "must run". A flagged library
                            // with no fixture wired must fail loudly — silently skipping
                            // would let runtime coverage we claim to have regress to none.
                            // The fix is to wire a fixture in this switch and a
                            // `Run<Lib>BehaviorFixture` method below.
                            failures.Add(
                                $"{lib.Name}: behaviorTier=true but no fixture wired in " +
                                "Build.BehaviorTier.cs — add a case to the switch.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{lib.Name}: {ex.Message}");
                }
            }

            if (failures.Count > 0)
            {
                Log.Error("BehaviorTier FAILED — {Count} fixture(s) failed:", failures.Count);
                foreach (var f in failures) Log.Error("  {Detail}", f);
                Assert.Fail($"BehaviorTier: {failures.Count} fixture(s) failed");
            }

            Log.Information("BehaviorTier OK — {Count} fixture(s) round-tripped through Swift", ran);
        });

    void RunFoundationBehaviorFixture(AbsolutePath scratch, AbsolutePath nupkgDir)
    {
        Log.Information("  Foundation: scaffolding consumer");
        var fixtureDir = scratch / "foundation";
        fixtureDir.CreateDirectory();

        WriteBehaviorTierNuGetConfig(fixtureDir, nupkgDir);

        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{BehaviorTierConsumerTfm}</TargetFramework>
                <RuntimeIdentifier>osx-arm64</RuntimeIdentifier>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>
                <ApplicationId>com.swiftbindings.behaviortier.foundation</ApplicationId>
                <NoWarn>$(NoWarn);CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="SwiftBindings.Runtime" Version="{BehaviorTierVersion}" />
                <PackageReference Include="SwiftBindings.Apple" Version="{BehaviorTierAppleVersion}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(fixtureDir / "FoundationFixture.csproj", csproj);

        // The fixture exercises a real Swift round-trip: byte[] → Swift.Foundation.Data
        // (PInvoke_InitWithBytes) → Data.Count (PInvoke_GetCount). A managed-only
        // construction would not prove the supplement's P/Invokes are linkable on the
        // host; reading Count after construction does.
        var program = """
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            using Swift.Foundation;

            var data = Data.FromByteArray(new byte[] { 1, 2, 3 });
            Console.WriteLine($"FOUNDATION_COUNT={data.Count}");
            """;
        File.WriteAllText(fixtureDir / "Program.cs", program);

        Log.Information("  Foundation: building");
        DotNetBuild(s => s
            .SetProjectFile(fixtureDir / "FoundationFixture.csproj")
            .SetConfiguration("Release")
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        var appExe = fixtureDir / "bin" / "Release" / BehaviorTierConsumerTfm / "osx-arm64" /
            "FoundationFixture.app" / "Contents" / "MacOS" / "FoundationFixture";
        if (!File.Exists(appExe))
            throw new Exception($"consumer binary not produced at {appExe}");

        Log.Information("  Foundation: launching");
        var (stdout, stderr, exit) = LaunchBehaviorTierConsumer(appExe, fixtureDir);
        if (exit != 0)
            throw new Exception($"consumer exited with code {exit}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        if (!stdout.Contains("FOUNDATION_COUNT=3", StringComparison.Ordinal))
            throw new Exception(
                $"expected 'FOUNDATION_COUNT=3' in stdout but got:\n{stdout}\nstderr:\n{stderr}");

        Log.Information("  Foundation OK — Data.Count round-tripped to managed code");
    }

    void RunAlamofireBehaviorFixture(AbsolutePath scratch, AbsolutePath nupkgDir, ValidationLibrary lib)
    {
        Log.Information("  Alamofire: scaffolding consumer");

        // The macOS xcframework is produced by Build.Validation.Fetch.cs's extended
        // BuildFromSource path when behaviorTier=true. If it isn't on disk, fetch
        // hasn't been run with the flag — surface the actionable hint rather than
        // silently no-op.
        var product = lib.Products.FirstOrDefault()
            ?? throw new Exception("Alamofire has no products in manifest");
        var xcfw = LibrariesDir / lib.Name / ".behavior-tier" / $"{product.Framework}-macos.xcframework";
        if (!Directory.Exists(xcfw))
            throw new Exception(
                $"{xcfw} not found — run `nuke fetch --filter Alamofire --force` to build the macOS slice");

        var fixtureDir = scratch / "alamofire";
        var bindingsDir = fixtureDir / "bindings";
        var appDir = fixtureDir / "app";
        fixtureDir.CreateDirectory();
        bindingsDir.CreateDirectory();
        appDir.CreateDirectory();

        WriteBehaviorTierNuGetConfig(fixtureDir, nupkgDir);

        // Bindings library — SwiftBindings.Sdk auto-generates + compiles bindings
        // for the supplied xcframework. Mirrors PackGate's WritePackGateConsumerLib.
        var bindingsCsproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{BehaviorTierVersion}">
              <PropertyGroup>
                <TargetFramework>{BehaviorTierConsumerTfm}</TargetFramework>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416;CS0108;CS8625;CS8601;CS8602;CS8603;CS8604;CS8618;CS8619;CS8765;CS8767</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftFramework Include="{xcfw}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(bindingsDir / "AlamofireBindings.csproj", bindingsCsproj);

        // Console app — references the bindings project. HTTPMethod is a
        // RawRepresentable struct in Alamofire (rawValue: String). The Swift
        // round-trip we exercise is constructing one with a known raw value
        // and reading it back, which goes through Swift's init(rawValue:) and
        // the rawValue getter. Static lets like `.get` call swift_once-style
        // init on first access; either path proves the binding loads and runs.
        var appCsproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{BehaviorTierConsumerTfm}</TargetFramework>
                <RuntimeIdentifier>osx-arm64</RuntimeIdentifier>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>
                <ApplicationId>com.swiftbindings.behaviortier.alamofire</ApplicationId>
                <NoWarn>$(NoWarn);CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{bindingsDir / "AlamofireBindings.csproj"}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(appDir / "AlamofireFixture.csproj", appCsproj);

        var program = """
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            using Alamofire;

            // Construct via the rawValue initializer and read it back — exercises
            // both directions of the RawRepresentable bridge through Swift.
            var method = new HTTPMethod(rawValue: "GET");
            Console.WriteLine($"ALAMOFIRE_METHOD={method.RawValue}");
            """;
        File.WriteAllText(appDir / "Program.cs", program);

        Log.Information("  Alamofire: building");
        DotNetBuild(s => s
            .SetProjectFile(appDir / "AlamofireFixture.csproj")
            .SetConfiguration("Release")
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        var appExe = appDir / "bin" / "Release" / BehaviorTierConsumerTfm / "osx-arm64" /
            "AlamofireFixture.app" / "Contents" / "MacOS" / "AlamofireFixture";
        if (!File.Exists(appExe))
            throw new Exception($"consumer binary not produced at {appExe}");

        Log.Information("  Alamofire: launching");
        var (stdout, stderr, exit) = LaunchBehaviorTierConsumer(appExe, appDir);
        if (exit != 0)
            throw new Exception($"consumer exited with code {exit}.\nstdout:\n{stdout}\nstderr:\n{stderr}");
        if (!stdout.Contains("ALAMOFIRE_METHOD=GET", StringComparison.Ordinal))
            throw new Exception(
                $"expected 'ALAMOFIRE_METHOD=GET' in stdout but got:\n{stdout}\nstderr:\n{stderr}");

        Log.Information("  Alamofire OK — HTTPMethod.rawValue round-tripped to managed code");
    }

    static (string Stdout, string Stderr, int Exit) LaunchBehaviorTierConsumer(
        AbsolutePath appExe, AbsolutePath workingDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = appExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir,
        };
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception($"failed to launch {appExe}");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (stdout, stderr, proc.ExitCode);
    }

    static void WriteBehaviorTierNuGetConfig(AbsolutePath fixtureDir, AbsolutePath nupkgDir)
    {
        // packageSourceMapping pins SwiftBindings.* to the local feed so the
        // throwaway-version packages can't be confused with shipped releases.
        var nugetConfig = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="behavior-tier-local" value="{nupkgDir}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="behavior-tier-local">
                  <package pattern="SwiftBindings.*" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        File.WriteAllText(fixtureDir / "NuGet.config", nugetConfig);
    }
}
