// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.MixedPack.cs — opt-in iOS mixed-framework pack→consume→run leg
//
// Closes the one end-to-end gap the macOS PackGate cannot: a mixed (ObjC + Swift)
// binding, packed into a SINGLE NuGet package and consumed via ONE PackageReference,
// LINKED and RUN on the iOS runtimes where duplicate-ObjC-class registration actually
// bites — the iOS Simulator (Mono JIT) and a physical device (NativeAOT). This is the
// exact shape of the issue #40 report: a static mixed framework whose ObjC class, if the
// source archive is embedded ALONGSIDE the force-loading wrapper, registers twice and the
// loader prints "Class X is implemented in both …". PackGate proves the nupkg STRUCTURE
// (source dropped, wrapper sole-carrier, companion embedded in lib/) and runs the consumer
// on the macOS host; the Gap-2 drop decision is keyed on native linkage, not platform, so
// the structure is identical here. What macOS cannot do is exercise the iOS LOADER + the
// Mono-JIT / NativeAOT runtimes, which is the unique value of this leg.
//
// OPT-IN BY DESIGN. This leg is never part of the default `nuke binding-tests` run and
// never part of `--compile-only`. It only runs when `--mixed-pack` is explicitly passed,
// composing with `--sim` / `--device` (defaults to --sim when neither is given). It is a
// heavyweight gate — it packs the Runtime/SDK/Apple feed at a throwaway version, builds a
// 2-slice iOS mixed xcframework, packs the fixture, then builds (sim) or NativeAOT-publishes
// (device) a fresh single-PackageReference consumer and deploys it. Run it before a release
// and after changes to native packaging policy, the ObjC companion pack path, calling
// conventions, or struct/P-Invoke marshalling — NOT on every inner-loop iteration. Needs a
// booted simulator (--sim) and/or a provisioned device (--device).

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
    [Parameter("Opt-in: pack a mixed (ObjC+Swift) binding into ONE nupkg and consume it via a single PackageReference on iOS sim/device. Composes with --sim/--device; never part of the default run or --compile-only.")]
    readonly bool MixedPack;

    // Distinct throwaway versions (own suffix) so the leg is independent of whether
    // PackGate ran and its NuGet-cache clears never collide with PackGate's. The Apple
    // supplement major must be an integer (the generator rejects a leading 0), so pin it
    // to the live Apple train with a -mixedpack suffix, mirroring PackGateAppleVersion.
    const string MixedPackVersion = "0.0.0-mixedpack";
    const string MixedPackAppleVersion = "26.2.0-mixedpack";

    // The mixed fixture: a STATIC source (the issue #40 condition — the wrapper force-loads
    // the ObjC archive and is the sole carrier, the source is dropped from the consumer).
    const string MixedPackModule = "SbMixedPack";
    const string MixedPackProbeClass = "SbMixedPackProbe";

    // The consumer app's bundle id / app name (drives simctl/devicectl install + launch).
    const string MixedPackBundleId = "com.swiftbindings.mixedpack";
    const string MixedPackAppName = "MixedPackApp";

    // The NuGet rid the SwiftBindings.Sdk lays the iOS native xcframeworks under
    // (matches PackGate's ExpectedSourceXcframeworkLayout / ExpectedXcframeworkLayout).
    const string MixedPackIosRid = "ios-arm64";

    AbsolutePath MixedPackScratch => RootDirectory / "artifacts" / "mixed-pack";

    // Entry point invoked from the BindingTests target dispatch when --mixed-pack is set.
    // Builds the feed + fixture once (the nupkg is platform-agnostic: it carries both the
    // ios-arm64 device and the ios-arm64-simulator slices), runs the structural assertions
    // once, then consumes + links + RUNS on each requested platform.
    void RunMixedPackLeg(bool sim, bool device)
    {
        Log.Information("=================================================");
        Log.Information(" BindingTests — mixed-framework pack→consume→run");
        Log.Information("   sim: {Sim}   device: {Device}", sim, device);
        Log.Information("=================================================");

        // Hard-fail guard parity with PackGate: the SDK ships SwiftInterfaceParser; if it's
        // missing the fixture pack would silently certify a packaging shape that doesn't ship
        // the host parser. Run `nuke compile` on a Darwin host with the Swift toolchain first.
        var stagedBinary = SwiftInterfaceParserStagingDir / "SwiftInterfaceParser";
        if (!File.Exists(stagedBinary))
            throw new InvalidOperationException(
                $"--mixed-pack: expected SwiftInterfaceParser binary at '{stagedBinary}' but it is missing. " +
                "Run `nuke compile` on a macOS host with the Swift toolchain installed before exercising this leg.");

        var scratch = MixedPackScratch;
        if (Directory.Exists(scratch)) scratch.DeleteDirectory();
        var nupkgDir = scratch / "packages";
        var fixtureDir = scratch / "fixture";
        var fixtureOut = scratch / "fixture-output";
        nupkgDir.CreateDirectory();
        fixtureDir.CreateDirectory();
        fixtureOut.CreateDirectory();

        using var scope = new VersionScope(MixedPackVersion, RootDirectory, MixedPackAppleVersion);

        BuildMixedPackFeed(nupkgDir, scope);

        Log.Information("=== mixed-pack: building 2-slice (device + simulator) iOS mixed xcframework ===");
        var xcfw = BuildMixedPackIosXcframework(scratch / "build", MixedPackModule, MixedPackProbeClass);

        WriteMixedPackFixture(fixtureDir, nupkgDir, MixedPackModule, xcfw);

        Log.Information("=== mixed-pack: packing fixture ===");
        DotNetPack(s => s
            .SetProject(fixtureDir / $"{MixedPackModule}.csproj")
            .SetConfiguration("Release")
            .SetOutputDirectory(fixtureOut)
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        AssertMixedPackNupkgStructure(fixtureOut, MixedPackModule, MixedPackProbeClass);

        // Consume + link + RUN on each requested platform from the SAME packed fixture.
        if (sim)
            RunMixedPackConsumer(scratch, nupkgDir, fixtureOut, MixedPackModule, MixedPackProbeClass, onDevice: false);
        if (device)
            RunMixedPackConsumer(scratch, nupkgDir, fixtureOut, MixedPackModule, MixedPackProbeClass, onDevice: true);
    }

    // Builds the throwaway-version Runtime + SDK + Apple feed the fixture and consumer
    // restore from. A trimmed mirror of PackGate steps 1–3 (publish generator into the
    // SDK tools dir, pack the three core packages, clear the SwiftBindings.* NuGet cache so
    // a stale same-version entry from a prior run can't shadow these).
    void BuildMixedPackFeed(AbsolutePath nupkgDir, VersionScope scope)
    {
        Log.Information("=== mixed-pack: building local feed at {Version} ===", MixedPackVersion);

        Log.Information("  [1/3] Publishing generator into SDK tools");
        DotNetPublish(s => scope.Apply(s
            .SetProject(SourceDir / "Swift.Bindings" / "src" / "Swift.Bindings.csproj")
            .SetConfiguration("Release")
            .SetOutput(SourceDir / "Swift.Bindings.Sdk" / "tools" / DotNetTfm / "any")
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet)));

        Log.Information("  [2/3] Packing Runtime + Sdk + Apple");
        foreach (var csproj in new[]
        {
            SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj",
            SourceDir / "Swift.Bindings.Sdk" / "Swift.Bindings.Sdk.csproj",
            SourceDir / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj",
        })
        {
            DotNetPack(s => scope.Apply(s
                .SetProject(csproj)
                .SetConfiguration("Release")
                .SetOutputDirectory(nupkgDir)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet)));
        }

        Log.Information("  [3/3] Clearing NuGet cache for throwaway-version packages");
        ProcessTasks.StartProcess("dotnet", "nuget locals http-cache --clear", logOutput: false)
            .AssertWaitForExit();
        var nugetCacheDir = (AbsolutePath)(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
        foreach (var (pkg, ver) in new[]
        {
            ("swiftbindings.runtime", MixedPackVersion),
            ("swiftbindings.sdk", MixedPackVersion),
            ("swiftbindings.apple", MixedPackAppleVersion),
            ($"mixedpackfixture.{MixedPackModule.ToLowerInvariant()}", MixedPackVersion),
        })
        {
            var pkgDir = nugetCacheDir / pkg / ver;
            if (Directory.Exists(pkgDir)) pkgDir.DeleteDirectory();
        }
    }

    // Assembles a 2-slice (ios-arm64 device + ios-arm64-simulator) STATIC mixed xcframework
    // from source, reusing the per-slice recipe shared with the macOS PackGate fixture
    // (WriteMixedFrameworkSources + BuildMixedFrameworkSlice in Build.PackGate.MixedFixture.cs).
    // Slice parameters come from ApplePlatform.IOS so the device/sim triples, module suffixes,
    // sdks, min-OS, and plist platforms stay the single source of truth.
    AbsolutePath BuildMixedPackIosXcframework(AbsolutePath buildRoot, string module, string probeClass)
    {
        if (Directory.Exists(buildRoot)) buildRoot.DeleteDirectory();
        buildRoot.CreateDirectory();
        var (probeM, libSwift) = WriteMixedFrameworkSources(buildRoot, module, probeClass);

        var ios = ApplePlatform.IOS;

        var deviceSlice = buildRoot / ios.DeviceSliceId!;
        BuildMixedFrameworkSlice(
            deviceSlice, probeM, libSwift, module, probeClass, isStatic: true,
            triple: ios.DeviceTarget!, moduleSuffix: ios.DeviceModuleSuffix!,
            sdkName: ios.DeviceSdkName!, minOs: ios.MinOsVersion, plistPlatform: ios.DevicePlistPlatform!);

        var simSlice = buildRoot / ios.SimulatorSliceId;
        BuildMixedFrameworkSlice(
            simSlice, probeM, libSwift, module, probeClass, isStatic: true,
            triple: ios.SimulatorTarget, moduleSuffix: ios.SimulatorModuleSuffix,
            sdkName: ios.SimulatorSdkName, minOs: ios.MinOsVersion, plistPlatform: ios.SimulatorPlistPlatform);

        var xcframeworkPath = buildRoot / $"{module}.xcframework";
        if (Directory.Exists(xcframeworkPath)) xcframeworkPath.DeleteDirectory();
        XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings()
            .AddFrameworkPath(deviceSlice / $"{module}.framework")
            .AddFrameworkPath(simSlice / $"{module}.framework")
            .SetOutputPath(xcframeworkPath));

        Log.Information("  built static+mixed iOS xcframework (device + simulator slices): {Path}", xcframeworkPath);
        return xcframeworkPath;
    }

    // The mixed binding fixture, targeting the unsuffixed net10.0-ios single TFM (the
    // version-suffixed form breaks the SDK's single-TFM platform detection — see the
    // PackGateConsumerTfm commentary). SwiftBindings.Sdk drives generate → compile wrapper →
    // build the ObjC companion and EMBED its managed dll into the single Swift nupkg's lib/.
    static void WriteMixedPackFixture(
        AbsolutePath fixtureDir, AbsolutePath nupkgDir, string module, AbsolutePath xcfwPath)
    {
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{MixedPackVersion}">
              <PropertyGroup>
                <TargetFramework>net10.0-ios</TargetFramework>
                <PackageId>MixedPackFixture.{module}</PackageId>
                <PackageVersion>{MixedPackVersion}</PackageVersion>
                <IsPackable>true</IsPackable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416;CS8604</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftFramework Include="{xcfwPath}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(fixtureDir / $"{module}.csproj", csproj);
        File.WriteAllText(fixtureDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));
    }

    // Structural assertions on the REAL packed iOS Swift nupkg — the iOS counterpart to the
    // macOS PackGate static-leg checks, so a packaging regression that is iOS-specific (wrong
    // rid slice, companion landing under the wrong lib/ TFM casing) is caught here even when
    // the macOS gate stays green. Reuses the platform-neutral helpers from
    // Build.PackGate.MixedFixture.cs; the runtimes/ rid + the lib/ TFM slice are iOS here.
    void AssertMixedPackNupkgStructure(AbsolutePath fixtureOut, string module, string probeClass)
    {
        var swiftNupkg = fixtureOut / $"MixedPackFixture.{module}.{MixedPackVersion}.nupkg";
        if (!File.Exists(swiftNupkg))
            Assert.Fail($"--mixed-pack: Swift binding nupkg not produced at {swiftNupkg}");

        // ONE package only: no standalone ObjC companion nupkg (the companion is embedded in
        // the Swift binding's lib/, never packed separately).
        var separateCompanion = Directory
            .EnumerateFiles(fixtureOut, $"{module}.ObjC.iOS.*.nupkg")
            .Select(Path.GetFileName)
            .ToList();
        if (separateCompanion.Count > 0)
            Assert.Fail(
                $"--mixed-pack: a standalone ObjC companion nupkg was produced ({string.Join(", ", separateCompanion)}) — " +
                "the companion must be EMBEDDED in the Swift binding's lib/, not packed as a separate package " +
                "(check the companion csproj keeps IsPackable=false).");

        var extract = fixtureOut / "extract";
        ExtractNupkg(swiftNupkg, extract);

        var failures = new List<string>();
        var native = extract / "runtimes" / MixedPackIosRid / "native";
        var sourceXcfw = native / $"{module}.xcframework";
        var wrapperXcfw = native / $"{module}SwiftBindings.xcframework";

        // (a) STATIC source is dropped — the wrapper is the sole native carrier of the ObjC class.
        if (Directory.Exists(sourceXcfw))
            failures.Add(
                $"static source xcframework was packed (Gap 2 double-embed hazard): " +
                $"runtimes/{MixedPackIosRid}/native/{module}.xcframework/ — for static linkage the wrapper " +
                $"force-loads the archive and is the sole carrier; the source must be dropped.");

        // (b) the wrapper IS shipped and (c) its ios-arm64 (device) slice actually carries the
        //     force-loaded ObjC class symbol (nm reads the arm64 archive cross-arch on the host).
        if (!Directory.Exists(wrapperXcfw))
        {
            failures.Add($"missing wrapper xcframework: runtimes/{MixedPackIosRid}/native/{module}SwiftBindings.xcframework/");
        }
        else
        {
            // Find the iOS *device* slice dir without hardcoding its exact name: it is thin
            // "ios-arm64" for a source binding today, but tolerate a future fat/renamed device
            // slice (e.g. "ios-arm64_<x>") by matching the "ios-arm64" prefix and excluding the
            // simulator slice — so a naming drift can't turn a present symbol into a false miss.
            // A well-formed iOS xcframework has exactly ONE device slice; fail closed on an
            // ambiguous (>1) match rather than picking one by enumeration order, mirroring the
            // deterministic, fail-loud handling of the .app candidates in RunMixedPackConsumer.
            var deviceSlices = Directory
                .EnumerateDirectories(wrapperXcfw, "ios-arm64*")
                .Where(d => !Path.GetFileName(d).Contains("simulator", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (deviceSlices.Count == 0)
            {
                failures.Add($"no iOS device slice (ios-arm64*, non-simulator) found inside the packed wrapper xcframework {module}SwiftBindings.xcframework");
            }
            else if (deviceSlices.Count > 1)
            {
                failures.Add(
                    $"ambiguous iOS device slices in the packed wrapper xcframework {module}SwiftBindings.xcframework " +
                    $"({string.Join(", ", deviceSlices.Select(Path.GetFileName))}) — expected exactly one non-simulator ios-arm64* slice.");
            }
            else
            {
                var deviceSlice = deviceSlices[0];
                var sliceName = Path.GetFileName(deviceSlice);
                var wrapperBinary = Directory
                    .EnumerateFiles(deviceSlice, $"{module}SwiftBindings", SearchOption.AllDirectories)
                    .FirstOrDefault(p => !p.EndsWith(".plist", StringComparison.Ordinal));
                if (wrapperBinary is null)
                    failures.Add($"wrapper binary {module}SwiftBindings not found inside the packed wrapper xcframework's {sliceName} (iOS device) slice");
                else if (!NmDefinedGlobals(wrapperBinary).Contains($"_OBJC_CLASS_$_{probeClass}", StringComparison.Ordinal))
                    failures.Add(
                        $"packed wrapper binary ({sliceName}) does not export _OBJC_CLASS_$_{probeClass} — force_load did not carry the " +
                        $"static ObjC class through the pack pipeline, so the dropped source leaves the class unregistered.");
            }
        }

        // (d) every consumer .targets references the wrapper behind an Exists() guard and the
        //     source ONLY as a wrapper-absent fallback (never unconditionally).
        AssertConsumerTargetsWrapperGuard(extract, module, MixedPackIosRid, failures);

        // (e) the Swift nupkg EMBEDS the companion's managed assembly under a version-qualified
        //     iOS lib/ TFM slice (lib/net*-ios<version>/) — single-package topology.
        AssertCompanionEmbeddedInLib(extract, module, "iOS", "ios", failures);

        // (f) and the embed is NOT also (or instead) declared as a nuspec <dependency>.
        AssertNuspecHasNoCompanionDependency(extract, "MixedPackFixture", module, failures);

        // (g) Defect J binding leg — the generic-bearing binding ships a trimmer descriptor
        //     under buildTransitive/ named after its own assembly, so the NativeAOT consumer
        //     this leg then publishes (--mixed-pack --device) actually roots the open generic's
        //     reflection metadata. This is the device-runtime complement to the static leg's
        //     hermetic host-only structural proof of the same descriptor delivery.
        AssertBindingDescriptorDelivered(extract, module, failures);

        // (h) Finding 55 — the packable SDK-driven binding ships its doc XML in lib/.
        AssertBindingDocFileDelivered(extract, module, failures);

        if (failures.Count > 0)
        {
            Log.Error("--mixed-pack structural check FAILED — {Count} defect(s) in {Nupkg}:",
                failures.Count, Path.GetFileName(swiftNupkg));
            foreach (var f in failures) Log.Error("  {Detail}", f);
            Assert.Fail($"--mixed-pack: {failures.Count} structural defect(s) in the iOS nupkg — see log.");
        }
        Log.Information("--mixed-pack structural OK — source dropped, wrapper device slice carries _OBJC_CLASS_$_{Probe}, companion embedded in lib/ios slice",
            probeClass);
    }

    // Single-PackageReference iOS consumer: a net10.0-ios app that takes ONE PackageReference
    // to the Swift binding and uses the ObjC type (the companion managed dll arrives EMBEDDED
    // in that package's lib/ and the wrapper native through its runtimes/ — both from the one
    // reference; the Runtime package's [ModuleInitializer] auto-registers the DllImportResolver,
    // so the consumer does NOT register it manually — the faithful packaged-consumer shape).
    // Builds for the iOS Simulator (Mono JIT) or NativeAOT-publishes for a device, deploys, and
    // asserts the ObjC greeting round-trips AND the loader emitted no duplicate-class warning.
    void RunMixedPackConsumer(
        AbsolutePath scratch, AbsolutePath nupkgDir, AbsolutePath fixtureOut,
        string module, string probeClass, bool onDevice)
    {
        var label = onDevice ? "device (NativeAOT)" : "simulator (Mono JIT)";
        var rid = onDevice ? "ios-arm64" : "iossimulator-arm64";
        var appDir = scratch / (onDevice ? "consumer-device" : "consumer-sim");
        if (Directory.Exists(appDir)) appDir.DeleteDirectory();
        appDir.CreateDirectory();

        WriteMixedPackConsumerApp(appDir, module, probeClass, rid);
        File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureOut));

        Log.Information("=== mixed-pack ({Label}): building single-PackageReference consumer ===", label);
        if (onDevice)
        {
            Log.Information("    NativeAOT publish — this may take several minutes (ILCompiler + code signing)...");
            DotNetPublish(s => s
                .SetProject(appDir / $"{MixedPackAppName}.csproj")
                .SetConfiguration("Release")
                .SetRuntime("ios-arm64")
                .SetVerbosity(DotNetVerbosity.quiet));
        }
        else
        {
            DotNetBuild(s => s
                .SetProjectFile(appDir / $"{MixedPackAppName}.csproj")
                .SetConfiguration("Debug")
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));
        }

        // Locate the produced .app bundle. The exact intermediate layout differs between a
        // sim build (bin/Debug/...) and a device publish (bin/Release/.../publish or not), so
        // search the bin tree for the bundle matching this config + rid.
        var config = onDevice ? "Release" : "Debug";
        var publishSegment = $"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}";
        var candidates = Directory
            .GetDirectories(appDir / "bin", $"{MixedPackAppName}.app", SearchOption.AllDirectories)
            .Where(d => d.Contains(config, StringComparison.Ordinal) && d.Contains(rid, StringComparison.Ordinal))
            // Deterministic selection: on device prefer the published bundle (the signed,
            // NativeAOT-compiled output) over a stray build-dir copy; then a stable path
            // tiebreak so the choice never depends on filesystem enumeration order.
            .OrderByDescending(d => onDevice && d.Contains(publishSegment, StringComparison.Ordinal))
            .ThenBy(d => d.Length)
            .ThenBy(d => d, StringComparer.Ordinal)
            .ToList();
        var appPath = candidates.FirstOrDefault()
            ?? throw new Exception($"--mixed-pack ({label}): {MixedPackAppName}.app bundle not found after build");
        if (candidates.Count > 1)
            Log.Warning("--mixed-pack ({Label}): {Count} matching {App}.app bundles (config={Config}, rid={Rid}); selected {Path}",
                label, candidates.Count, MixedPackAppName, config, rid, appPath);
        Log.Information("    app bundle: {Path}", appPath);

        Log.Information("=== mixed-pack ({Label}): deploying + launching consumer ===", label);

        // Resolve the deploy target once, then install+launch inside the retry loop below.
        Func<LaunchResult> deployAndLaunch;
        if (onDevice)
        {
            // Honor --device-udid (the shared DeviceUdid parameter) to pin a specific device,
            // mirroring the RuntimeTests device path; otherwise take the first connected one.
            var dev = !string.IsNullOrEmpty(DeviceUdid)
                ? new DeviceCtl.PhysicalDevice(DeviceUdid, "specified")
                : DeviceCtl.ListDevices().FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "--mixed-pack --device: no connected iOS device found. Connect an iPhone and try again, or pass --device-udid UDID.");
            Log.Information("    device: {Name} ({Udid})", dev.Name, dev.Udid);
            deployAndLaunch = () =>
            {
                DeviceCtl.Install(dev.Udid, appPath);
                return DeviceCtl.Launch(dev.Udid, MixedPackBundleId, Array.Empty<string>(), TimeSpan.FromSeconds(Timeout));
            };
        }
        else
        {
            var sim = !string.IsNullOrEmpty(DeviceUdid)
                ? new SimCtl.SimDevice(DeviceUdid, "pre-booted", "Booted", true, "")
                : SimCtl.EnsureBootedDevice();
            Log.Information("    simulator: {Name} ({Udid})", sim.Name, sim.Udid);
            deployAndLaunch = () =>
            {
                SimCtl.Install(sim.Udid, appPath);
                return SimCtl.Launch(sim.Udid, MixedPackBundleId, Array.Empty<string>(), TimeSpan.FromSeconds(Timeout), appName: MixedPackAppName);
            };
        }

        var result = LaunchUntilAppRuns(deployAndLaunch, $"--mixed-pack ({label})");

        Log.Information("");
        Log.Information("=== CONSUMER OUTPUT ({Label}) ===", label);
        Log.Information(result.Output);

        AssertMixedPackConsumerResult(result, label);
    }

    // Asserts the launched consumer (a) ran the ObjC type to completion through the single
    // PackageReference (TEST SUCCESS after RESULTS FLUSHED), (b) round-tripped the greeting,
    // and (c) registered the ObjC class exactly once — the loader's duplicate-class warning
    // ("Class X is implemented in both …") is the LOAD-TIME Gap 2 symptom and the whole reason
    // this iOS leg exists, since it cannot be observed from pack-time zip inspection.
    void AssertMixedPackConsumerResult(LaunchResult result, string label)
    {
        // (0) Launcher never started the app: report THAT, not a binding verdict. Every assertion
        //     below is about what the app printed, and this run has no app output to reason from —
        //     attributing it to the ObjC type would send the reader after a defect the evidence
        //     does not support.
        if (LaunchDiagnostics.LauncherNeverStartedApp(result))
            Assert.Fail(
                $"--mixed-pack ({label}): the app was deployed but the launcher never started it (retried " +
                $"{LaunchInfraMaxAttempts}×), so nothing evaluated the ObjC type — this is a deploy/launch failure, " +
                $"NOT a binding result.\nlauncher output:\n{result.Output}");

        // (c) single registration — check FIRST so a duplicate-class regression reports the
        //     precise Gap-2 cause even if the greeting also happened to print.
        if (result.Output.Contains("implemented in both", StringComparison.OrdinalIgnoreCase))
            Assert.Fail(
                $"--mixed-pack ({label}): the loader reported a duplicate ObjC class registration (Gap 2 regression) — the " +
                $"static source archive was embedded in the consumer in ADDITION to the force-loading wrapper.\noutput:\n{result.Output}");

        var expected = $"OBJC_GREETING:{PackGateMixedObjCGreeting}";
        if (!result.Output.Contains(expected, StringComparison.Ordinal))
            Assert.Fail(
                $"--mixed-pack ({label}): expected '{expected}' in output — the ObjC type was not usable through the single " +
                $"Swift-binding PackageReference.\noutput:\n{result.Output}");

        if (result.Result != TestResult.Success)
            Assert.Fail(
                $"--mixed-pack ({label}): consumer did not report TEST SUCCESS (result={result.Result}). The greeting may have " +
                $"printed but the app did not complete cleanly.\noutput:\n{result.Output}");

        Log.Information("--mixed-pack ({Label}) consumer-run OK — ObjC type usable through single PackageReference, class registered once",
            label);
    }

    // The iOS consumer csproj + Program.cs + Info.plist. Mirrors RuntimeTestsApp's iOS shape:
    // default sim RID with a device-RID PropertyGroup that flips on PublishAot + code signing.
    // Unlike RuntimeTestsApp (which ProjectReferences Swift.Runtime and must hand-include the
    // ILLink descriptor because buildTransitive does NOT flow across ProjectReference), this
    // consumer takes a PackageReference, so the Runtime package's buildTransitive descriptor
    // flows automatically — exercising the packaged NativeAOT wiring a real consumer relies on.
    static void WriteMixedPackConsumerApp(AbsolutePath appDir, string module, string probeClass, string rid)
    {
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-ios</TargetFramework>
                <RuntimeIdentifier>{rid}</RuntimeIdentifier>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <ApplicationId>{MixedPackBundleId}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
                <NoWarn>$(NoWarn);CA1416;CA1422</NoWarn>
              </PropertyGroup>

              <!-- Simulator (Mono JIT): no trimming. -->
              <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'iossimulator-arm64'">
                <MtouchLink>None</MtouchLink>
              </PropertyGroup>

              <!-- Device (NativeAOT): PublishAot + code signing (Justin's wildcard dev identity,
                   matching RuntimeTestsApp). -->
              <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'ios-arm64'">
                <PublishAot>true</PublishAot>
                <PublishAotUsingRuntimePack>true</PublishAotUsingRuntimePack>
                <CodesignKey>Apple Development: Justin Wojciechowski (KBKS29A36Q)</CodesignKey>
                <CodesignProvision>Wildcard Dev</CodesignProvision>
                <TeamIdentifierPrefix>TL2K6QUQEH</TeamIdentifierPrefix>
              </PropertyGroup>

              <!-- LibraryImport requires DisableRuntimeMarshalling for Swift interop types. -->
              <ItemGroup>
                <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
              </ItemGroup>

              <ItemGroup>
                <None Include="Info.plist" />
              </ItemGroup>

              <!-- THE single reference: the mixed Swift binding. Its lib/ carries the ObjC
                   companion dll and its runtimes/ carries the wrapper native; the transitive
                   SwiftBindings.Runtime + SwiftBindings.Apple deps come from the local feed. -->
              <ItemGroup>
                <PackageReference Include="MixedPackFixture.{module}" Version="{MixedPackVersion}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(appDir / $"{MixedPackAppName}.csproj", csproj);

        // Minimal UIKit app: bring up a window, then exercise the ObjC type and print the
        // markers the launch harness scrapes. Console.WriteLine reaches the simctl/devicectl
        // --console capture. Print RESULTS FLUSHED before the TEST marker so the launcher
        // (which waits for that marker) returns promptly instead of timing out.
        var program = $$"""
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            using CoreFoundation;
            using Foundation;
            using UIKit;

            namespace MixedPackApp;

            public static class Application
            {
                static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
            }

            public class AppDelegate : UIApplicationDelegate
            {
                public override UIWindow? Window { get; set; }

                public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions)
                {
                    Window = new UIWindow(UIScreen.MainScreen.Bounds);
                    Window.RootViewController = new UIViewController();
                    Window.MakeKeyAndVisible();

                    // Defer the probe so launch completes first, then run it on the main
                    // queue (object init + objc_msgSend through the binding).
                    DispatchQueue.MainQueue.DispatchAsync(RunProbe);
                    return true;
                }

                static void RunProbe()
                {
                    try
                    {
                        var probe = new global::{{module}}.{{probeClass}}();
                        Console.WriteLine("OBJC_GREETING:" + probe.Greeting());
                        Console.WriteLine("RESULTS FLUSHED");
                        Console.WriteLine("TEST SUCCESS");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("RESULTS FLUSHED");
                        Console.WriteLine("TEST FAILURE: " + ex);
                    }
                }
            }
            """;
        File.WriteAllText(appDir / "Program.cs", program);

        File.WriteAllText(appDir / "Info.plist",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n<dict>\n" +
            "    <key>UILaunchScreen</key>\n    <dict/>\n" +
            "</dict>\n</plist>\n");
    }

    // NuGet.config for the mixed-pack fixture + consumer. SwiftBindings.* resolve from the
    // throwaway feed; the single fixture package (MixedPackFixture.*) resolves from the
    // fixture-output feed when consuming (fixtureNupkgDir non-null).
    static string MixedPackNuGetConfig(AbsolutePath nupkgDir, AbsolutePath? fixtureNupkgDir)
    {
        var fixtureSource = fixtureNupkgDir is null
            ? ""
            : $"""
                    <add key="mixed-pack-fixture" value="{fixtureNupkgDir}" />
            """;
        var fixtureMapping = fixtureNupkgDir is null
            ? ""
            : $"""
                    <packageSource key="mixed-pack-fixture">
                      <package pattern="MixedPackFixture.*" />
                    </packageSource>
            """;
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="mixed-pack-local" value="{nupkgDir}" />
            {fixtureSource}
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="mixed-pack-local">
                  <package pattern="SwiftBindings.*" />
                </packageSource>
            {fixtureMapping}
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """;
    }
}
