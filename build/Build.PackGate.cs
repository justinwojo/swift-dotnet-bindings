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
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

    // Tiny custom Swift artifact compiled at gate time for the end-to-end
    // consumer step. Single public top-level function returning a
    // deterministic string. Kept macOS-only because the consumer runs on
    // the host machine — no sim, no codesign, no app launch ceremony.
    const string PackGateHelloPackModule = "HelloPack";
    const string PackGateHelloPackTarget = "arm64-apple-macos12.0";
    const string PackGateHelloPackModuleSuffix = "arm64-apple-macos";
    const string PackGateHelloPackMinOs = "12.0";
    const string PackGateHelloPackPlistPlatform = "MacOSX";
    // Single-TFM projects: the version suffix (e.g. -macos26.2) breaks the
    // SDK's `_SwiftBindingPlatform` detection in single-TFM mode (Sdk.props
    // evaluates before the project body's TargetFramework is set, so the
    // Contains check sees an empty value). Multi-TFM projects work because
    // each inner build pre-sets TargetFramework. The SDK template uses the
    // unsuffixed form for the same reason; we mirror it here.
    const string PackGateConsumerTfm = "net10.0-macos";
    const string PackGateConsumerExpected = "Hello, PackGate from Swift!";

    AbsolutePath PackGateScratch => RootDirectory / "artifacts" / "pack-gate";
    AbsolutePath PackGateHelloPackSource =>
        RootDirectory / "build" / "PackGate" / "HelloPack" / "HelloPack.swift";

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

            // Hard-fail guard parity with Pack: the SDK ships SwiftInterfaceParser; if it's
            // missing the gate would silently certify a packaging shape that doesn't actually
            // ship the host binary. Run `nuke compile` on a Darwin host with the Swift toolchain.
            var stagedBinary = SwiftInterfaceParserStagingDir / "SwiftInterfaceParser";
            if (!File.Exists(stagedBinary))
            {
                throw new System.InvalidOperationException(
                    $"PackGate: expected SwiftInterfaceParser binary at '{stagedBinary}' but it is missing. " +
                    "Run `nuke compile` on a macOS host with the Swift toolchain installed " +
                    "(Xcode or the Command Line Tools) before exercising the pack gate.");
            }
            // Parity with Pack: refuse to certify a single-arch artifact through the gate.
            AssertUniversal2(stagedBinary);

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

            // 2b. Assert the Apple supplement nuspec declares Runtime at the bounded range
            // (e.g. [0.0.0-packgate,0.1.0)) rather than the bare min-only version. NuGet's
            // _GetProjectReferenceVersions ignores <Version>/<VersionOverride> on
            // ProjectReference items, so the supplement csproj has to override the resolved
            // _ProjectReferencesWithVersions item — easy to break, silent failure mode (the
            // packed dep just becomes unbounded). This catches it the moment the nuspec is
            // produced.
            AssertSupplementBoundsRuntimeRange(nupkgDir, PackGateVersion, PackGateAppleVersion);

            // 2c. Assert the Runtime nupkg ships ILLink.Descriptors.xml adjacent to
            // SwiftBindings.Runtime.targets in buildTransitive/. The targets file references
            // the descriptor via $(MSBuildThisFileDirectory)ILLink.Descriptors.xml; if the
            // descriptor isn't packed alongside it, every NativeAOT consumer's IlcArg
            // resolves to a non-existent path and ILC silently strips ValueTuple ctors and
            // core Swift.Runtime types that the embedded descriptor (ILLink-only) was
            // pinning. Catches a packaging-contract regression at pack time, before
            // downstream consumers crash at runtime.
            AssertRuntimeBuildTransitiveLayout(nupkgDir, PackGateVersion);

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

            // 3b. Verify SwiftBindings.Runtime's buildTransitive .targets actually injects
            // the ILC descriptor under PublishAot=true. Step 2c proves the file is in the
            // package; this proves the MSBuild conditional fires and points at it. Together
            // they cover the full packaging contract: the file ships AND the targets wire it
            // up. Behavioral end-to-end (ILC actually honoring the descriptor) is covered by
            // RuntimeTestsApp on device (`nuke binding-tests --device`), which references
            // the same descriptor source file.
            AssertRuntimeAotDescriptorInjection(PackGateScratch, nupkgDir, PackGateVersion);

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

            // 6-8. Source xcframework slicing assertions. Packs a second fixture
            //    that references a real multi-platform source xcframework (Nuke)
            //    and asserts the per-RID slice subset is exact — no extras
            //    (regression where slicing silently stops working would otherwise
            //    ship full content under every RID), no missing required slice.
            //    Skipped when Nuke isn't available on disk; CI is expected to run
            //    `nuke fetch` beforehand if it wires PackGate in. The consumer-run
            //    end-to-end gate (step 9 below) is independent of Nuke and runs
            //    either way — don't `return` from this branch.
            var nukeSource = LibrariesDir / "Nuke" / "Nuke.xcframework";
            if (!Directory.Exists(nukeSource))
            {
                Log.Warning("PackGate: skipping source-xcfw slicing checks (run `nuke fetch` first to populate {Path})", nukeSource);
            }
            else
            {
                Log.Information("=== PackGate (source-xcfw): packing Nuke fixture ===");
                var sourceFixtureDir = scratch / "source-fixture";
                var sourceFixtureOut = scratch / "source-fixture-output";
                sourceFixtureDir.CreateDirectory();
                sourceFixtureOut.CreateDirectory();
                WritePackGateSourceFixture(sourceFixtureDir, nupkgDir, nukeSource);
                DotNetPack(s => s
                    .SetProject(sourceFixtureDir / "PackGateSourceFixture.csproj")
                    .SetConfiguration("Release")
                    .SetOutputDirectory(sourceFixtureOut)
                    .EnableNoLogo()
                    .SetVerbosity(DotNetVerbosity.quiet));

                var sourceNupkgPath = Directory.GetFiles(sourceFixtureOut, "*.nupkg").FirstOrDefault()
                    ?? throw new Exception("PackGate source-xcfw produced no nupkg");
                var sourceExtractDir = sourceFixtureOut / "extract";
                if (Directory.Exists(sourceExtractDir)) sourceExtractDir.DeleteDirectory();
                ZipFile.ExtractToDirectory(sourceNupkgPath, sourceExtractDir);

                var sourceFailures = new List<string>();
                var verifiedSourceSlices = 0;
                foreach (var (rid, expectedSlices) in ExpectedSourceXcframeworkLayout)
                {
                    var xcfw = sourceExtractDir / "runtimes" / rid / "native" / "Nuke.xcframework";
                    if (!Directory.Exists(xcfw))
                    {
                        sourceFailures.Add($"missing source xcframework: runtimes/{rid}/native/Nuke.xcframework/");
                        continue;
                    }
                    var actualSlices = Directory.EnumerateDirectories(xcfw)
                        .Select(Path.GetFileName).Where(n => n != null).Cast<string>()
                        .OrderBy(s => s, StringComparer.Ordinal).ToArray();
                    var expected = expectedSlices.OrderBy(s => s, StringComparer.Ordinal).ToArray();
                    if (!actualSlices.SequenceEqual(expected))
                    {
                        sourceFailures.Add(
                            $"runtimes/{rid}/native/Nuke.xcframework/ slice mismatch — " +
                            $"expected [{string.Join(", ", expected)}], got [{string.Join(", ", actualSlices)}]");
                        continue;
                    }
                    verifiedSourceSlices += actualSlices.Length;
                }

                if (sourceFailures.Count > 0)
                {
                    Log.Error("PackGate (source-xcfw) FAILED — {Count} mismatch(es) in {Nupkg}:",
                        sourceFailures.Count, Path.GetFileName(sourceNupkgPath));
                    foreach (var f in sourceFailures)
                        Log.Error("  {Detail}", f);
                    Assert.Fail($"PackGate (source-xcfw): {sourceFailures.Count} per-RID slice mismatch(es) in {sourceNupkgPath}");
                }

                Log.Information("PackGate (source-xcfw) OK — verified {Slices} slice(s) across {Rids} RID(s) in {Nupkg}",
                    verifiedSourceSlices, ExpectedSourceXcframeworkLayout.Length, Path.GetFileName(sourceNupkgPath));

                // 7. Filtered-slice NU5123 sanity. The slice-set assertion above already
                //    guarantees no filtered slice (watchos, maccatalyst on Nuke) is in the
                //    nupkg's runtimes/ tree. Defense-in-depth: walk the extract dir and
                //    fail if any path mentions a filtered slice id, in case a future
                //    layout change accidentally smuggles them in via a different path.
                //    Long-path NU5123 from KEPT slices' swiftinterfaces is a separate
                //    concern (zip toggle TBD per design doc); not asserted here.
                var stalePaths = Directory.EnumerateFiles(sourceExtractDir, "*", SearchOption.AllDirectories)
                    .Where(p => p.Contains("watchos", StringComparison.OrdinalIgnoreCase)
                             || p.Contains("maccatalyst", StringComparison.OrdinalIgnoreCase))
                    .Select(p => Path.GetRelativePath(sourceExtractDir, p))
                    .ToList();
                if (stalePaths.Count > 0)
                    Assert.Fail($"PackGate: {stalePaths.Count} filtered-slice path(s) leaked into nupkg: {string.Join("; ", stalePaths.Take(5))}");

                // 8. Consumer restore + package-shape smoke. A tiny iOS-targeting library
                //    project PackageReferences PackGateSourceFixture.Nuke, restores it,
                //    and builds without -r. This proves the sliced nupkg's manifest is
                //    well-formed and restorable, but does NOT exercise per-RID native
                //    asset selection — _ExpandNativeReferences (which picks the actual
                //    slice for ios-arm64 vs iossimulator-arm64) is driven by an app
                //    publish/build with a RuntimeIdentifier, not by a plain library
                //    build. True per-RID resolution coverage would require an iOS app
                //    consumer published with each RID; deferred as a followup since the
                //    exact-set slice assertions (step 6) + filtered-slice walk (step 7)
                //    already verify the on-disk layout the consumer would resolve from.
                Log.Information("=== PackGate (source-xcfw): consumer restore + package-shape smoke ===");
                var consumerDir = scratch / "source-consumer";
                consumerDir.CreateDirectory();
                WritePackGateSourceConsumer(consumerDir, nupkgDir, sourceFixtureOut);

                // Clear NuGet cache for the fixture nupkg so a stale entry from a
                // prior run doesn't shadow the freshly-packed copy.
                var consumerCacheDir = nugetCacheDir / "packgatesourcefixture.nuke";
                if (Directory.Exists(consumerCacheDir)) consumerCacheDir.DeleteDirectory();

                DotNetRestore(s => s
                    .SetProjectFile(consumerDir / "Consumer.csproj")
                    .SetVerbosity(DotNetVerbosity.quiet));

                DotNetBuild(s => s
                    .SetProjectFile(consumerDir / "Consumer.csproj")
                    .SetConfiguration("Release")
                    .EnableNoRestore()
                    .EnableNoLogo()
                    .SetVerbosity(DotNetVerbosity.quiet));

                Log.Information("PackGate (consumer) OK — restore + library build succeeded against sliced nupkg");
            }

            // 9. End-to-end consumer run. Compiles a tiny custom Swift framework,
            //    scaffolds a fresh bindings library + console-app pair against the
            //    just-packed nupkgs, builds the app, launches it, and asserts the
            //    Swift round-trip string appears in stdout. This is the only gate
            //    today that exercises the full shipping pipeline (pack -> SDK ->
            //    template-shaped consumer -> generator -> runtime -> Swift call ->
            //    return value back to managed code) — every prior step has stopped
            //    at "the binding compiles."
            //
            //    macOS-only on purpose: we run the binary on the host machine
            //    instead of paying for a sim launch. The unique value here is
            //    "the pipeline glues together," which is platform-agnostic;
            //    iOS-sim/device runtime coverage already lives in `nuke
            //    binding-tests --sim --device`.
            Log.Information("=== PackGate (consumer-run): building HelloPack xcframework ===");
            var consumerRunRoot = scratch / "consumer-run";
            consumerRunRoot.CreateDirectory();
            var helloPackXcfw = BuildPackGateHelloPackXcframework(consumerRunRoot);

            var bindingsDir = consumerRunRoot / "bindings";
            var appDir = consumerRunRoot / "app";
            bindingsDir.CreateDirectory();
            appDir.CreateDirectory();
            WritePackGateConsumerNuGetConfig(consumerRunRoot, nupkgDir);
            WritePackGateConsumerLib(bindingsDir, helloPackXcfw);
            WritePackGateConsumerApp(appDir, bindingsDir);

            Log.Information("=== PackGate (consumer-run): building app ===");
            DotNetBuild(s => s
                .SetProjectFile(appDir / "PackGateApp.csproj")
                .SetConfiguration("Release")
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            var appExe = appDir / "bin" / "Release" / PackGateConsumerTfm / "osx-arm64" /
                "PackGateApp.app" / "Contents" / "MacOS" / "PackGateApp";
            if (!File.Exists(appExe))
                Assert.Fail($"PackGate (consumer-run): consumer app binary not produced at {appExe}");

            Log.Information("=== PackGate (consumer-run): launching consumer ===");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = appExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = appDir,
            };
            using var consumerProc = System.Diagnostics.Process.Start(psi)
                ?? throw new Exception($"Failed to launch consumer at {appExe}");
            var consumerStdout = consumerProc.StandardOutput.ReadToEnd();
            var consumerStderr = consumerProc.StandardError.ReadToEnd();
            consumerProc.WaitForExit();

            if (consumerProc.ExitCode != 0)
                Assert.Fail(
                    $"PackGate (consumer-run): consumer exited with code {consumerProc.ExitCode}.\n" +
                    $"stdout:\n{consumerStdout}\nstderr:\n{consumerStderr}");
            if (!consumerStdout.Contains(PackGateConsumerExpected, StringComparison.Ordinal))
                Assert.Fail(
                    $"PackGate (consumer-run): expected '{PackGateConsumerExpected}' in stdout but got:\n" +
                    $"stdout:\n{consumerStdout}\nstderr:\n{consumerStderr}");

            Log.Information("PackGate (consumer-run) OK — Swift round-trip string returned to managed code");
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

    // Expected per-RID source-xcframework slice layout for the Nuke source-xcfw
    // fixture. Asserted as an EXACT set (no extras, no missing) — a regression
    // where slicing silently stops working would otherwise quietly ship every
    // slice under every RID and the gate would still pass. Slice ids are the
    // upstream Nuke.xcframework's actual on-disk identifiers (e.g. fat
    // `arm64_x86_64-simulator` form, not the workload's normalized names).
    // Nuke ships no maccatalyst slice, so the Catalyst RID is absent from the
    // fixture's TFM list — TipKit fixture above already covers maccatalyst.
    static readonly KeyValuePair<string, string[]>[] ExpectedSourceXcframeworkLayout =
    [
        new("ios-arm64",   new[] { "ios-arm64", "ios-arm64_x86_64-simulator" }),
        new("tvos-arm64",  new[] { "tvos-arm64", "tvos-arm64_x86_64-simulator" }),
        new("osx-arm64",   new[] { "macos-arm64_x86_64" }),
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

    static void WritePackGateSourceConsumer(AbsolutePath consumerDir, AbsolutePath nupkgDir, AbsolutePath fixtureNupkgDir)
    {
        // Tiny iOS library that PackageReferences the freshly-packed source-xcfw
        // fixture. No type usage — the build itself is the smoke. Library build
        // without -r exercises restore + manifest shape only; per-RID native asset
        // selection (_ExpandNativeReferences picking ios-arm64 vs iossimulator-arm64)
        // would require an iOS app consumer published with each RID. See step 8
        // commentary in the PackGate target for the deferred-followup rationale.
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0-ios26.2</TargetFramework>
                <Nullable>enable</Nullable>
                <IsPackable>false</IsPackable>
                <NoWarn>$(NoWarn);CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="PackGateSourceFixture.Nuke" Version="{PackGateVersion}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(consumerDir / "Consumer.csproj", csproj);
        File.WriteAllText(consumerDir / "Class1.cs", "public class Class1 { }\n");

        var nugetConfig = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="pack-gate-local" value="{nupkgDir}" />
                <add key="pack-gate-fixture" value="{fixtureNupkgDir}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="pack-gate-local">
                  <package pattern="SwiftBindings.*" />
                </packageSource>
                <packageSource key="pack-gate-fixture">
                  <package pattern="PackGateSourceFixture.*" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        File.WriteAllText(consumerDir / "NuGet.config", nugetConfig);
    }

    static void WritePackGateSourceFixture(AbsolutePath fixtureDir, AbsolutePath nupkgDir, AbsolutePath sourceXcfwPath)
    {
        // 3 TFMs — Nuke ships ios + tvos + macos slices (no maccatalyst). The
        // TipKit fixture above already exercises the maccatalyst RID; this fixture
        // is single-purpose for source-xcfw slicing assertions across the RIDs the
        // source actually supports. SwiftFramework Include points at the on-disk
        // Nuke.xcframework so the SDK's discover step picks it up.
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{PackGateVersion}">
              <PropertyGroup>
                <TargetFrameworks>net10.0-ios26.2;net10.0-tvos26.2;net10.0-macos26.2</TargetFrameworks>
                <PackageId>PackGateSourceFixture.Nuke</PackageId>
                <PackageVersion>{PackGateVersion}</PackageVersion>
                <IsPackable>true</IsPackable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416;CS8604</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftFramework Include="{sourceXcfwPath}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(fixtureDir / "PackGateSourceFixture.csproj", csproj);

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

    // Compile build/PackGate/HelloPack/HelloPack.swift into a macOS-only
    // single-slice xcframework under <consumerRunRoot>/swift/. Reuses the
    // existing CompileModuleSlice + XcodeBuild pipeline that BindingTests
    // already drives, just without the device slice and dependency module.
    AbsolutePath BuildPackGateHelloPackXcframework(AbsolutePath consumerRunRoot)
    {
        var swiftBuildDir = consumerRunRoot / "swift";
        if (Directory.Exists(swiftBuildDir)) swiftBuildDir.DeleteDirectory();
        var sliceDir = swiftBuildDir / "macos-arm64";
        sliceDir.CreateDirectory();
        var frameworkDir = sliceDir / $"{PackGateHelloPackModule}.framework";

        var sdkPath = XcRun.GetSdkPath("macosx");
        CompileModuleSlice(
            moduleName: PackGateHelloPackModule,
            target: PackGateHelloPackTarget,
            sdkPath: sdkPath,
            moduleSuffix: PackGateHelloPackModuleSuffix,
            minOs: PackGateHelloPackMinOs,
            plistPlatform: PackGateHelloPackPlistPlatform,
            frameworkDir: frameworkDir,
            sourceFiles: new[] { PackGateHelloPackSource.ToString() },
            frameworkSearchPaths: null,
            swiftDefines: null);

        var xcframeworkPath = swiftBuildDir / $"{PackGateHelloPackModule}.xcframework";
        if (Directory.Exists(xcframeworkPath)) xcframeworkPath.DeleteDirectory();
        XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings()
            .AddFrameworkPath(frameworkDir)
            .SetOutputPath(xcframeworkPath));
        return xcframeworkPath;
    }

    static void WritePackGateConsumerNuGetConfig(AbsolutePath consumerRoot, AbsolutePath nupkgDir)
    {
        // Single NuGet.config at the consumer-run root — both the bindings
        // library and the app csproj cascade into it via the standard NuGet
        // discovery walk, so neither child project needs its own copy.
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
        File.WriteAllText(consumerRoot / "NuGet.config", nugetConfig);
    }

    static void WritePackGateConsumerLib(AbsolutePath bindingsDir, AbsolutePath helloPackXcfw)
    {
        // SwiftFramework points at the freshly-built HelloPack.xcframework.
        // The SDK auto-injects the wrapper xcframework into consuming projects
        // via GetNativeManifest, so the app csproj only needs ProjectReference.
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{PackGateVersion}">
              <PropertyGroup>
                <TargetFramework>{PackGateConsumerTfm}</TargetFramework>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftFramework Include="{helloPackXcfw}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(bindingsDir / "PackGateBindings.csproj", csproj);
    }

    static void WritePackGateConsumerApp(AbsolutePath appDir, AbsolutePath bindingsDir)
    {
        // Plain net10.0-macos console app. RuntimeIdentifier=osx-arm64 so the
        // .app bundle has a single arch matching the wrapper xcframework slice
        // (which we built arm64-only). EnableCodeSigning=false matches the
        // RuntimeTestsApp.Mac pattern — there's nothing here that needs to be
        // distributable.
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{PackGateConsumerTfm}</TargetFramework>
                <RuntimeIdentifier>osx-arm64</RuntimeIdentifier>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>
                <ApplicationId>com.swiftbindings.packgate</ApplicationId>
                <NoWarn>$(NoWarn);CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{bindingsDir / "PackGateBindings.csproj"}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(appDir / "PackGateApp.csproj", csproj);

        var program = """
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            var greeting = global::HelloPack.Functions.PackGateGreet("PackGate");
            Console.WriteLine(greeting);
            """;
        File.WriteAllText(appDir / "Program.cs", program);
    }

    // Opens the just-packed Apple supplement nupkg, reads its nuspec, and asserts every
    // Apple TFM group carries exactly one SwiftBindings.Runtime dependency stamped at the
    // bounded range built from the runtime version. The packed nuspec is the only place
    // this is observable: unit tests can pin the csproj override target, but the actual
    // NuGet pack pipeline is what produces the dep declaration, so we verify the output.
    //
    // Both the per-group dep count AND the exact range string matter: a future regression
    // could drop the dep from three of the four TFM groups, or stamp a different range
    // shape (e.g. `[v,)` unbounded-upper, or a wider ceiling than RuntimeVersionRange.Build
    // produces). A loose 'starts-with-[' check would let those slip through.
    static void AssertSupplementBoundsRuntimeRange(
        AbsolutePath nupkgDir, string runtimeVersion, string appleVersion)
    {
        // Mirrors the four <TargetFrameworks> entries in Swift.Bindings.Apple.csproj. NuGet
        // emits one <group> per TFM with a normalized OS-version suffix (e.g. "26.0"); we
        // match by prefix to stay decoupled from whichever Apple TFM the host SDK pins.
        string[] expectedTfmPrefixes =
        {
            "net10.0-ios",
            "net10.0-maccatalyst",
            "net10.0-macos",
            "net10.0-tvos",
        };

        var supplementPath = nupkgDir / $"SwiftBindings.Apple.{appleVersion}.nupkg";
        if (!File.Exists(supplementPath))
            Assert.Fail($"PackGate: expected supplement nupkg at {supplementPath}, but it was not produced.");

        using var archive = ZipFile.OpenRead(supplementPath);
        var nuspecEntry = archive.Entries.FirstOrDefault(e =>
            e.FullName.Equals("SwiftBindings.Apple.nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception($"PackGate: SwiftBindings.Apple.nuspec missing from {supplementPath}");
        using var reader = new StreamReader(nuspecEntry.Open());
        var nuspec = reader.ReadToEnd();

        var doc = System.Xml.Linq.XDocument.Parse(nuspec);
        var ns = doc.Root!.GetDefaultNamespace();

        // Compare against the canonical bounded range with whitespace stripped — NuGet
        // sometimes inserts a space after the comma during nuspec serialization, so both
        // "[0.9.0,0.10.0)" and "[0.9.0, 0.10.0)" are accepted; anything else is a regression.
        var expectedRange = BindingsGeneration.RuntimeVersionRange.Build(runtimeVersion);
        var expectedNormalized = StripWhitespace(expectedRange);

        var groups = doc.Descendants(ns + "group").ToList();
        var seenPrefixes = new HashSet<string>();
        foreach (var group in groups)
        {
            var tfm = (string?)group.Attribute("targetFramework") ?? "";
            var matchedPrefix = expectedTfmPrefixes.FirstOrDefault(p =>
                tfm.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (matchedPrefix is null)
            {
                Assert.Fail(
                    $"PackGate: supplement nuspec has unexpected <group targetFramework=\"{tfm}\">. " +
                    $"Expected one of: {string.Join(", ", expectedTfmPrefixes)}. Path: {supplementPath}");
                return; // Assert.Fail throws — annotate for nullable analysis.
            }
            seenPrefixes.Add(matchedPrefix);

            var deps = group.Elements(ns + "dependency")
                .Where(d => (string?)d.Attribute("id") == "SwiftBindings.Runtime")
                .ToList();
            if (deps.Count != 1)
            {
                Assert.Fail(
                    $"PackGate: supplement nuspec group '{tfm}' has {deps.Count} " +
                    $"SwiftBindings.Runtime entries, expected exactly 1. Path: {supplementPath}");
            }

            var version = (string?)deps[0].Attribute("version") ?? "";
            if (!string.Equals(StripWhitespace(version), expectedNormalized, StringComparison.Ordinal))
            {
                Assert.Fail(
                    $"PackGate: supplement nuspec group '{tfm}' declares Runtime as '{version}', " +
                    $"expected '{expectedRange}'. This is the regression where " +
                    $"_GetProjectReferenceVersions writes the bare $(PackageVersion) of " +
                    $"Swift.Runtime instead of the $(SwiftRuntimePackageVersionRange) override. " +
                    $"Path: {supplementPath}");
            }
        }

        var missing = expectedTfmPrefixes.Where(p => !seenPrefixes.Contains(p)).ToList();
        if (missing.Count > 0)
        {
            Assert.Fail(
                $"PackGate: supplement nuspec is missing dependency groups for TFM(s): " +
                $"{string.Join(", ", missing)}. Path: {supplementPath}");
        }

        Log.Information("  Apple supplement nuspec: Runtime dep is '{Range}' across {Count} TFM group(s)",
            expectedRange, groups.Count);

        static string StripWhitespace(string s) =>
            new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());
    }

    // Static layout assertion: SwiftBindings.Runtime.{ver}.nupkg must contain BOTH
    // buildTransitive/SwiftBindings.Runtime.targets AND buildTransitive/ILLink.Descriptors.xml.
    // The targets file resolves the descriptor via $(MSBuildThisFileDirectory)ILLink.Descriptors.xml,
    // so the two must ship adjacent in buildTransitive/. A regression that drops the descriptor
    // from the pack manifest would silently produce IlcArgs pointing at non-existent files —
    // ILC would either error obscurely or, worse, succeed-with-warning while stripping the
    // pinned types. Caught at pack time before downstream consumers see the failure.
    static void AssertRuntimeBuildTransitiveLayout(AbsolutePath nupkgDir, string runtimeVersion)
    {
        var runtimePath = nupkgDir / $"SwiftBindings.Runtime.{runtimeVersion}.nupkg";
        if (!File.Exists(runtimePath))
            Assert.Fail($"PackGate: expected runtime nupkg at {runtimePath}, but it was not produced.");

        using var archive = ZipFile.OpenRead(runtimePath);
        string[] requiredEntries =
        {
            "buildTransitive/SwiftBindings.Runtime.targets",
            "buildTransitive/ILLink.Descriptors.xml",
        };
        var missing = requiredEntries
            .Where(e => !archive.Entries.Any(entry => string.Equals(entry.FullName, e, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count > 0)
        {
            Assert.Fail(
                $"PackGate: SwiftBindings.Runtime.{runtimeVersion}.nupkg is missing required " +
                $"buildTransitive entr(ies): {string.Join(", ", missing)}. " +
                $"The NativeAOT descriptor wiring depends on both files being adjacent in " +
                $"buildTransitive/ — without ILLink.Descriptors.xml on disk the IlcArg " +
                $"--descriptor: path resolves to a non-existent file. Path: {runtimePath}");
        }

        // Embedded-resource leg of the contract: the IL trimmer (ILLink) auto-discovers
        // descriptors embedded as ManifestResource on referenced assemblies. Trimmed-but-
        // not-AOT consumers (PublishTrimmed=true on a library, or any IsTrimmable=true
        // assembly) rely on this path — buildTransitive/ wiring is ILC-only. Without this
        // assertion, removing <EmbeddedResource Include="ILLink.Descriptors.xml"> from
        // Swift.Runtime.csproj would silently strip ValueTuple ctors on those consumers
        // while the buildTransitive/ILC path kept passing. Check every TFM-specific
        // Swift.Runtime.dll: Apple consumers receive the lib/net10.0-ios26.0 (or similar)
        // assembly, not the plain net10.0 one, so a regression scoped to a single TFM
        // would slip past a check on the first DLL only.
        var dllEntries = archive.Entries
            .Where(e =>
                e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.EndsWith("/Swift.Runtime.dll", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (dllEntries.Count == 0)
        {
            Assert.Fail(
                $"PackGate: SwiftBindings.Runtime.{runtimeVersion}.nupkg contains no " +
                $"lib/<tfm>/Swift.Runtime.dll entry — cannot verify the embedded ILLink.Descriptors.xml " +
                $"manifest resource. Path: {runtimePath}");
        }

        var missingResource = new List<string>();
        foreach (var dllEntry in dllEntries)
        {
            using var dllStream = dllEntry.Open();
            using var ms = new MemoryStream();
            dllStream.CopyTo(ms);
            ms.Position = 0;
            using var peReader = new PEReader(ms);
            var mdReader = peReader.GetMetadataReader();
            var hasDescriptor = mdReader.ManifestResources
                .Select(h => mdReader.GetString(mdReader.GetManifestResource(h).Name))
                .Contains("ILLink.Descriptors.xml");
            if (!hasDescriptor)
                missingResource.Add(dllEntry.FullName);
        }

        if (missingResource.Count > 0)
        {
            Assert.Fail(
                $"PackGate: the following Swift.Runtime.dll entries inside " +
                $"SwiftBindings.Runtime.{runtimeVersion}.nupkg are missing the embedded " +
                $"'ILLink.Descriptors.xml' manifest resource: {string.Join(", ", missingResource)}. " +
                $"The IL trimmer auto-discovers this descriptor via ManifestResource on referenced " +
                $"assemblies — without it, trimmed (non-AOT) consumers silently strip ValueTuple " +
                $"ctors and core Swift.Runtime types. Apple consumers receive the platform-specific " +
                $"TFM (e.g. lib/net10.0-ios26.0), not lib/net10.0, so the descriptor must be embedded " +
                $"in every TFM. Restore <EmbeddedResource Include=\"ILLink.Descriptors.xml\"> in " +
                $"src/Swift.Runtime/src/Swift.Runtime.csproj.");
        }

        Log.Information("  Runtime nupkg buildTransitive layout: ILLink.Descriptors.xml + SwiftBindings.Runtime.targets present");
        Log.Information("  Runtime assembly embedded resource: ILLink.Descriptors.xml present in all {Count} TFM-specific Swift.Runtime.dll entries", dllEntries.Count);
    }

    // Item-injection assertion: under PublishAot=true, the buildTransitive .targets must
    // inject an IlcArg containing '--descriptor:.../ILLink.Descriptors.xml' AND a matching
    // TrimmerRootDescriptor item. Verified by spinning up a tiny consumer that
    // PackageReferences SwiftBindings.Runtime, then running `dotnet msbuild` against an
    // in-csproj target that prints the resolved item identities. Hermetic — no actual ILC
    // publish, no Apple workload, no codesign. The "ILC actually honors --descriptor" leg
    // of the contract is covered by RuntimeTestsApp on device (NativeAOT), which references
    // the same source descriptor file directly.
    static void AssertRuntimeAotDescriptorInjection(
        AbsolutePath scratch, AbsolutePath nupkgDir, string runtimeVersion)
    {
        var consumerDir = scratch / "aot-injection";
        if (Directory.Exists(consumerDir)) consumerDir.DeleteDirectory();
        consumerDir.CreateDirectory();

        WritePackGateConsumerNuGetConfig(consumerDir, nupkgDir);

        // Plain net10.0 (not workload TFM) keeps this hermetic — no Apple workload,
        // no codesign, no AOT runtime pack lookup. The buildTransitive .targets fire
        // during evaluation regardless of TFM, so this is sufficient to verify the
        // PublishAot conditional + item injection.
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PublishAot>true</PublishAot>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="SwiftBindings.Runtime" Version="{runtimeVersion}" />
              </ItemGroup>
              <Target Name="DumpInjectedItems">
                <Message Text="ILC_ARG_ITEM:%(IlcArg.Identity)" Importance="high" />
                <Message Text="TRIMMER_ROOT_ITEM:%(TrimmerRootDescriptor.Identity)" Importance="high" />
              </Target>
            </Project>
            """;
        File.WriteAllText(consumerDir / "AotInjection.csproj", csproj);

        DotNetRestore(s => s
            .SetProjectFile(consumerDir / "AotInjection.csproj")
            .SetVerbosity(DotNetVerbosity.quiet));

        var process = ProcessTasks.StartProcess(
                "dotnet",
                "msbuild AotInjection.csproj -t:DumpInjectedItems -p:PublishAot=true -nologo -v:minimal",
                workingDirectory: consumerDir, logOutput: false)
            .AssertWaitForExit();
        var output = process.Output.StdToText();

        if (process.ExitCode != 0)
        {
            Assert.Fail(
                $"PackGate (AOT injection): dotnet msbuild exited {process.ExitCode}.\n{output}");
        }

        var ilcArgLines = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("ILC_ARG_ITEM:", StringComparison.Ordinal))
            .ToList();
        var trimmerRootLines = output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains("TRIMMER_ROOT_ITEM:", StringComparison.Ordinal))
            .ToList();

        var ilcArgHasDescriptor = ilcArgLines.Any(l =>
            l.Contains("--descriptor:", StringComparison.Ordinal) &&
            l.Contains("ILLink.Descriptors.xml", StringComparison.Ordinal));
        var trimmerRootHasDescriptor = trimmerRootLines.Any(l =>
            l.Contains("ILLink.Descriptors.xml", StringComparison.Ordinal));

        if (!ilcArgHasDescriptor || !trimmerRootHasDescriptor)
        {
            Assert.Fail(
                $"PackGate (AOT injection): SwiftBindings.Runtime's buildTransitive targets is " +
                $"not injecting the descriptor under PublishAot=true. " +
                $"IlcArg with --descriptor:ILLink.Descriptors.xml present: {ilcArgHasDescriptor}. " +
                $"TrimmerRootDescriptor with ILLink.Descriptors.xml present: {trimmerRootHasDescriptor}. " +
                $"Without these, NativeAOT consumers silently strip ValueTuple constructors and " +
                $"core Swift.Runtime types — see ILLink.Descriptors.xml for the affected surface.\n" +
                $"IlcArg items found:\n  {(ilcArgLines.Count == 0 ? "(none)" : string.Join("\n  ", ilcArgLines))}\n" +
                $"TrimmerRootDescriptor items found:\n  {(trimmerRootLines.Count == 0 ? "(none)" : string.Join("\n  ", trimmerRootLines))}");
        }

        Log.Information("  Runtime AOT injection: IlcArg + TrimmerRootDescriptor wired correctly under PublishAot=true");
    }
}
