// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.PackGate.cs — nupkg packaging regression gate
//
// Packs Runtime + Sdk + Apple supplement at a throwaway version, consumes them
// in a tiny Apple-framework fixture, packs the fixture, and asserts every
// embedded xcframework slice carries an Info.plist. Catches regressions in
// `_ConfigureSwiftBindingPack` (Sdk.targets) AND in
// `_CompileAppleFrameworkSecondWrapperSlice` — the latter is ship-blockers
// Issue 1 (device slice missing Info.plist causes iOS device-install failures
// with MICreateCFBundleEnforcingInfoPlistSize).
//
// Complements the existing `nuke validate` CheckSwiftWrapper gate, which checks
// the intermediate xcframework but not the produced nupkg.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    const string PackGateVersion = "0.0.0-packgate";
    // Apple supplement version must have an integer leading major — the generator
    // parses it via ParseAppleVersionMajor and rejects leading '0'. Pin it to the
    // live Apple train so --apple-version stays semantically correct while the
    // -packgate suffix keeps the scratch nupkg from colliding with a shipped one.
    const string PackGateAppleVersion = "26.2.0-packgate";
    const string PackGateFixtureFramework = "TipKit";

    AbsolutePath PackGateScratch => RootDirectory / "artifacts" / "pack-gate";

    Target PackGate => _ => _
        .DependsOn(Compile)
        .After(ValidateBlastRadius)
        .Executes(() =>
        {
            var scratch = PackGateScratch;
            if (Directory.Exists(scratch)) scratch.DeleteDirectory();
            var nupkgDir = scratch / "packages";
            var fixtureDir = scratch / "fixture";
            var fixtureOut = scratch / "fixture-output";
            nupkgDir.CreateDirectory();
            fixtureDir.CreateDirectory();
            fixtureOut.CreateDirectory();

            Log.Information("=== PackGate: packing fixture at {Version} ===", PackGateVersion);

            using var scope = new VersionScope(PackGateVersion, RootDirectory, PackGateAppleVersion);

            // 1. Publish generator to src/Swift.Bindings.Sdk/tools/net10.0/any/ so
            // the SDK's tools/**/* pack glob picks it up.
            Log.Information("  [1/5] Publishing generator");
            DotNetPublish(s => s
                .SetProject(SourceDir / "Swift.Bindings" / "src" / "Swift.Bindings.csproj")
                .SetConfiguration("Release")
                .SetOutput(SourceDir / "Swift.Bindings.Sdk" / "tools" / DotNetTfm / "any")
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            // 2. Pack the three core packages at the throwaway version.
            Log.Information("  [2/5] Packing Runtime + Sdk + Apple");
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

            // 3. Clear NuGet caches for SwiftBindings.* so the throwaway-version packages
            // aren't shadowed by a stale entry from a previous pack-gate run.
            Log.Information("  [3/5] Clearing NuGet cache");
            ProcessTasks.StartProcess("dotnet", "nuget locals http-cache --clear", logOutput: false)
                .AssertWaitForExit();
            var nugetCacheDir = (AbsolutePath)(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages"));
            foreach (var (pkg, ver) in new[]
            {
                ("swiftbindings.runtime", PackGateVersion),
                ("swiftbindings.sdk", PackGateVersion),
                ("swiftbindings.apple", PackGateAppleVersion),
            })
            {
                var pkgDir = nugetCacheDir / pkg / ver;
                if (Directory.Exists(pkgDir)) pkgDir.DeleteDirectory();
            }

            // 4. Write fixture + NuGet.config, then pack the fixture.
            Log.Information("  [4/5] Packing fixture ({Fw})", PackGateFixtureFramework);
            WritePackGateFixture(fixtureDir, nupkgDir);
            DotNetPack(s => s
                .SetProject(fixtureDir / "PackGateFixture.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(fixtureOut)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            // 5. Unzip and assert the full expected xcframework / slice / Info.plist set.
            //    Enumerating from the file tree and asserting only on what is present would
            //    pass silently if a regression dropped the wrapper xcframework for a TFM, or
            //    a whole slice — so instead check every expected path explicitly.
            Log.Information("  [5/5] Verifying packed xcframework layout");
            var nupkgPath = Directory.GetFiles(fixtureOut, "*.nupkg").FirstOrDefault()
                ?? throw new Exception("PackGate produced no nupkg");
            var extractDir = fixtureOut / "extract";
            if (Directory.Exists(extractDir)) extractDir.DeleteDirectory();
            ZipFile.ExtractToDirectory(nupkgPath, extractDir);

            var wrapperModule = $"{PackGateFixtureFramework}SwiftBindings";
            var failures = new List<string>();
            var verifiedSlices = 0;

            foreach (var (rid, sliceIds) in ExpectedXcframeworkLayout)
            {
                var xcfw = extractDir / "runtimes" / rid / "native" / $"{wrapperModule}.xcframework";
                if (!Directory.Exists(xcfw))
                {
                    failures.Add($"missing xcframework: runtimes/{rid}/native/{wrapperModule}.xcframework/");
                    continue;
                }
                if (!File.Exists(xcfw / "Info.plist"))
                    failures.Add($"missing xcframework Info.plist: runtimes/{rid}/native/{wrapperModule}.xcframework/Info.plist");

                foreach (var sliceId in sliceIds)
                {
                    var slice = xcfw / sliceId;
                    if (!Directory.Exists(slice))
                    {
                        failures.Add($"missing slice: runtimes/{rid}/native/{wrapperModule}.xcframework/{sliceId}/");
                        continue;
                    }
                    var fwDir = slice / $"{wrapperModule}.framework";
                    if (!Directory.Exists(fwDir))
                    {
                        failures.Add($"missing .framework: runtimes/{rid}/native/{wrapperModule}.xcframework/{sliceId}/{wrapperModule}.framework/");
                        continue;
                    }
                    if (!File.Exists(fwDir / wrapperModule))
                        failures.Add($"missing binary: runtimes/{rid}/native/{wrapperModule}.xcframework/{sliceId}/{wrapperModule}.framework/{wrapperModule}");
                    if (!File.Exists(fwDir / "Info.plist"))
                    {
                        failures.Add($"missing Info.plist: runtimes/{rid}/native/{wrapperModule}.xcframework/{sliceId}/{wrapperModule}.framework/Info.plist");
                        continue;
                    }
                    verifiedSlices++;
                }
            }

            var expectedSliceCount = ExpectedXcframeworkLayout.Sum(p => p.Value.Length);

            if (failures.Count > 0)
            {
                Log.Error("PackGate FAILED — {Count} missing entr(ies) in {Nupkg}:",
                    failures.Count, Path.GetFileName(nupkgPath));
                foreach (var f in failures)
                    Log.Error("  {Path}", f);
                Assert.Fail($"PackGate: {failures.Count} expected xcframework entr(ies) missing in {nupkgPath}");
            }

            if (verifiedSlices != expectedSliceCount)
            {
                Assert.Fail($"PackGate: verified {verifiedSlices} slice(s), expected {expectedSliceCount} in {nupkgPath}");
            }

            Log.Information("PackGate OK — verified {Slices} slice(s) across {Xcfw} xcframework(s) in {Nupkg}",
                verifiedSlices, ExpectedXcframeworkLayout.Length, Path.GetFileName(nupkgPath));
        });

    // Expected nupkg layout for the 4-TFM TipKit fixture. Keyed by NuGet RID
    // (mirrors Sdk.props _SwiftBindingNuGetRid). Slice ids mirror Sdk.props
    // _SwiftBindingDeviceSliceId / _SwiftBindingSimulatorSliceId. Kept as a
    // data-driven expected set so a future regression that drops a whole
    // xcframework or slice fails the gate explicitly rather than silently.
    static readonly KeyValuePair<string, string[]>[] ExpectedXcframeworkLayout =
    [
        new("ios-arm64",          new[] { "ios-arm64",          "ios-arm64-simulator" }),
        new("tvos-arm64",         new[] { "tvos-arm64",         "tvos-arm64-simulator" }),
        new("osx-arm64",          new[] { "macos-arm64" }),
        new("maccatalyst-arm64",  new[] { "ios-arm64-maccatalyst" }),
    ];

    static void WritePackGateFixture(AbsolutePath fixtureDir, AbsolutePath nupkgDir)
    {
        // The fixture's purpose is to exercise the nupkg-emission pipeline and assert
        // xcframework slice contents — generated-code warning hygiene is outside scope.
        // Release-mode CS0649/CS0114/CA1416 would otherwise fail the gate on generated
        // vtable fields, Handle overrides, and availability surface that downstream
        // consumer projects already suppress via their own NoWarn / TreatWarningsAsErrors
        // settings.
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{PackGateVersion}">
              <PropertyGroup>
                <!-- Four TFMs — pack gate exercises device + simulator slice assembly
                     on iOS and tvOS, plus the single-slice macOS / MacCatalyst paths. -->
                <TargetFrameworks>net10.0-ios26.2;net10.0-tvos26.2;net10.0-maccatalyst26.2;net10.0-macos26.2</TargetFrameworks>
                <PackageId>PackGateFixture.{PackGateFixtureFramework}</PackageId>
                <PackageVersion>{PackGateVersion}</PackageVersion>
                <IsPackable>true</IsPackable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftAppleFrameworkTarget Include="{PackGateFixtureFramework}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(fixtureDir / "PackGateFixture.csproj", csproj);

        var nugetConfig = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="pack-gate-local" value="{nupkgDir}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="pack-gate-local">
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
