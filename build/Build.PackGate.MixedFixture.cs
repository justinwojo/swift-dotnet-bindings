// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.PackGate.MixedFixture.cs — mixed-framework (ObjC + Swift) pack→consume legs
//
// Extends the PackGate gate with two purpose-built MIXED xcframework fixtures,
// built from source at gate time (no `nuke fetch`, no `.libraries/` dependency,
// always present on a macOS host). A mixed framework carries an ObjC class AND a
// Swift API, so the generator emits BOTH a Swift binding csproj and an ObjC
// companion csproj — exercising the pieces no prior gate covered end-to-end:
//
//   * Gap 1 Phase E/F — a mixed framework is ONE xcframework, so it ships as ONE
//     NuGet package. `dotnet pack` on the mixed binding produces a SINGLE nupkg
//     that EMBEDS the ObjC companion's managed assembly under lib/ (no separate
//     companion package, no nuspec <dependency>), and a consumer that takes a
//     SINGLE PackageReference to the Swift binding must restore + compile + link
//     + run + use the ObjC type — the embedded companion dll resolves at compile
//     time and the wrapper/source native carries the ObjC class at run time.
//
//   * Gap 2 single-registration — for a STATIC source the wrapper is the sole
//     carrier (it `-force_load`s the archive's ObjC class) and the source
//     xcframework is DROPPED from the consumer's references, so the ObjC class
//     is registered with the runtime exactly once. The "objc class implemented
//     in both ..." dyld warning is a LOAD-TIME symptom — it cannot be seen from
//     pack-time zip inspection — so the static leg launches the consumer on the
//     macOS host (the one platform CI can link+launch deterministically) and
//     asserts the warning is absent. iOS-sim/device launch coverage for the same
//     shape stays a manual `nuke binding-tests --sim --device` acceptance.
//
// Platform = macOS on purpose: it is the only target where the gate can do the
// full pack → consume → link → RUN without a simulator/device, which is exactly
// what makes the launch-time single-registration check automatable here. The
// Gap 2 force_load decision is keyed on native linkage, not platform, so a
// macOS static+mixed fixture exercises the same SwiftWrapperCompiler /
// NativePackagingPolicy path the reported iOS case does.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    // Host arch drives every triple/RID below. The fixtures are single-arch
    // (host-only): a one-slice xcframework is all the macOS consumer-run needs,
    // and it keeps the build sub-second.
    static string PackGateMixedArch =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x86_64" : "arm64";
    static string PackGateMixedRid =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "osx-x64" : "osx-arm64";
    static string PackGateMixedTriple => $"{PackGateMixedArch}-apple-macos11.0";
    static string PackGateMixedModuleSuffix => $"{PackGateMixedArch}-apple-macos";

    // Deterministic greeting returned by the ObjC probe class — the consumer-run
    // asserts it round-trips back through managed -> objc_msgSend -> managed.
    const string PackGateMixedObjCGreeting = "objc-mixed-ok";

    // Entry point for the PackGate target. Runs the static and dynamic mixed legs
    // in sequence; each packs, asserts nupkg structure, then consumes + links + RUNS.
    // Both reuse the same just-packed Runtime/SDK/Apple feed in nupkgDir.
    void RunPackGateMixedLegs(AbsolutePath scratch, AbsolutePath nupkgDir)
    {
        var mixedRoot = scratch / "mixed";
        mixedRoot.CreateDirectory();

        // Static leg — the load-bearing Gap 2 + Phase F gate. Packs, asserts the
        // dropped-source / sole-carrier structure on the real nupkg, then runs and
        // checks the ObjC class registers exactly once.
        RunPackGateMixedStaticLeg(mixedRoot, nupkgDir);

        // Dynamic leg — complements the static leg by proving the source-drop is
        // CONDITIONAL on static linkage: a dynamic source must be RETAINED in the
        // nupkg (the wrapper links it by install_name; it is not force-loaded). It
        // also consumes + links + RUNS, exercising the distinct dynamic load-time
        // linkage path the static leg's force_load run cannot.
        RunPackGateMixedDynamicLeg(mixedRoot, nupkgDir);

        // Multi-TFM leg — a mixed binding that multi-targets (one csproj,
        // <TargetFrameworks>net10.0-ios;net10.0-macos>, the SwiftBindings.Nuke shape).
        // Proves the single-package companion embed is per-TFM correct: each lib/<tfm>/
        // slice carries ITS OWN platform's ObjC companion (lib/net*-ios*/ → .ObjC.iOS,
        // lib/net*-macos*/ → .ObjC.macOS), never the wrong-platform assembly under another
        // slice. Pack is pure structure (no link/run), so this stays on the macOS host with
        // no simulator/device — packing the iOS slices cross-compiles cleanly here.
        RunPackGateMixedMultiTfmLeg(mixedRoot, nupkgDir);
    }

    // The platforms the multi-TFM mixed leg packs into ONE package. iOS + macOS are the
    // load-bearing pair; Mac Catalyst (UIKit-on-mac) and tvOS have distinct linkers and
    // runtimes, so the per-TFM companion-embed + single-package contract is proven on them
    // too (Gap #2). The runtimes/<rid>/native dir each platform's lib TFM resolves to comes
    // from ApplePlatform.NativeRid (host Rid, or the device slice id for sim-deployed
    // platforms) — derived from the model so there is no parallel RID list to drift.
    static readonly ApplePlatform[] PackGateMixedMultiTfmPlatforms =
    {
        ApplePlatform.IOS,
        ApplePlatform.MacOS,
        ApplePlatform.MacCatalyst,
        ApplePlatform.TvOS,
    };

    // ── Multi-TFM leg: one mixed binding, four platforms, one package ───────────
    // Locks in that a multi-targeted mixed binding embeds the CORRECT per-platform
    // ObjC companion in each lib/<tfm>/ slice — the regression a silent single-capture
    // companion path would introduce (the wrong-platform managed assembly under a slice).
    void RunPackGateMixedMultiTfmLeg(AbsolutePath mixedRoot, AbsolutePath nupkgDir)
    {
        const string module = "SbGap2MultiTfm";
        const string probeClass = "SbGap2MultiTfmProbe";

        Log.Information("=== PackGate (mixed/multi-tfm): building iOS(device+sim)+macOS+MacCatalyst+tvOS(device+sim) mixed xcframework ===");
        var legRoot = mixedRoot / "multitfm";
        if (Directory.Exists(legRoot)) legRoot.DeleteDirectory();
        legRoot.CreateDirectory();
        var xcfw = BuildPackGateMixedMultiTfmXcframework(legRoot / "build", module, probeClass);

        var fixtureDir = legRoot / "fixture";
        var fixtureOut = legRoot / "fixture-output";
        fixtureDir.CreateDirectory();
        fixtureOut.CreateDirectory();

        // ONE csproj, FOUR platforms — the multi-targeted mixed binding under test. The
        // version-suffixed TFM breaks single-TFM platform detection, so each entry is the
        // unsuffixed net10.0-<platform> (NuGet resolves each to its versioned lib/ slice).
        var tfms = string.Join(";", PackGateMixedMultiTfmPlatforms.Select(p => p.GetTfm()));
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{PackGateVersion}">
              <PropertyGroup>
                <TargetFramework />
                <TargetFrameworks>{tfms}</TargetFrameworks>
                <PackageId>PackGateMixedFixture.{module}</PackageId>
                <PackageVersion>{PackGateVersion}</PackageVersion>
                <IsPackable>true</IsPackable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416;CS8604</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftFramework Include="{xcfw}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(fixtureDir / $"{module}.csproj", csproj);
        File.WriteAllText(fixtureDir / "NuGet.config", PackGateMixedNuGetConfig(nupkgDir, fixtureNupkgDir: null));

        Log.Information("=== PackGate (mixed/multi-tfm): packing fixture ===");
        DotNetPack(s => s
            .SetProject(fixtureDir / $"{module}.csproj")
            .SetConfiguration("Release")
            .SetOutputDirectory(fixtureOut)
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        // Exactly ONE nupkg — the multi-TFM Swift binding. Assert the TOTAL count is one
        // rather than just "the expected one exists + no companion glob": a multi-TFM mixed
        // binding ships as ONE package with both platforms' companions embedded in lib/, so
        // ANY second nupkg — a leaked ObjC companion under any name, an extra package id — is a
        // single-package-contract regression, even one the two companion globs would miss.
        var swiftNupkg = fixtureOut / $"PackGateMixedFixture.{module}.{PackGateVersion}.nupkg";
        var producedNupkgs = Directory.EnumerateFiles(fixtureOut, "*.nupkg").Select(Path.GetFileName).OrderBy(x => x).ToList();
        if (!File.Exists(swiftNupkg))
            Assert.Fail(
                $"PackGate (mixed/multi-tfm): Swift binding nupkg not produced at {swiftNupkg}. " +
                $"Produced: {(producedNupkgs.Count == 0 ? "(none)" : string.Join(", ", producedNupkgs))}");
        if (producedNupkgs.Count != 1)
            Assert.Fail(
                $"PackGate (mixed/multi-tfm): expected exactly ONE nupkg (the multi-TFM Swift binding, with both " +
                $"platforms' ObjC companions embedded in lib/), but pack produced {producedNupkgs.Count}: " +
                $"{string.Join(", ", producedNupkgs)} — any second package (a leaked companion, an extra package id) " +
                $"breaks the single-package contract.");

        var extract = legRoot / "swift-nupkg";
        ExtractNupkg(swiftNupkg, extract);

        var failures = new List<string>();

        // (a/b) each platform's lib slice carries ONLY its own ObjC companion.
        //     AssertCompanionEmbeddedInLib also fails if the named companion appears more
        //     than once under lib/, so calling it per-platform proves isolation: the
        //     .ObjC.<platform> assembly lives ONLY in that platform's slice — no
        //     wrong-platform embed. (PackageSuffix names the dll, TfmSuffix the slice dir.)
        foreach (var p in PackGateMixedMultiTfmPlatforms)
            AssertCompanionEmbeddedInLib(extract, module, p.PackageSuffix, p.TfmSuffix, failures);

        // (c) belt-and-suspenders cross-isolation: for every ordered (own, foreign) pair,
        //     explicitly assert the own companion did NOT bleed into the foreign platform's
        //     slice (a future change could embed BOTH in each slice — that would pass the
        //     per-platform single-hit check above only if it also dropped the other, so name
        //     the exact forbidden placements here across the full N×(N-1) matrix).
        foreach (var own in PackGateMixedMultiTfmPlatforms)
            foreach (var foreign in PackGateMixedMultiTfmPlatforms)
                if (own.TfmSuffix != foreign.TfmSuffix)
                    AssertCompanionNotInForeignSlice(extract, $"{module}.ObjC.{own.PackageSuffix}.dll", foreignInfix: foreign.TfmSuffix, failures);

        // (d) per-RID native wrappers for EVERY platform ship in the one package; and because
        //     the fixture source is STATIC, the source xcframework is DROPPED from every RID —
        //     the wrapper force-loads the static archive and is the sole carrier (Gap 2), so the
        //     source must never ship alongside the wrapper (double-embed = duplicate ObjC-class
        //     registration at link/run). Assert both per RID, matching the static leg's rigor.
        foreach (var p in PackGateMixedMultiTfmPlatforms)
        {
            var rid = p.NativeRid;
            var nativeDir = extract / "runtimes" / rid / "native";
            if (!Directory.Exists(nativeDir / $"{module}SwiftBindings.xcframework"))
                failures.Add($"missing wrapper xcframework for {rid}: runtimes/{rid}/native/{module}SwiftBindings.xcframework/ — the multi-TFM package must ship native for every targeted platform.");
            if (Directory.Exists(nativeDir / $"{module}.xcframework"))
                failures.Add($"static source xcframework was packed for {rid}: runtimes/{rid}/native/{module}.xcframework/ — for static linkage the wrapper is the sole carrier; the source must be dropped (Gap 2 double-embed hazard).");
        }

        // (e) the companions are embedded, never promoted to nuspec <dependency> items.
        AssertNuspecHasNoCompanionDependency(extract, "PackGateMixedFixture", module, failures);

        if (failures.Count > 0)
        {
            Log.Error("PackGate (mixed/multi-tfm) FAILED — {Count} structural defect(s) in {Nupkg}:",
                failures.Count, Path.GetFileName(swiftNupkg));
            foreach (var f in failures) Log.Error("  {Detail}", f);
            Assert.Fail($"PackGate (mixed/multi-tfm): {failures.Count} structural defect(s) — see log.");
        }
        Log.Information("PackGate (mixed/multi-tfm) structural OK — exactly one package; per-platform .ObjC.<{Platforms}> companions each isolated to their own lib slice; wrapper-only native for every RID (static source dropped)",
            string.Join(",", PackGateMixedMultiTfmPlatforms.Select(p => p.PackageSuffix)));
    }

    // Builds a static MIXED xcframework spanning every platform in PackGateMixedMultiTfmPlatforms
    // (iOS device+sim, macOS, Mac Catalyst, tvOS device+sim) — enough for the multi-TFM binding to
    // pack all four. Reuses the shared per-slice recipe; slice parameters come from ApplePlatform so
    // the triples/sdks/min-OS/plist stay the single source of truth. create-xcframework keys each
    // slice off its Mach-O LC_BUILD_VERSION platform (driven by the triple), so the macabi and
    // macos-arm64 slices — both arm64 — slot into distinct platform buckets without collision.
    AbsolutePath BuildPackGateMixedMultiTfmXcframework(AbsolutePath buildRoot, string module, string probeClass)
    {
        if (Directory.Exists(buildRoot)) buildRoot.DeleteDirectory();
        buildRoot.CreateDirectory();
        var (probeM, libSwift) = WriteMixedFrameworkSources(buildRoot, module, probeClass);

        var slices = new List<AbsolutePath>();
        foreach (var p in PackGateMixedMultiTfmPlatforms)
        {
            if (p.HasDeviceSlice)
            {
                var deviceSlice = buildRoot / p.DeviceSliceId!;
                BuildMixedFrameworkSlice(deviceSlice, probeM, libSwift, module, probeClass, isStatic: true,
                    triple: p.DeviceTarget!, moduleSuffix: p.DeviceModuleSuffix!,
                    sdkName: p.DeviceSdkName!, minOs: p.MinOsVersion, plistPlatform: p.DevicePlistPlatform!);
                slices.Add(deviceSlice / $"{module}.framework");
            }

            var primarySlice = buildRoot / p.SimulatorSliceId;
            BuildMixedFrameworkSlice(primarySlice, probeM, libSwift, module, probeClass, isStatic: true,
                triple: p.SimulatorTarget, moduleSuffix: p.SimulatorModuleSuffix,
                sdkName: p.SimulatorSdkName, minOs: p.MinOsVersion, plistPlatform: p.SimulatorPlistPlatform);
            slices.Add(primarySlice / $"{module}.framework");
        }

        var xcframeworkPath = buildRoot / $"{module}.xcframework";
        if (Directory.Exists(xcframeworkPath)) xcframeworkPath.DeleteDirectory();
        var create = new CreateXcframeworkSettings().SetOutputPath(xcframeworkPath);
        foreach (var s in slices) create = create.AddFrameworkPath(s);
        XcodeBuild.ExecuteCreateXcframework(create);

        Log.Information("  built static+mixed iOS+macOS+MacCatalyst+tvOS xcframework ({Count} slices): {Path}",
            slices.Count, xcframeworkPath);
        return xcframeworkPath;
    }

    // Asserts a companion assembly does NOT appear under a foreign platform's lib/ slice
    // (e.g. the .ObjC.iOS dll must never land in a lib/net*-macos*/ folder). Complements
    // AssertCompanionEmbeddedInLib's "right slice + exactly once" check with an explicit
    // wrong-slice guard for the multi-TFM case, where two slices coexist in one package.
    static void AssertCompanionNotInForeignSlice(
        AbsolutePath extract, string companionDll, string foreignInfix, List<string> failures)
    {
        var lib = extract / "lib";
        if (!Directory.Exists(lib)) return;
        var foreign = Directory.EnumerateFiles(lib, companionDll, SearchOption.AllDirectories)
            .Where(p => System.Text.RegularExpressions.Regex.IsMatch(
                Path.GetFileName(Path.GetDirectoryName(p) ?? ""), $@"^net[0-9].*-{foreignInfix}[0-9]"))
            .Select(p => Path.GetFileName(Path.GetDirectoryName(p)))
            .ToList();
        if (foreign.Count > 0)
            failures.Add(
                $"companion {companionDll} is embedded under a foreign {foreignInfix} slice " +
                $"({string.Join(", ", foreign)}) — a multi-TFM mixed package must embed each platform's companion " +
                $"ONLY under its own lib/ slice; a wrong-platform managed assembly would TypeLoad/throw at runtime.");
    }

    // ── Static leg: Gap 2 single-registration + Phase F mixed pack→consume ──────
    void RunPackGateMixedStaticLeg(AbsolutePath mixedRoot, AbsolutePath nupkgDir)
    {
        const string module = "SbGap2Static";
        const string probeClass = "SbGap2StaticProbe";

        Log.Information("=== PackGate (mixed/static): building static+mixed xcframework ===");
        var legRoot = mixedRoot / "static";
        if (Directory.Exists(legRoot)) legRoot.DeleteDirectory();
        legRoot.CreateDirectory();
        var xcfw = BuildPackGateMixedXcframework(legRoot / "build", module, probeClass, isStatic: true);

        var fixtureDir = legRoot / "fixture";
        var fixtureOut = legRoot / "fixture-output";
        fixtureDir.CreateDirectory();
        fixtureOut.CreateDirectory();
        WritePackGateMixedFixture(fixtureDir, nupkgDir, module, xcfw);

        Log.Information("=== PackGate (mixed/static): packing fixture ===");
        DotNetPack(s => s
            .SetProject(fixtureDir / $"{module}.csproj")
            .SetConfiguration("Release")
            .SetOutputDirectory(fixtureOut)
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        // Exactly ONE nupkg: the Swift binding (the companion is embedded inside it,
        // not packed separately). Assert the Swift nupkg exists and NO standalone
        // companion nupkg was produced.
        var swiftNupkg = fixtureOut / $"PackGateMixedFixture.{module}.{PackGateVersion}.nupkg";
        if (!File.Exists(swiftNupkg))
            Assert.Fail($"PackGate (mixed/static): Swift binding nupkg not produced at {swiftNupkg}");
        AssertNoSeparateCompanionNupkg(fixtureOut, module, "static");

        // Structural assertions on the REAL packed Swift nupkg.
        var extract = legRoot / "swift-nupkg";
        ExtractNupkg(swiftNupkg, extract);

        var failures = new List<string>();
        var native = extract / "runtimes" / PackGateMixedRid / "native";
        var sourceXcfw = native / $"{module}.xcframework";
        var wrapperXcfw = native / $"{module}SwiftBindings.xcframework";

        // (a) STATIC source is dropped — the wrapper is the sole native carrier.
        if (Directory.Exists(sourceXcfw))
            failures.Add(
                $"static source xcframework was packed (Gap 2 double-embed hazard): " +
                $"runtimes/{PackGateMixedRid}/native/{module}.xcframework/ — for static linkage the " +
                $"wrapper force-loads the archive and is the sole carrier; the source must be dropped.");

        // (b) the wrapper IS shipped and (c) it actually carries the force-loaded ObjC class.
        if (!Directory.Exists(wrapperXcfw))
        {
            failures.Add($"missing wrapper xcframework: runtimes/{PackGateMixedRid}/native/{module}SwiftBindings.xcframework/");
        }
        else
        {
            var wrapperBinary = Directory
                .EnumerateFiles(wrapperXcfw, $"{module}SwiftBindings", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.EndsWith(".plist", StringComparison.Ordinal));
            if (wrapperBinary is null)
            {
                failures.Add($"wrapper binary {module}SwiftBindings not found inside the packed wrapper xcframework");
            }
            else if (!NmDefinedGlobals(wrapperBinary).Contains($"_OBJC_CLASS_$_{probeClass}", StringComparison.Ordinal))
            {
                failures.Add(
                    $"packed wrapper binary does not export _OBJC_CLASS_$_{probeClass} — force_load did not carry the " +
                    $"static ObjC class through the pack pipeline, so the dropped source leaves the class unregistered.");
            }
        }

        // (d) every consumer .targets references the wrapper behind an Exists() guard
        //     and the source ONLY as a wrapper-absent fallback (never unconditionally).
        AssertConsumerTargetsWrapperGuard(extract, module, PackGateMixedRid, failures);

        // (e) the Swift nupkg EMBEDS the companion's managed assembly under lib/
        //     (single-package topology — no separate package, no nuspec dependency).
        AssertCompanionEmbeddedInLib(extract, module, "macOS", "macos", failures);

        // (f) and the embed is NOT also (or instead) declared as a nuspec <dependency>.
        AssertNuspecHasNoCompanionDependency(extract, "PackGateMixedFixture", module, failures);

        if (failures.Count > 0)
        {
            Log.Error("PackGate (mixed/static) FAILED — {Count} structural defect(s) in {Nupkg}:",
                failures.Count, Path.GetFileName(swiftNupkg));
            foreach (var f in failures) Log.Error("  {Detail}", f);
            Assert.Fail($"PackGate (mixed/static): {failures.Count} structural defect(s) — see log.");
        }
        Log.Information("PackGate (mixed/static) structural OK — source dropped, wrapper carries _OBJC_CLASS_$_{Probe}, companion dll embedded in lib/",
            probeClass);

        // End-to-end: single PackageReference consumer that LINKS + RUNS on the
        // host and exercises the ObjC type. The class must register exactly once
        // (only the wrapper defines it) — assert the dyld duplicate-class warning
        // is absent. This is the only place the load-time Gap 2 symptom is
        // observable in CI.
        RunPackGateMixedConsumer(legRoot, nupkgDir, fixtureOut, module, probeClass, assertSingleRegistration: true);
    }

    // ── Dynamic leg: source RETAINED, then consume + link + run ────────────────
    void RunPackGateMixedDynamicLeg(AbsolutePath mixedRoot, AbsolutePath nupkgDir)
    {
        const string module = "SbGap2Dynamic";
        const string probeClass = "SbGap2DynamicProbe";

        Log.Information("=== PackGate (mixed/dynamic): building dynamic+mixed xcframework ===");
        var legRoot = mixedRoot / "dynamic";
        if (Directory.Exists(legRoot)) legRoot.DeleteDirectory();
        legRoot.CreateDirectory();
        var xcfw = BuildPackGateMixedXcframework(legRoot / "build", module, probeClass, isStatic: false);

        var fixtureDir = legRoot / "fixture";
        var fixtureOut = legRoot / "fixture-output";
        fixtureDir.CreateDirectory();
        fixtureOut.CreateDirectory();
        WritePackGateMixedFixture(fixtureDir, nupkgDir, module, xcfw);

        Log.Information("=== PackGate (mixed/dynamic): packing fixture ===");
        DotNetPack(s => s
            .SetProject(fixtureDir / $"{module}.csproj")
            .SetConfiguration("Release")
            .SetOutputDirectory(fixtureOut)
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        var swiftNupkg = fixtureOut / $"PackGateMixedFixture.{module}.{PackGateVersion}.nupkg";
        if (!File.Exists(swiftNupkg))
            Assert.Fail($"PackGate (mixed/dynamic): Swift binding nupkg not produced at {swiftNupkg}");
        AssertNoSeparateCompanionNupkg(fixtureOut, module, "dynamic");

        var extract = legRoot / "swift-nupkg";
        ExtractNupkg(swiftNupkg, extract);

        var failures = new List<string>();
        var native = extract / "runtimes" / PackGateMixedRid / "native";

        // For DYNAMIC linkage the source dylib xcframework MUST be retained — the
        // wrapper links it by install_name at load time, so dropping it would
        // DllNotFound at runtime. This is the deliberate complement to the static
        // leg's drop assertion: it proves the policy is linkage-conditional.
        if (!Directory.Exists(native / $"{module}.xcframework"))
            failures.Add(
                $"dynamic source xcframework was dropped: runtimes/{PackGateMixedRid}/native/{module}.xcframework/ is " +
                $"missing — only STATIC sources are dropped (wrapper sole-carrier); a dynamic source must ship so the " +
                $"wrapper can link it by install_name.");
        if (!Directory.Exists(native / $"{module}SwiftBindings.xcframework"))
            failures.Add($"missing wrapper xcframework: runtimes/{PackGateMixedRid}/native/{module}SwiftBindings.xcframework/");

        AssertCompanionEmbeddedInLib(extract, module, "macOS", "macos", failures);
        AssertNuspecHasNoCompanionDependency(extract, "PackGateMixedFixture", module, failures);

        if (failures.Count > 0)
        {
            Log.Error("PackGate (mixed/dynamic) FAILED — {Count} structural defect(s) in {Nupkg}:",
                failures.Count, Path.GetFileName(swiftNupkg));
            foreach (var f in failures) Log.Error("  {Detail}", f);
            Assert.Fail($"PackGate (mixed/dynamic): {failures.Count} structural defect(s) — see log.");
        }
        Log.Information("PackGate (mixed/dynamic) structural OK — dynamic source retained, wrapper shipped, companion dll embedded in lib/");

        // End-to-end (the complement to the static leg's run): a single-PackageReference
        // consumer that LINKS + RUNS the dynamic-source mixed binding and exercises the ObjC
        // type. This proves the RETAINED source dylib resolves at load time (install_name /
        // @rpath) alongside the wrapper through one PackageReference — a distinct linkage path
        // from the static leg's force-loaded archive, which the structural assertions above
        // cannot exercise. No single-registration assertion: a dynamic source legitimately
        // ships its own image, and the ObjC class is defined only there (the wrapper links it,
        // it does not force_load it), so there is no duplicate-class warning to police.
        RunPackGateMixedConsumer(legRoot, nupkgDir, fixtureOut, module, probeClass, assertSingleRegistration: false);
    }

    // Builds a single-slice macOS MIXED xcframework from source. Thin wrapper over
    // the shared per-slice recipe (BuildMixedFrameworkSlice) — the iOS mixed-pack
    // leg (Build.BindingTests.MixedPack.cs) reuses the same recipe to assemble a
    // multi-slice (device + simulator) iOS xcframework.
    AbsolutePath BuildPackGateMixedXcframework(
        AbsolutePath buildRoot, string module, string probeClass, bool isStatic)
    {
        if (Directory.Exists(buildRoot)) buildRoot.DeleteDirectory();
        buildRoot.CreateDirectory();
        var (probeM, libSwift) = WriteMixedFrameworkSources(buildRoot, module, probeClass);

        var sliceDir = buildRoot / $"macos-{PackGateMixedArch}";
        BuildMixedFrameworkSlice(
            sliceDir, probeM, libSwift, module, probeClass, isStatic,
            triple: PackGateMixedTriple, moduleSuffix: PackGateMixedModuleSuffix,
            sdkName: "macosx", minOs: "11.0", plistPlatform: "MacOSX");

        var xcframeworkPath = buildRoot / $"{module}.xcframework";
        if (Directory.Exists(xcframeworkPath)) xcframeworkPath.DeleteDirectory();
        XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings()
            .AddFrameworkPath(sliceDir / $"{module}.framework")
            .SetOutputPath(xcframeworkPath));

        Log.Information("  built {Linkage}+mixed xcframework: {Path}", isStatic ? "static" : "dynamic", xcframeworkPath);
        return xcframeworkPath;
    }

    // Writes the shared MIXED-framework sources once into buildRoot: an ObjC class
    // (the unreferenced probe, with a unique name so no SDK/Foundation symbol can
    // satisfy the force_load assertion by accident) and a small independent Swift
    // API (so the framework is classified Mixed — both an ObjC and a Swift surface).
    // The same two source files compile against every slice's triple/SDK.
    static (AbsolutePath ProbeM, AbsolutePath LibSwift) WriteMixedFrameworkSources(
        AbsolutePath buildRoot, string module, string probeClass)
    {
        var probeM = buildRoot / "probe.m";
        File.WriteAllText(probeM,
            "#import <Foundation/Foundation.h>\n" +
            $"@interface {probeClass} : NSObject\n- (NSString *)greeting;\n@end\n" +
            $"@implementation {probeClass}\n- (NSString *)greeting {{ return @\"{PackGateMixedObjCGreeting}\"; }}\n@end\n");

        var libSwift = buildRoot / "Lib.swift";
        File.WriteAllText(libSwift,
            $"public func {module}Marker() -> Int32 {{ return 1 }}\n" +
            $"public struct {module}Value {{ public let n: Int32; public init(n: Int32) {{ self.n = n }} }}\n");

        return (probeM, libSwift);
    }

    // Builds ONE framework slice (static `ar` archive or dynamic Mach-O dylib) for a
    // mixed framework into {sliceDir}/{module}.framework: an umbrella header +
    // modulemap (the non-Swift header is what flips framework-type detection to
    // Mixed), a library-evolution .swiftmodule/.swiftinterface, an .abi.json, the
    // binary, and an Info.plist. Mirrors the toolchain recipe in
    // SwiftWrapperForceLoadSymbolExportTests + CompileModuleSlice. Scratch object
    // files live under sliceDir so building multiple slices never clobbers them.
    void BuildMixedFrameworkSlice(
        AbsolutePath sliceDir, AbsolutePath probeM, AbsolutePath libSwift,
        string module, string probeClass, bool isStatic,
        string triple, string moduleSuffix, string sdkName, string minOs, string plistPlatform)
    {
        sliceDir.CreateDirectory();
        var frameworkDir = sliceDir / $"{module}.framework";
        var modDir = frameworkDir / "Modules" / $"{module}.swiftmodule";
        var hdrDir = frameworkDir / "Headers";
        modDir.CreateDirectory();
        hdrDir.CreateDirectory();

        var sdkPath = XcRun.GetSdkPath(sdkName);

        File.WriteAllText(hdrDir / $"{module}.h",
            "#import <Foundation/Foundation.h>\n" +
            $"@interface {probeClass} : NSObject\n- (NSString *)greeting;\n@end\n");

        File.WriteAllText(frameworkDir / "Modules" / "module.modulemap",
            $"framework module {module} {{\n" +
            $"    umbrella header \"{module}.h\"\n" +
            "    export *\n" +
            "    module * { export * }\n" +
            "}\n");

        var probeO = sliceDir / "probe.o";
        RunPackGateTool("clang", new[]
        {
            "-x", "objective-c", "-c", "-target", triple,
            "-isysroot", sdkPath.ToString(), "-fobjc-arc",
            probeM.ToString(), "-o", probeO.ToString(),
        });

        var binaryPath = frameworkDir / module;
        var swiftModulePath = modDir / $"{moduleSuffix}.swiftmodule";
        var swiftInterfacePath = modDir / $"{moduleSuffix}.swiftinterface";

        if (isStatic)
        {
            // Static: compile the Swift to an object + module/interface, then `ar`
            // the Swift and ObjC objects into the framework binary (an archive).
            var libO = sliceDir / "lib.o";
            SwiftCompiler.Execute(new SwiftCompilerSettings()
                .SetTarget(triple)
                .SetSdk(sdkPath)
                .SetModuleName(module)
                .SetEmitModule()
                .SetEnableLibraryEvolution()
                .SetEmitModuleInterface()
                .SetOutputPath(libO)
                .SetModulePath(swiftModulePath)
                .SetModuleInterfacePath(swiftInterfacePath)
                .AddExtraArgument("-parse-as-library")
                .AddExtraArgument("-emit-object")
                .AddSourceFile(libSwift));

            RunPackGateTool("ar", new[] { "rcs", binaryPath.ToString(), libO.ToString(), probeO.ToString() });
        }
        else
        {
            // Dynamic: link the Swift source + ObjC object into a dylib in one
            // swiftc pass, emitting the module/interface alongside. Foundation must
            // be linked explicitly: Lib.swift never `import`s it, so swiftc won't
            // auto-link it, and the ObjC object's @"…" string literal needs
            // ___CFConstantStringClassReference (CoreFoundation, reexported by
            // Foundation) resolved at link time.
            SwiftCompiler.Execute(new SwiftCompilerSettings()
                .SetTarget(triple)
                .SetSdk(sdkPath)
                .SetModuleName(module)
                .SetEmitLibrary()
                .SetEmitModule()
                .SetEnableLibraryEvolution()
                .SetEmitModuleInterface()
                .SetOutputPath(binaryPath)
                .SetModulePath(swiftModulePath)
                .SetModuleInterfacePath(swiftInterfacePath)
                .SetInstallName($"@rpath/{module}.framework/{module}")
                .AddExtraArgument("-framework").AddExtraArgument("Foundation")
                .AddSourceFile(libSwift)
                .AddSourceFile(probeO));
        }

        // Some resolvers prefer a private interface alongside the public one.
        File.Copy(swiftInterfacePath, modDir / $"{moduleSuffix}.private.swiftinterface", overwrite: true);

        // ABI JSON from the .swiftinterface.
        SwiftFrontend.Execute(new SwiftFrontendSettings()
            .SetSwiftInterfacePath(swiftInterfacePath)
            .SetTarget(triple)
            .SetModuleName(module)
            .SetSdk(sdkPath)
            .SetAbiDescriptorPath(modDir / $"{moduleSuffix}.abi.json"));

        PlistGenerator.WriteFrameworkPlist(
            frameworkDir / "Info.plist",
            bundleId: $"com.swiftbindings.packgate.{module}",
            bundleName: module,
            executableName: module,
            minOs: minOs,
            plistPlatform: plistPlatform);
    }

    // Mixed binding fixture: SwiftBindings.Sdk drives generate -> compile wrapper
    // -> build the ObjC companion and EMBED its managed dll into the single Swift
    // nupkg's lib/. Single unsuffixed net10.0-macos TFM (the version-suffixed form
    // breaks single-TFM platform detection — see PackGateConsumerTfm commentary).
    static void WritePackGateMixedFixture(
        AbsolutePath fixtureDir, AbsolutePath nupkgDir, string module, AbsolutePath xcfwPath)
    {
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{PackGateVersion}">
              <PropertyGroup>
                <TargetFramework>net10.0-macos</TargetFramework>
                <PackageId>PackGateMixedFixture.{module}</PackageId>
                <PackageVersion>{PackGateVersion}</PackageVersion>
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
        File.WriteAllText(fixtureDir / "NuGet.config", PackGateMixedNuGetConfig(nupkgDir, fixtureNupkgDir: null));
    }

    // Single-PackageReference consumer: a net10.0-macos console app that takes ONE
    // PackageReference to the Swift binding and uses the ObjC type (the companion
    // managed dll arrives EMBEDDED in that package's lib/ and the wrapper native
    // through its runtimes/ — both from the one reference). Builds with a
    // RuntimeIdentifier (full native link), launches on the host, and asserts the
    // ObjC greeting round-trips. When assertSingleRegistration is set, also asserts
    // dyld emitted no duplicate-class ("implemented in both") warning — the
    // load-time Gap 2 symptom.
    void RunPackGateMixedConsumer(
        AbsolutePath legRoot, AbsolutePath nupkgDir, AbsolutePath fixtureOut,
        string module, string probeClass, bool assertSingleRegistration)
    {
        var appDir = legRoot / "consumer";
        if (Directory.Exists(appDir)) appDir.DeleteDirectory();
        appDir.CreateDirectory();

        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0-macos</TargetFramework>
                <RuntimeIdentifier>{PackGateMixedRid}</RuntimeIdentifier>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>
                <ApplicationId>com.swiftbindings.packgate.{module.ToLowerInvariant()}</ApplicationId>
                <NoWarn>$(NoWarn);CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="PackGateMixedFixture.{module}" Version="{PackGateVersion}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(appDir / "PackGateMixedApp.csproj", csproj);

        // Use the ObjC type through the single Swift-binding PackageReference. The
        // allocation forces the wrapper image to load and the runtime to register
        // the class; the method call exercises objc_msgSend end-to-end.
        var program = $$"""
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            var probe = new global::{{module}}.{{probeClass}}();
            Console.WriteLine("OBJC_GREETING:" + probe.Greeting());
            """;
        File.WriteAllText(appDir / "Program.cs", program);
        File.WriteAllText(appDir / "NuGet.config", PackGateMixedNuGetConfig(nupkgDir, fixtureOut));

        Log.Information("=== PackGate (mixed/{Module}): building single-PackageReference consumer ===", module);
        DotNetBuild(s => s
            .SetProjectFile(appDir / "PackGateMixedApp.csproj")
            .SetConfiguration("Release")
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        var appExe = appDir / "bin" / "Release" / "net10.0-macos" / PackGateMixedRid /
            "PackGateMixedApp.app" / "Contents" / "MacOS" / "PackGateMixedApp";
        if (!File.Exists(appExe))
            Assert.Fail($"PackGate (mixed/{module}): consumer app binary not produced at {appExe}");

        Log.Information("=== PackGate (mixed/{Module}): launching consumer ===", module);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = appExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = appDir,
        };
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception($"Failed to launch consumer at {appExe}");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            Assert.Fail(
                $"PackGate (mixed/{module}): consumer exited with code {proc.ExitCode}.\n" +
                $"stdout:\n{stdout}\nstderr:\n{stderr}");

        var expected = $"OBJC_GREETING:{PackGateMixedObjCGreeting}";
        if (!stdout.Contains(expected, StringComparison.Ordinal))
            Assert.Fail(
                $"PackGate (mixed/{module}): expected '{expected}' in stdout — the ObjC type was not usable through " +
                $"the single Swift-binding PackageReference.\nstdout:\n{stdout}\nstderr:\n{stderr}");

        if (assertSingleRegistration)
        {
            // dyld/objc prints "Class X is implemented in both ... One of the two
            // will be used. Which one is undefined." to stderr when a class is
            // registered from two loaded images. For a correctly-dropped static
            // source only the wrapper defines the class, so this must be absent.
            if (stderr.Contains("implemented in both", StringComparison.OrdinalIgnoreCase))
                Assert.Fail(
                    $"PackGate (mixed/static): dyld reported a duplicate ObjC class registration (Gap 2 regression) — " +
                    $"the source archive was embedded in the consumer in ADDITION to the wrapper.\nstderr:\n{stderr}");
            Log.Information("PackGate (mixed/static) consumer-run OK — ObjC type usable, class registered once (no duplicate-class warning)");
        }
        else
        {
            Log.Information("PackGate (mixed/{Module}) consumer-run OK — ObjC type usable through single PackageReference", module);
        }
    }

    // ── shared helpers ─────────────────────────────────────────────────────────

    static string PackGateMixedNuGetConfig(AbsolutePath nupkgDir, AbsolutePath? fixtureNupkgDir)
    {
        // One package only: the Swift binding "PackGateMixedFixture.{module}" (the
        // companion's managed dll is embedded inside it, never a separate package).
        // Map that single prefix to the local fixture feed when consuming.
        var fixtureSource = fixtureNupkgDir is null
            ? ""
            : $"""
                    <add key="pack-gate-mixed" value="{fixtureNupkgDir}" />
            """;
        var fixtureMapping = fixtureNupkgDir is null
            ? ""
            : $"""
                    <packageSource key="pack-gate-mixed">
                      <package pattern="PackGateMixedFixture.*" />
                    </packageSource>
            """;
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="pack-gate-local" value="{nupkgDir}" />
            {fixtureSource}
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="pack-gate-local">
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

    // Asserts NO standalone companion nupkg exists for the module: a mixed
    // framework is one xcframework -> one package, with the companion embedded in
    // the Swift binding's lib/. A separate {module}.ObjC.macOS.*.nupkg would mean
    // the companion leaked out as its own package (IsPackable regression).
    static void AssertNoSeparateCompanionNupkg(AbsolutePath fixtureOut, string module, string legName)
    {
        var matches = Directory
            .EnumerateFiles(fixtureOut, $"{module}.ObjC.macOS.*.nupkg")
            .ToList();
        if (matches.Count > 0)
            Assert.Fail(
                $"PackGate (mixed/{legName}): a standalone ObjC companion nupkg was produced " +
                $"({string.Join(", ", matches.Select(Path.GetFileName))}) — the companion must be EMBEDDED in the Swift " +
                $"binding's lib/, not packed as a separate package. Check the companion csproj keeps IsPackable=false.");
    }

    // Asserts the Swift binding's nupkg embeds the companion's managed assembly
    // under lib/ (single-package topology: the ObjC surface ships inside the Swift
    // binding's package, so one PackageReference compiles + links against both).
    // packageSuffix is the companion's TFM PackageSuffix ("macOS"/"iOS"); tfmInfix
    // is the lib/ TFM-slice platform infix ("macos"/"ios") — the two differ in
    // casing because the dll name follows ApplePlatform.PackageSuffix while the
    // NuGet TFM slice folder is lowercased.
    static void AssertCompanionEmbeddedInLib(
        AbsolutePath extract, string module, string packageSuffix, string tfmInfix, List<string> failures)
    {
        var lib = extract / "lib";
        var companionDll = $"{module}.ObjC.{packageSuffix}.dll";
        var hits = Directory.Exists(lib)
            ? Directory.EnumerateFiles(lib, companionDll, SearchOption.AllDirectories).ToList()
            : new List<string>();
        if (hits.Count == 0)
        {
            failures.Add(
                $"Swift binding nupkg does not embed the companion assembly lib/**/{companionDll} — the mixed binding's " +
                $"single package omits the ObjC managed surface, so a single PackageReference would not expose the ObjC " +
                $"type at compile time.");
            return;
        }
        if (hits.Count > 1)
        {
            failures.Add(
                $"companion assembly {companionDll} appears {hits.Count}× under lib/ " +
                $"({string.Join(", ", hits.Select(h => Path.GetFileName(Path.GetDirectoryName(h))))}) — expected exactly one, " +
                $"in the same TFM slice as the Swift binding's own managed dll.");
            return;
        }
        // The single hit must live in a version-qualified Apple TFM slice (e.g. lib/net10.0-macos26.0/,
        // lib/net10.0-ios18.0/) — the same lib/ shape NuGet stages the Swift binding's own managed dll
        // under. A companion dropped at lib/ root, under a bare net10.0 folder, or under a
        // platform-version-LESS net10.0-<platform> folder would not be resolved by the consumer's TFM.
        // The fixture targets the unsuffixed net10.0-<platform>, which NuGet resolves to
        // net10.0-<platform><version> in lib/, so the slice MUST carry that platform version: require a
        // digit immediately after both "net" and "-<platform>" (net<n>...-<platform><n>...).
        var slice = Path.GetFileName(Path.GetDirectoryName(hits[0]) ?? "");
        if (!System.Text.RegularExpressions.Regex.IsMatch(slice, $@"^net[0-9].*-{tfmInfix}[0-9]"))
            failures.Add(
                $"companion {companionDll} is embedded under lib/{slice}/ — expected a version-qualified {tfmInfix} TFM slice " +
                $"(lib/net*-{tfmInfix}<version>/, e.g. lib/net10.0-{tfmInfix}26.0/) matching the Swift binding's lib/ layout. A bare " +
                $"'net10.0-{tfmInfix}' (no platform version) or lib/ root drop is not resolvable by the consumer's TFM.");
    }

    // Asserts the consumer .targets reference the wrapper behind Exists() and the
    // source xcframework ONLY as a wrapper-absent fallback — never unconditionally.
    static void AssertConsumerTargetsWrapperGuard(
        AbsolutePath extract, string module, string rid, List<string> failures)
    {
        var targetsFiles = new[] { extract / "buildTransitive", extract / "build" }
            .Where(d => Directory.Exists(d))
            .SelectMany(d => Directory.EnumerateFiles(d, "*.targets", SearchOption.AllDirectories))
            .ToList();
        var sourceToken = $"{module}.xcframework";
        var wrapperToken = $"{module}SwiftBindings.xcframework";
        var withRef = targetsFiles
            .Where(f => File.ReadAllText(f).Contains(sourceToken, StringComparison.Ordinal))
            .ToList();
        if (withRef.Count == 0)
        {
            failures.Add($"no consumer .targets references {sourceToken} under buildTransitive/ — the static source's wrapper-absent fallback reference is missing.");
            return;
        }
        // Match each opening <NativeReference ...> tag (Include + Condition span lines, so consume
        // up to the first '>'). We must verify the SOURCE reference's OWN Condition gates it behind
        // !Exists(wrapper) — a coarse "the file mentions Exists() somewhere" check would green-light
        // an UNCONDITIONAL source ref that happens to sit beside an unrelated guard, which is exactly
        // the Gap-2 double-registration regression this gate exists to catch.
        var nativeRefTag = new System.Text.RegularExpressions.Regex(@"<NativeReference\b[^>]*>");
        foreach (var f in withRef)
        {
            var text = File.ReadAllText(f);
            if (!text.Contains(wrapperToken, StringComparison.Ordinal))
            {
                failures.Add($"{Path.GetFileName(f)} references {sourceToken} but never the wrapper {wrapperToken} — the source is not guarded against the wrapper carrying the same ObjC class.");
                continue;
            }
            var sawSourceRef = false;
            foreach (System.Text.RegularExpressions.Match m in nativeRefTag.Matches(text))
            {
                var tag = m.Value;
                // The source Include ends with "{module}.xcframework" + closing quote; the wrapper
                // Include ends with "{module}SwiftBindings.xcframework" + quote, so `{module}.xcframework"`
                // is NOT a substring of the wrapper tag's Include — this uniquely selects the source ref.
                var isSourceRef = tag.Contains($"{sourceToken}\"", StringComparison.Ordinal);
                if (!isSourceRef) continue;
                sawSourceRef = true;
                // Its Condition must name the wrapper inside a !Exists(...) — i.e. "resolve the source
                // ONLY when the wrapper is absent." When the wrapper is present it force-loaded the static
                // archive, so the source staying inert keeps the ObjC class registered exactly once.
                var gated = tag.Contains("!Exists(", StringComparison.Ordinal)
                            && tag.Contains(wrapperToken, StringComparison.Ordinal);
                if (!gated)
                    failures.Add(
                        $"{Path.GetFileName(f)}: the {sourceToken} NativeReference is not gated behind " +
                        $"!Exists(...{wrapperToken}) — a static source must resolve only as a wrapper-absent " +
                        $"fallback, else it double-registers the ObjC class. Offending element: {tag}");
            }
            if (!sawSourceRef)
                failures.Add(
                    $"{Path.GetFileName(f)} mentions {sourceToken} but no <NativeReference Include=\"...{sourceToken}\"> " +
                    $"element was found to verify its wrapper guard.");
        }
    }

    // Asserts the Swift binding's nuspec does NOT declare the ObjC companion as a package
    // <dependency>. The whole point of the single-package topology is that the companion is
    // EMBEDDED in lib/ (AssertCompanionEmbeddedInLib), not promoted to a transitive dependency
    // a consumer would have to resolve from a feed. A stray companion dependency here is the
    // failure mode the "ONE xcframework → ONE package" decision exists to prevent.
    static void AssertNuspecHasNoCompanionDependency(AbsolutePath extract, string fixturePrefix, string module, List<string> failures)
    {
        var nuspec = extract / $"{fixturePrefix}.{module}.nuspec";
        if (!File.Exists(nuspec))
        {
            failures.Add(
                $"Swift binding nuspec not found at {Path.GetFileName(nuspec)} — cannot verify the companion is embedded " +
                $"rather than declared as a dependency.");
            return;
        }
        var text = File.ReadAllText(nuspec);
        var depsStart = text.IndexOf("<dependencies", StringComparison.Ordinal);
        if (depsStart < 0) return; // no dependencies block at all → certainly no companion dependency
        var depsEnd = text.IndexOf("</dependencies>", depsStart, StringComparison.Ordinal);
        var deps = depsEnd > depsStart ? text.Substring(depsStart, depsEnd - depsStart) : text.Substring(depsStart);
        // Legitimate deps (SwiftBindings.Runtime, SwiftBindings.Apple) never contain ".ObjC"; the
        // companion id pattern does ({module}.ObjC.macOS). Match case-insensitively — NuGet package
        // ids are case-insensitive, so a companion declared as lowercase ".objc" is the same
        // forbidden dependency and must not slip past this guard.
        if (deps.Contains(".ObjC", StringComparison.OrdinalIgnoreCase))
            failures.Add(
                $"Swift binding nuspec declares an ObjC companion as a package <dependency> — the companion must be " +
                $"EMBEDDED in lib/, never promoted to a nuspec dependency. Offending <dependencies> block:\n{deps}");
    }

    static void ExtractNupkg(AbsolutePath nupkg, AbsolutePath dest)
    {
        if (Directory.Exists(dest)) dest.DeleteDirectory();
        dest.CreateDirectory();
        ZipFile.ExtractToDirectory(nupkg, dest);
    }

    static string NmDefinedGlobals(string binary)
    {
        var output = ProcessTasks.StartProcess("nm", ArgumentEscaper.Join(new[] { "-gU", binary }), logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode()
            .Output.StdToText();
        return output;
    }

    static void RunPackGateTool(string tool, IReadOnlyList<string> args)
    {
        ProcessTasks.StartProcess(XcRun.FindTool(tool), ArgumentEscaper.Join(args), logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode();
    }
}
