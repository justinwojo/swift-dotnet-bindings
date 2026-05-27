// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.X64PackGate.cs — x86_64 packaging + Rosetta runtime gate (Layer 1)
//
// The Session 2 gate for Intel-mac (x86_64) support. Where X64ThunkGate proves
// the bare cdecl->swiftcc thunk ABI in isolation (manual P/Invokes, no
// Swift.Runtime), this gate proves the *packaged* binding contract end to end:
//
//   1. Build a multi-platform FAT (arm64+x86_64) source xcframework from the
//      committed X64PackFixture.swift, with slices for every Apple platform
//      that ships an x86_64 target (macOS, iOS-sim, tvOS-sim, Mac Catalyst) plus
//      arm64-only device slices (iOS, tvOS).
//   2. Pack a 4-TFM SDK bindings library against it at a throwaway version.
//   3. Leg B (build/slice/package, all four x86_64 RIDs): extract the nupkg and
//      assert the per-RID wrapper *and* source framework binaries carry exactly
//      the expected arch set — fat where an x86_64 slice exists, arm64-only for
//      the device slices. `lipo -archs` on the real Mach-O is the load-bearing
//      check; the xcframework Info.plist SupportedArchitectures is asserted too
//      (plist can lie, so the binary check is primary, the plist defense-in-depth).
//   4. Leg A (osx-x64 runtime, the primary proof): consume the packed binding
//      from a net10.0-macos app with RuntimeIdentifier=osx-x64, run it under
//      `arch -x86_64`, and assert the Swift round-trip returns correctly while
//      ProcessArchitecture reports X64. Then repeat with osx-arm64 and assert
//      arm64 is unchanged.
//
// This is the doc's Session 2 gate verbatim: "build a binding from a third-party
// xcframework with an x86_64 macOS slice, pack it, consume from an osx-x64 app,
// run under Rosetta, assert correct; arm64 unchanged. The other three RIDs get a
// build/slice/package gate here." The runtime gates for Catalyst / iOS-sim /
// tvOS-sim x86_64 are S3/S4.
//
// Not part of `nuke test`/`nuke binding-tests`: needs the macOS SDK, the Apple
// .NET workload's osx-x64 runtime pack, and Rosetta. Opt-in: `nuke X64PackGate`.

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
    // Throwaway versions — the -x64packgate suffix keeps scratch nupkgs from
    // colliding with shipped ones. Apple supplement major must be a real integer
    // (the generator's ParseAppleVersionMajor rejects a leading 0), so pin it to
    // the live Apple train like PackGate does.
    const string X64PackGateVersion = "0.0.0-x64packgate";
    const string X64PackGateAppleVersion = "26.2.0-x64packgate";

    const string X64PackModule = "X64PackFixture";
    const string X64PackWrapperModule = "X64PackFixtureSwiftBindings";
    const string X64PackBindingsPackageId = "X64PackFixture.Bindings";

    // The packed bindings library's macOS TFM (suffixed — multi-TFM SwiftBindings
    // projects need the OS-version suffix so each inner build pre-sets
    // TargetFramework before _SwiftBindingPlatform detection runs). The Leg A
    // consumer app targets the same suffixed macOS TFM so NuGet resolves the
    // package's net10.0-macos26.2 asset exactly.
    const string X64PackConsumerTfm = "net10.0-macos26.2";

    // Round-trip the committed fixture exercises (class ARC + String + by-value
    // int + top-level func). The consumer prints these so the gate can assert the
    // value crossed the boundary intact in both directions.
    const string X64PackExpectedRoundTrip = "greeting=Hello, Rosetta! sum=42 describe=n=7";

    AbsolutePath X64PackGateScratch => RootDirectory / "artifacts" / "x64-pack-gate";
    AbsolutePath X64PackFixtureSource =>
        RootDirectory / "build" / "X64PackGate" / "Fixture" / "X64PackFixture.swift";

    // Expected per-RID WRAPPER slice arch sets in the packed nupkg. Slice ids are
    // the SDK-normalized identifiers (mirrors Sdk.props _SwiftBinding*SliceId), not
    // the upstream fat `arm64_x86_64` form. The binary is fat (arm64+x86_64) for
    // every slice that has an x86_64 target; arm64-only for the device slices.
    static readonly (string Rid, string SliceId, string[] Archs)[] X64PackWrapperExpected =
    [
        ("osx-arm64",         "macos-arm64",           ["arm64", "x86_64"]),
        ("maccatalyst-arm64", "ios-arm64-maccatalyst", ["arm64", "x86_64"]),
        ("ios-arm64",         "ios-arm64",             ["arm64"]),
        ("ios-arm64",         "ios-arm64-simulator",   ["arm64", "x86_64"]),
        ("tvos-arm64",        "tvos-arm64",            ["arm64"]),
        ("tvos-arm64",        "tvos-arm64-simulator",  ["arm64", "x86_64"]),
    ];

    // Expected per-RID SOURCE slice arch sets. Slice ids are the on-disk names
    // xcodebuild -create-xcframework assigns (fat slices keep the combined
    // `arm64_x86_64` form). Source fat slices must be preserved through slicing —
    // option (b) keeps one universal source binary per RID, not an arm64-only one.
    static readonly (string Rid, string SliceId, string[] Archs)[] X64PackSourceExpected =
    [
        ("osx-arm64",         "macos-arm64_x86_64",            ["arm64", "x86_64"]),
        ("maccatalyst-arm64", "ios-arm64_x86_64-maccatalyst",  ["arm64", "x86_64"]),
        ("ios-arm64",         "ios-arm64",                     ["arm64"]),
        ("ios-arm64",         "ios-arm64_x86_64-simulator",    ["arm64", "x86_64"]),
        ("tvos-arm64",        "tvos-arm64",                    ["arm64"]),
        ("tvos-arm64",        "tvos-arm64_x86_64-simulator",   ["arm64", "x86_64"]),
    ];

    Target X64PackGate => _ => _
        .DependsOn(Compile)
        .OnlyWhenStatic(() => OperatingSystem.IsMacOS())
        .Executes(() =>
        {
            var scratch = X64PackGateScratch;
            if (Directory.Exists(scratch)) scratch.DeleteDirectory();
            var nupkgDir = scratch / "packages";
            var bindingsDir = scratch / "bindings";
            var bindingsOut = scratch / "bindings-output";
            nupkgDir.CreateDirectory();
            bindingsDir.CreateDirectory();
            bindingsOut.CreateDirectory();

            Log.Information("=== X64PackGate: x86_64 packaging + Rosetta runtime gate ===");

            // Same hard-fail guard as Pack/PackGate: the SDK ships SwiftInterfaceParser;
            // without a universal2 host binary the gate would certify a packaging shape
            // that can't actually slice on this host. Run `nuke compile` first.
            var stagedBinary = SwiftInterfaceParserStagingDir / "SwiftInterfaceParser";
            if (!File.Exists(stagedBinary))
            {
                throw new InvalidOperationException(
                    $"X64PackGate: expected SwiftInterfaceParser binary at '{stagedBinary}' but it is missing. " +
                    "Run `nuke compile` on a macOS host with the Swift toolchain installed first.");
            }
            AssertUniversal2(stagedBinary);

            using var scope = new VersionScope(X64PackGateVersion, RootDirectory, X64PackGateAppleVersion);

            // 1. Publish generator into the SDK tools dir so the SDK's pack glob picks it up.
            Log.Information("  [1/6] Publishing generator");
            DotNetPublish(s => s
                .SetProject(SourceDir / "Swift.Bindings" / "src" / "Swift.Bindings.csproj")
                .SetConfiguration("Release")
                .SetOutput(SourceDir / "Swift.Bindings.Sdk" / "tools" / DotNetTfm / "any")
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            // 2. Rebuild the Apple supplement xcframework fresh (universal slices),
            //    then pack Runtime + Sdk + Apple at the throwaway version. The
            //    supplement is packed from native/ as-is, so rebuilding here makes the
            //    gate self-contained: it certifies the fat supplement the SDK injects
            //    implicitly into every binding, not whatever stale arm64-only copy a
            //    prior build left on disk.
            Log.Information("  [2/6] Rebuilding fat Apple supplement, then packing Runtime + Sdk + Apple");
            RunBuildAppleSupplementXcframework();
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

            // 3. Clear cached throwaway-version packages so a prior run can't shadow them.
            Log.Information("  [3/6] Clearing NuGet cache");
            ProcessTasks.StartProcess("dotnet", "nuget locals http-cache --clear", logOutput: false)
                .AssertWaitForExit();
            var nugetCacheDir = (AbsolutePath)(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages"));
            foreach (var (pkg, ver) in new[]
            {
                ("swiftbindings.runtime", X64PackGateVersion),
                ("swiftbindings.sdk", X64PackGateVersion),
                ("swiftbindings.apple", X64PackGateAppleVersion),
                (X64PackBindingsPackageId.ToLowerInvariant(), X64PackGateVersion),
            })
            {
                var pkgDir = nugetCacheDir / pkg / ver;
                if (Directory.Exists(pkgDir)) pkgDir.DeleteDirectory();
            }

            // 4. Build the multi-platform fat source xcframework + pack the bindings library.
            Log.Information("  [4/6] Building fat source xcframework + packing bindings");
            var sourceXcfw = BuildX64PackSourceXcframework(scratch / "swift");
            WriteX64PackBindingsProject(bindingsDir, nupkgDir, sourceXcfw);
            DotNetPack(s => s
                .SetProject(bindingsDir / "X64PackBindings.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(bindingsOut)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            var bindingsNupkg = Directory.GetFiles(bindingsOut, "*.nupkg").FirstOrDefault()
                ?? throw new Exception("X64PackGate: bindings pack produced no nupkg");

            // 5. Leg B — extract + assert per-RID wrapper and source arch sets.
            Log.Information("  [5/6] Verifying packed wrapper + source arch slices (Leg B)");
            var extractDir = bindingsOut / "extract";
            if (Directory.Exists(extractDir)) extractDir.DeleteDirectory();
            ZipFile.ExtractToDirectory(bindingsNupkg, extractDir);
            VerifyX64PackSlices(extractDir, bindingsNupkg);

            // 6. Leg A — consume the packed binding from osx-x64 (Rosetta) and osx-arm64.
            Log.Information("  [6/6] Consuming packed binding under osx-x64 (Rosetta) + osx-arm64 (Leg A)");
            RunX64PackConsumer(scratch, nupkgDir, bindingsOut, rid: "osx-x64",
                runUnderRosetta: true, expectedArch: "X64");
            RunX64PackConsumer(scratch, nupkgDir, bindingsOut, rid: "osx-arm64",
                runUnderRosetta: false, expectedArch: "Arm64");

            Log.Information("=== X64PackGate: PASS — x86_64 binding packed, sliced, and round-tripped under Rosetta; arm64 unchanged ===");
        });

    // Build a single multi-platform FAT source xcframework from the committed
    // fixture. macOS / iOS-sim / tvOS-sim / Mac Catalyst slices are universal
    // (arm64+x86_64) — lipo-merged from two single-arch compiles; iOS / tvOS
    // device slices are arm64-only. This is the "third-party xcframework with an
    // x86_64 slice" the gate consumes.
    AbsolutePath BuildX64PackSourceXcframework(AbsolutePath swiftRoot)
    {
        if (Directory.Exists(swiftRoot)) swiftRoot.DeleteDirectory();
        swiftRoot.CreateDirectory();
        var sources = new[] { X64PackFixtureSource.ToString() };

        var frameworkDirs = new List<AbsolutePath>
        {
            // macOS — universal.
            BuildFatFrameworkSlice(swiftRoot, "macos", sources,
                armTarget: "arm64-apple-macos12.0",  armSuffix: "arm64-apple-macos",
                x64Target: "x86_64-apple-macos12.0",  x64Suffix: "x86_64-apple-macos",
                sdkName: "macosx", minOs: "12.0", plistPlatform: "MacOSX"),

            // iOS device — arm64 only.
            BuildArm64FrameworkSlice(swiftRoot, "ios-device", sources,
                target: "arm64-apple-ios15.0", suffix: "arm64-apple-ios",
                sdkName: "iphoneos", minOs: "15.0", plistPlatform: "iPhoneOS"),

            // iOS simulator — universal.
            BuildFatFrameworkSlice(swiftRoot, "ios-sim", sources,
                armTarget: "arm64-apple-ios15.0-simulator",  armSuffix: "arm64-apple-ios-simulator",
                x64Target: "x86_64-apple-ios15.0-simulator",  x64Suffix: "x86_64-apple-ios-simulator",
                sdkName: "iphonesimulator", minOs: "15.0", plistPlatform: "iPhoneSimulator"),

            // tvOS device — arm64 only.
            BuildArm64FrameworkSlice(swiftRoot, "tvos-device", sources,
                target: "arm64-apple-tvos15.0", suffix: "arm64-apple-tvos",
                sdkName: "appletvos", minOs: "15.0", plistPlatform: "AppleTVOS"),

            // tvOS simulator — universal.
            BuildFatFrameworkSlice(swiftRoot, "tvos-sim", sources,
                armTarget: "arm64-apple-tvos15.0-simulator",  armSuffix: "arm64-apple-tvos-simulator",
                x64Target: "x86_64-apple-tvos15.0-simulator",  x64Suffix: "x86_64-apple-tvos-simulator",
                sdkName: "appletvsimulator", minOs: "15.0", plistPlatform: "AppleTVSimulator"),

            // Mac Catalyst — universal.
            BuildFatFrameworkSlice(swiftRoot, "maccatalyst", sources,
                armTarget: "arm64-apple-ios15.0-macabi",  armSuffix: "arm64-apple-ios-macabi",
                x64Target: "x86_64-apple-ios15.0-macabi",  x64Suffix: "x86_64-apple-ios-macabi",
                sdkName: "macosx", minOs: "15.0", plistPlatform: "MacOSX"),
        };

        var xcfw = swiftRoot / $"{X64PackModule}.xcframework";
        if (Directory.Exists(xcfw)) xcfw.DeleteDirectory();
        var settings = new CreateXcframeworkSettings().SetOutputPath(xcfw);
        foreach (var fw in frameworkDirs)
            settings.AddFrameworkPath(fw);
        XcodeBuild.ExecuteCreateXcframework(settings);
        return xcfw;
    }

    // Compile one arm64-only framework slice. Returns the .framework dir.
    AbsolutePath BuildArm64FrameworkSlice(
        AbsolutePath swiftRoot, string tag, IReadOnlyList<string> sources,
        string target, string suffix, string sdkName, string minOs, string plistPlatform)
    {
        var fwDir = swiftRoot / tag / $"{X64PackModule}.framework";
        fwDir.Parent.CreateDirectory();
        CompileModuleSlice(
            moduleName: X64PackModule, target: target, sdkPath: XcRun.GetSdkPath(sdkName),
            moduleSuffix: suffix, minOs: minOs, plistPlatform: plistPlatform,
            frameworkDir: fwDir, sourceFiles: sources, frameworkSearchPaths: null);
        return fwDir;
    }

    // Compile arm64 + x86_64 framework slices, then fold the x86_64 binary and
    // swiftmodule entries into the arm64 framework so the result is universal.
    // xcodebuild -create-xcframework accepts a single fat .framework per
    // platform+variant and names the slice `<platform>-arm64_x86_64[-variant]`.
    AbsolutePath BuildFatFrameworkSlice(
        AbsolutePath swiftRoot, string tag, IReadOnlyList<string> sources,
        string armTarget, string armSuffix, string x64Target, string x64Suffix,
        string sdkName, string minOs, string plistPlatform)
    {
        var sdkPath = XcRun.GetSdkPath(sdkName);
        var armFw = swiftRoot / tag / "arm64" / $"{X64PackModule}.framework";
        var x64Fw = swiftRoot / tag / "x86_64" / $"{X64PackModule}.framework";
        armFw.Parent.CreateDirectory();
        x64Fw.Parent.CreateDirectory();

        CompileModuleSlice(
            moduleName: X64PackModule, target: armTarget, sdkPath: sdkPath,
            moduleSuffix: armSuffix, minOs: minOs, plistPlatform: plistPlatform,
            frameworkDir: armFw, sourceFiles: sources, frameworkSearchPaths: null);
        CompileModuleSlice(
            moduleName: X64PackModule, target: x64Target, sdkPath: sdkPath,
            moduleSuffix: x64Suffix, minOs: minOs, plistPlatform: plistPlatform,
            frameworkDir: x64Fw, sourceFiles: sources, frameworkSearchPaths: null);

        // lipo can't write to one of its own inputs — merge to a temp, then replace.
        var fatBin = armFw.Parent / $"{X64PackModule}.fat";
        RunLipoCreate(new[] { armFw / X64PackModule, x64Fw / X64PackModule }, fatBin);
        File.Delete(armFw / X64PackModule);
        File.Move(fatBin, armFw / X64PackModule);

        // Fold the x86_64 swiftmodule artifacts in alongside the arm64 ones. Each
        // arch's files are suffix-named (e.g. x86_64-apple-macos.swiftmodule), so
        // they coexist; the per-arch .tbd shares a name, so keep only arm64's (the
        // fat binary carries both arches' symbols — the tbd is a secondary stub).
        var armModules = armFw / "Modules" / $"{X64PackModule}.swiftmodule";
        var x64Modules = x64Fw / "Modules" / $"{X64PackModule}.swiftmodule";
        foreach (var file in Directory.EnumerateFiles(x64Modules))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".tbd", StringComparison.Ordinal)) continue;
            File.Copy(file, armModules / name, overwrite: true);
        }
        return armFw;
    }

    // SDK bindings library: 4 Apple TFMs, one <SwiftFramework> against the fat
    // source xcframework. Packs into a NuGet package the Leg A app consumes.
    static void WriteX64PackBindingsProject(AbsolutePath bindingsDir, AbsolutePath nupkgDir, AbsolutePath sourceXcfw)
    {
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{X64PackGateVersion}">
              <PropertyGroup>
                <TargetFrameworks>net10.0-ios26.2;net10.0-tvos26.2;net10.0-maccatalyst26.2;net10.0-macos26.2</TargetFrameworks>
                <PackageId>{X64PackBindingsPackageId}</PackageId>
                <PackageVersion>{X64PackGateVersion}</PackageVersion>
                <IsPackable>true</IsPackable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftFramework Include="{sourceXcfw}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(bindingsDir / "X64PackBindings.csproj", csproj);
        File.WriteAllText(bindingsDir / "NuGet.config", X64PackNuGetConfig(nupkgDir));
    }

    static void VerifyX64PackSlices(AbsolutePath extractDir, string nupkgPath)
    {
        var failures = new List<string>();

        VerifyX64PackSliceSet(extractDir, X64PackWrapperModule, X64PackWrapperExpected, failures);
        VerifyX64PackSliceSet(extractDir, X64PackModule, X64PackSourceExpected, failures);

        if (failures.Count > 0)
        {
            Log.Error("X64PackGate (Leg B) FAILED — {Count} arch/layout mismatch(es) in {Nupkg}:",
                failures.Count, Path.GetFileName(nupkgPath));
            foreach (var f in failures)
                Log.Error("  {Detail}", f);
            Assert.Fail($"X64PackGate (Leg B): {failures.Count} arch/layout mismatch(es) in {nupkgPath}");
        }

        Log.Information("X64PackGate (Leg B) OK — wrapper + source slices carry the expected arch sets across all four x86_64 RIDs");
    }

    static void VerifyX64PackSliceSet(
        AbsolutePath extractDir, string module,
        (string Rid, string SliceId, string[] Archs)[] expected, List<string> failures)
    {
        foreach (var (rid, sliceId, archs) in expected)
        {
            var fwBinary = extractDir / "runtimes" / rid / "native" / $"{module}.xcframework"
                / sliceId / $"{module}.framework" / module;
            if (!File.Exists(fwBinary))
            {
                failures.Add($"missing binary: runtimes/{rid}/native/{module}.xcframework/{sliceId}/{module}.framework/{module}");
                continue;
            }

            var actual = LipoArchs(fwBinary);
            var want = archs.OrderBy(a => a, StringComparer.Ordinal).ToArray();
            if (!actual.SequenceEqual(want, StringComparer.Ordinal))
            {
                failures.Add(
                    $"{module} {rid}/{sliceId}: binary archs [{string.Join(", ", actual)}], " +
                    $"expected [{string.Join(", ", want)}]");
            }

            // Defense-in-depth: the xcframework Info.plist's SupportedArchitectures
            // for this slice must agree with the binary. A plist that lies (claims
            // x86_64 the binary lacks, or vice versa) breaks consumer slice
            // selection even when the binary is correct.
            var plistArchs = XcframeworkPlistArchs(
                extractDir / "runtimes" / rid / "native" / $"{module}.xcframework" / "Info.plist",
                sliceId);
            if (plistArchs is null)
            {
                failures.Add($"{module} {rid}/{sliceId}: no LibraryIdentifier '{sliceId}' in xcframework Info.plist");
            }
            else if (!plistArchs.SequenceEqual(want, StringComparer.Ordinal))
            {
                failures.Add(
                    $"{module} {rid}/{sliceId}: Info.plist SupportedArchitectures [{string.Join(", ", plistArchs)}], " +
                    $"expected [{string.Join(", ", want)}]");
            }
        }
    }

    // Build, restore, and run a consumer app that PackageReferences the packed
    // bindings nupkg at the given RID. Asserts the Swift round-trip string and the
    // reported process architecture. The osx-x64 build is launched under
    // `arch -x86_64`; osx-arm64 runs natively.
    void RunX64PackConsumer(
        AbsolutePath scratch, AbsolutePath nupkgDir, AbsolutePath bindingsOut,
        string rid, bool runUnderRosetta, string expectedArch)
    {
        var appDir = scratch / $"app-{rid}";
        if (Directory.Exists(appDir)) appDir.DeleteDirectory();
        appDir.CreateDirectory();
        WriteX64PackConsumerApp(appDir, nupkgDir, bindingsOut, rid);

        DotNetBuild(s => s
            .SetProjectFile(appDir / "X64PackApp.csproj")
            .SetConfiguration("Release")
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        var appExe = appDir / "bin" / "Release" / X64PackConsumerTfm / rid /
            "X64PackApp.app" / "Contents" / "MacOS" / "X64PackApp";
        if (!File.Exists(appExe))
            Assert.Fail($"X64PackGate (Leg A, {rid}): consumer app binary not produced at {appExe}");

        Log.Information("=== X64PackGate (Leg A, {Rid}): launching consumer{Mode} ===",
            rid, runUnderRosetta ? " under arch -x86_64" : "");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = appDir,
        };
        if (runUnderRosetta)
        {
            psi.FileName = "arch";
            psi.ArgumentList.Add("-x86_64");
            psi.ArgumentList.Add(appExe);
        }
        else
        {
            psi.FileName = appExe;
        }

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception($"X64PackGate (Leg A, {rid}): failed to launch consumer at {appExe}");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        Log.Information("X64PackGate ({Rid}) consumer output:\n{Output}", rid, stdout);

        if (proc.ExitCode != 0)
            Assert.Fail(
                $"X64PackGate (Leg A, {rid}): consumer exited {proc.ExitCode}.\n" +
                $"stdout:\n{stdout}\nstderr:\n{stderr}");
        if (!stdout.Contains(X64PackExpectedRoundTrip, StringComparison.Ordinal))
            Assert.Fail(
                $"X64PackGate (Leg A, {rid}): expected round-trip '{X64PackExpectedRoundTrip}' in stdout but got:\n" +
                $"stdout:\n{stdout}\nstderr:\n{stderr}");
        if (!stdout.Contains($"arch={expectedArch}", StringComparison.Ordinal))
            Assert.Fail(
                $"X64PackGate (Leg A, {rid}): expected 'arch={expectedArch}' in stdout but got:\n" +
                $"stdout:\n{stdout}\nstderr:\n{stderr}");

        Log.Information("X64PackGate (Leg A, {Rid}) OK — Swift round-trip returned; ProcessArchitecture={Arch}",
            rid, expectedArch);
    }

    static void WriteX64PackConsumerApp(
        AbsolutePath appDir, AbsolutePath nupkgDir, AbsolutePath bindingsOut, string rid)
    {
        // Plain net10.0-macos console app at the chosen RID. PackageReferences the
        // packed bindings — the SDK's buildTransitive targets inject the wrapper
        // NativeReference transitively, and .NET-for-Apple's ResolveNativeReferences
        // selects this RID's arch slice from the fat Mach-O.
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{X64PackConsumerTfm}</TargetFramework>
                <RuntimeIdentifier>{rid}</RuntimeIdentifier>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <SupportedOSPlatformVersion>13.0</SupportedOSPlatformVersion>
                <ApplicationId>com.swiftbindings.x64packgate</ApplicationId>
                <NoWarn>$(NoWarn);CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{X64PackBindingsPackageId}" Version="{X64PackGateVersion}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(appDir / "X64PackApp.csproj", csproj);

        var program = $$"""
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            using System.Runtime.InteropServices;

            using var greeter = new global::{{X64PackModule}}.Greeter("Hello");
            var greeting = greeter.Greet("Rosetta");
            var sum = greeter.Sum(40, 2);
            var describe = global::{{X64PackModule}}.Functions.Describe(7);
            Console.WriteLine($"arch={RuntimeInformation.ProcessArchitecture} greeting={greeting} sum={sum} describe={describe}");
            """;
        File.WriteAllText(appDir / "Program.cs", program);

        // App-level NuGet.config: SwiftBindings.* + the bindings package from local feeds.
        var nugetConfig = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="x64packgate-local" value="{nupkgDir}" />
                <add key="x64packgate-bindings" value="{bindingsOut}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="x64packgate-local">
                  <package pattern="SwiftBindings.*" />
                </packageSource>
                <packageSource key="x64packgate-bindings">
                  <package pattern="{X64PackBindingsPackageId}" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        File.WriteAllText(appDir / "NuGet.config", nugetConfig);
    }

    static string X64PackNuGetConfig(AbsolutePath nupkgDir) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="x64packgate-local" value="{nupkgDir}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="x64packgate-local">
              <package pattern="SwiftBindings.*" />
            </packageSource>
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """;

    static string[] LipoArchs(AbsolutePath binary)
    {
        var process = ProcessTasks.StartProcess("lipo", $"-archs \"{binary}\"", logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode();
        return process.Output.StdToText().Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToArray();
    }

    // Parse an xcframework Info.plist (via plutil -> JSON) and return the
    // SupportedArchitectures for the given LibraryIdentifier, sorted. Null if the
    // identifier is absent.
    static string[]? XcframeworkPlistArchs(AbsolutePath infoPlist, string libraryIdentifier)
    {
        if (!File.Exists(infoPlist)) return null;
        var process = ProcessTasks.StartProcess(
                "plutil", $"-convert json -o - \"{infoPlist}\"", logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode();
        using var doc = System.Text.Json.JsonDocument.Parse(process.Output.StdToText());
        if (!doc.RootElement.TryGetProperty("AvailableLibraries", out var libs))
            return null;
        foreach (var lib in libs.EnumerateArray())
        {
            if (!lib.TryGetProperty("LibraryIdentifier", out var id)) continue;
            if (id.GetString() != libraryIdentifier) continue;
            if (!lib.TryGetProperty("SupportedArchitectures", out var arches)) return Array.Empty<string>();
            return arches.EnumerateArray()
                .Select(a => a.GetString() ?? "")
                .OrderBy(a => a, StringComparer.Ordinal)
                .ToArray();
        }
        return null;
    }
}
