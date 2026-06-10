// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.X64SimGate.cs — iOS/tvOS x86_64 simulator packaging gate (Layer 3 + RID routing)
//
// iOS/tvOS x86_64 simulator packaging gate. X64PackGate already proved the
// *wrapper xcframework* ships fat `ios-arm64-simulator` and `tvos-arm64-simulator`
// slices (arm64+x86_64); this gate completes the chain by asserting a real
// .NET-for-Apple consumer at `RuntimeIdentifier=iossimulator-x64` (and
// `tvossimulator-x64`) resolves the x86_64 slice and embeds an x86_64-containing
// framework into its .app bundle.
//
// Why no runtime leg here: an x86_64 iOS/tvOS simulator cannot run on Apple
// Silicon — Apple Silicon hosts boot only arm64 simulators. This gate proves
// compile + packaging + native-reference selection only; the ABI itself is
// already proven by X64ThunkGate and the Mono-x86_64 runtime is already proven
// by the osx-x64 + maccatalyst-x64 BindingTests cells.
//
// Two legs:
//   Leg A — third-party SwiftFramework: pack the X64PackFixture-derived
//           bindings (reusing X64PackGate's fat-source-xcframework helpers) and
//           build a `net10.0-ios` / `net10.0-tvos` consumer app pinned to the
//           x86_64 sim RID. lipo-assert that the embedded
//           <App>.app/Frameworks/X64PackFixture.framework/X64PackFixture is
//           x86_64-containing.
//   Leg B — Apple-framework (StoreKit, "StoreKit2 reporter"):
//           build a `<SwiftAppleFrameworkTarget Module="StoreKit" />` binding
//           for iossimulator-x64, packing the wrapper xcframework. lipo-assert
//           that the produced wrapper xcframework's iOS sim slice carries
//           x86_64. StoreKit's iOS SDK ships both x86_64-apple-ios-simulator
//           and arm64-apple-ios-simulator swiftinterface slices (checked at
//           gate execution time; we fail fast if the user's Xcode dropped one).
//   Leg C — same StoreKit binding, but device-first (SwiftPlatformTarget=device)
//           so the wrapper SIMULATOR slice is produced by the SDK's
//           _CompileAppleFrameworkSecondWrapperSlice second-slice compile.
//   Leg D — SwiftUI-bridge-producing Apple framework (TipKit), device-first.
//           TipKit's binding emits a TipKitBridge.xcframework alongside the
//           wrapper, so this is the only leg that exercises the BRIDGE
//           second-slice compile (_CompileAppleFrameworkSecondBridgeSlice) and
//           the atomic park-aside bridge swap. The consumer asserts the embedded
//           TipKitBridge.framework's sim slice carries x86_64. TipKit ships both
//           x86_64 and arm64 simulator swiftinterface slices for iOS and tvOS
//           (checked at gate time; fail fast if Xcode dropped one).
//
// Not part of `nuke test`/`nuke binding-tests`: needs the macOS SDK, the Apple
// .NET workload's iossimulator-x64 + tvossimulator-x64 runtime packs, and the
// matching Xcode SDKs. Opt-in: `nuke X64SimGate`.

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
    // Throwaway versions — `-x64simgate` suffix isolates from X64PackGate's
    // `-x64packgate` so both gates can be run in the same shell session
    // without colliding in the NuGet cache. Apple supplement major must be a
    // real integer (the generator's ParseAppleVersionMajor rejects a leading
    // zero), so pin to the live Apple train like Pack/PackGate.
    const string X64SimGateVersion = "0.0.0-x64simgate";
    const string X64SimGateAppleVersion = "26.2.0-x64simgate";

    const string X64SimBindingsPackageId = "X64SimFixture.Bindings";
    const string X64SimAppleBindingsPackageId = "X64SimStoreKit.Bindings";
    // Leg C: same StoreKit Apple-framework binding, but device-first
    // (SwiftPlatformTarget=device). Distinct package id so it does not collide
    // with Leg B's binding in the same nupkg dir / NuGet cache.
    const string X64SimAppleBindingsDeviceFirstPackageId = "X64SimStoreKitDeviceFirst.Bindings";
    // Leg D: a SwiftUI-bridge-producing Apple framework (TipKit), packed
    // device-first so the SECOND-slice compile is the simulator slice. This is
    // the only X64SimGate leg whose binding emits a <Module>Bridge.xcframework,
    // so it is the only one that exercises _CompileAppleFrameworkSecondBridgeSlice
    // producing a fat arm64+x86_64 simulator bridge binary and committing it via
    // the atomic park-aside swap. The iossimulator-x64 / tvossimulator-x64
    // consumer then asserts the embedded TipKitBridge.framework carries x86_64.
    const string X64SimTipKitDeviceFirstPackageId = "X64SimTipKitDeviceFirst.Bindings";

    // Consumer TFMs (suffixed — required by the multi-TFM SwiftBindings pipeline
    // so each inner build pre-sets TargetFramework before _SwiftBindingPlatform
    // detection runs). Consumer apps target the matching iOS/tvOS suffix so the
    // resolved native asset comes from the corresponding nupkg TFM.
    const string X64SimIosTfm = "net10.0-ios26.2";
    const string X64SimTvosTfm = "net10.0-tvos26.2";

    AbsolutePath X64SimGateScratch => RootDirectory / "artifacts" / "x64-sim-gate";

    Target X64SimGate => _ => _
        .DependsOn(Compile)
        .OnlyWhenStatic(() => OperatingSystem.IsMacOS())
        // Pure ordering edge for Nuke --strict's sink-total-order requirement.
        // See X64PackGate for the chain rationale.
        .After(X64PackGate)
        .Executes(() =>
        {
            var scratch = X64SimGateScratch;
            if (Directory.Exists(scratch)) scratch.DeleteDirectory();
            var nupkgDir = scratch / "packages";
            var bindingsDir = scratch / "bindings";
            var bindingsOut = scratch / "bindings-output";
            var storeKitBindingsDir = scratch / "storekit-bindings";
            var storeKitBindingsOut = scratch / "storekit-bindings-output";
            var storeKitDeviceFirstBindingsDir = scratch / "storekit-devicefirst-bindings";
            var storeKitDeviceFirstBindingsOut = scratch / "storekit-devicefirst-bindings-output";
            var tipKitDeviceFirstBindingsDir = scratch / "tipkit-devicefirst-bindings";
            var tipKitDeviceFirstBindingsOut = scratch / "tipkit-devicefirst-bindings-output";
            nupkgDir.CreateDirectory();
            bindingsDir.CreateDirectory();
            bindingsOut.CreateDirectory();
            storeKitBindingsDir.CreateDirectory();
            storeKitBindingsOut.CreateDirectory();
            storeKitDeviceFirstBindingsDir.CreateDirectory();
            storeKitDeviceFirstBindingsOut.CreateDirectory();
            tipKitDeviceFirstBindingsDir.CreateDirectory();
            tipKitDeviceFirstBindingsOut.CreateDirectory();

            Log.Information("=== X64SimGate: iOS/tvOS x86_64 simulator packaging gate ===");

            // Same hard-fail guard as Pack/PackGate/X64PackGate: the SDK ships
            // SwiftInterfaceParser; without a universal2 host binary the gate would
            // certify a packaging shape that cannot slice on this host.
            var stagedBinary = SwiftInterfaceParserStagingDir / "SwiftInterfaceParser";
            if (!File.Exists(stagedBinary))
            {
                throw new InvalidOperationException(
                    $"X64SimGate: expected SwiftInterfaceParser binary at '{stagedBinary}' but it is missing. " +
                    "Run `nuke compile` on a macOS host with the Swift toolchain installed first.");
            }
            AssertUniversal2(stagedBinary);

            // StoreKit slice precheck: the doc's named reporter target. Fail fast
            // if Apple has dropped the x86_64 simulator slice — otherwise the
            // gate would mis-attribute the failure to our SDK.
            AssertStoreKitX64SliceAvailable();
            // TipKit slice precheck (Leg D): same fail-fast rationale, for both
            // the iOS and tvOS simulator SDKs the bridge leg consumes.
            AssertTipKitX64SliceAvailable();

            using var scope = new VersionScope(X64SimGateVersion, RootDirectory, X64SimGateAppleVersion);

            // 1. Publish generator into the SDK tools dir so the SDK's pack glob picks it up.
            Log.Information("  [1/7] Publishing generator");
            DotNetPublish(s => s
                .SetProject(SourceDir / "Swift.Bindings" / "src" / "Swift.Bindings.csproj")
                .SetConfiguration("Release")
                .SetOutput(SourceDir / "Swift.Bindings.Sdk" / "tools" / DotNetTfm / "any")
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            // 2. Rebuild the fat Apple supplement, then pack Runtime + Sdk + Apple.
            Log.Information("  [2/7] Rebuilding fat Apple supplement, then packing Runtime + Sdk + Apple");
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
            Log.Information("  [3/7] Clearing NuGet cache");
            ProcessTasks.StartProcess("dotnet", "nuget locals http-cache --clear", logOutput: false)
                .AssertWaitForExit();
            var nugetCacheDir = (AbsolutePath)(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages"));
            foreach (var (pkg, ver) in new[]
            {
                ("swiftbindings.runtime", X64SimGateVersion),
                ("swiftbindings.sdk", X64SimGateVersion),
                ("swiftbindings.apple", X64SimGateAppleVersion),
                (X64SimBindingsPackageId.ToLowerInvariant(), X64SimGateVersion),
                (X64SimAppleBindingsPackageId.ToLowerInvariant(), X64SimGateVersion),
                (X64SimAppleBindingsDeviceFirstPackageId.ToLowerInvariant(), X64SimGateVersion),
                (X64SimTipKitDeviceFirstPackageId.ToLowerInvariant(), X64SimGateVersion),
            })
            {
                var pkgDir = nugetCacheDir / pkg / ver;
                if (Directory.Exists(pkgDir)) pkgDir.DeleteDirectory();
            }

            // 4. Build the fat source xcframework — reuses X64PackGate's helper to
            //    produce iOS-sim + tvOS-sim slices that are fat (arm64+x86_64).
            Log.Information("  [4/7] Building fat source xcframework + packing third-party bindings");
            var sourceXcfw = BuildX64PackSourceXcframework(scratch / "swift");
            WriteX64SimBindingsProject(bindingsDir, nupkgDir, sourceXcfw);
            DotNetPack(s => s
                .SetProject(bindingsDir / "X64SimBindings.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(bindingsOut)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            // 5. Pack the StoreKit Apple-framework binding (iOS + tvOS TFMs).
            //    SwiftTargetArchitectures left at the SDK default (`auto`) so the
            //    wrapper is fat where the system framework ships x86_64.
            //
            //    Packed TWICE in succession: the second pack catches H2 (slice-id
            //    desync on incremental). _CompileAppleFrameworkSecondWrapperSlice
            //    is gated on _SwiftBindingUpToDate != 'true', so on an incremental
            //    pack the slice-id resync MUST come from a target that runs every
            //    build (_ResyncWrapperSliceIds). Pre-H2 fix this
            //    second pack fails with SWIFTBIND032 against a valid on-disk
            //    fat-named slice.
            Log.Information("  [5/8] Packing StoreKit Apple-framework binding (iOS + tvOS, x2 for incremental)");
            WriteX64SimStoreKitBindingsProject(storeKitBindingsDir, nupkgDir);
            for (int packAttempt = 1; packAttempt <= 2; packAttempt++)
            {
                Log.Information("    pack attempt {Attempt}/2", packAttempt);
                DotNetPack(s => s
                    .SetProject(storeKitBindingsDir / "X64SimStoreKitBindings.csproj")
                    .SetConfiguration("Release")
                    .SetOutputDirectory(storeKitBindingsOut)
                    .EnableNoLogo()
                    .SetVerbosity(DotNetVerbosity.quiet));
            }

            // 6. Pack the StoreKit Apple-framework binding with SwiftPlatformTarget=device.
            //    Leg C scope: the second-slice (simulator) compile must produce a fat
            //    arm64+x86_64 binary even when the first slice is device-arm64-only —
            //    otherwise iossimulator-x64 / tvossimulator-x64 consumers fail
            //    NativeReference resolution against an arm64-only sim slice.
            Log.Information("  [6/9] Packing StoreKit Apple-framework binding (device-first, Leg C)");
            WriteX64SimStoreKitDeviceFirstBindingsProject(storeKitDeviceFirstBindingsDir, nupkgDir);
            DotNetPack(s => s
                .SetProject(storeKitDeviceFirstBindingsDir / "X64SimStoreKitDeviceFirstBindings.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(storeKitDeviceFirstBindingsOut)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            // 6b. Pack the TipKit SwiftUI-bridge binding, device-first (Leg D).
            //     TipKit emits a TipKitBridge.xcframework, so the device-first
            //     pack drives _CompileAppleFrameworkSecondBridgeSlice to build the
            //     SIMULATOR bridge slice as the "second" slice — which must come
            //     out fat arm64+x86_64 and commit through the atomic park-aside
            //     swap, or the iossimulator-x64 / tvossimulator-x64 consumer
            //     resolves an arm64-only bridge slice and the lipo check on
            //     TipKitBridge.framework fails "missing x86_64". Packed TWICE for
            //     the same incremental-resync reason as the StoreKit leg: the
            //     second pack is up-to-date, so the bridge second-slice / swap
            //     must survive a no-regen build (the recovery + resync targets run
            //     every build, not just first generation).
            Log.Information("  [7/9] Packing TipKit SwiftUI-bridge binding (device-first, x2 for incremental, Leg D)");
            WriteX64SimTipKitDeviceFirstBindingsProject(tipKitDeviceFirstBindingsDir, nupkgDir);
            for (int packAttempt = 1; packAttempt <= 2; packAttempt++)
            {
                Log.Information("    pack attempt {Attempt}/2", packAttempt);
                DotNetPack(s => s
                    .SetProject(tipKitDeviceFirstBindingsDir / "X64SimTipKitDeviceFirstBindings.csproj")
                    .SetConfiguration("Release")
                    .SetOutputDirectory(tipKitDeviceFirstBindingsOut)
                    .EnableNoLogo()
                    .SetVerbosity(DotNetVerbosity.quiet));
            }

            // 7. Leg A — third-party iOS-sim + tvOS-sim consumer apps. Build only
            //    (no run — Apple Silicon cannot host x86_64 iOS/tvOS simulators).
            Log.Information("  [8/9] Consuming packed third-party binding under iossimulator-x64 + tvossimulator-x64 (Leg A)");
            VerifyConsumerX64SimSlice(scratch, nupkgDir, bindingsOut,
                consumerTag: "ios-sim-x64", tfm: X64SimIosTfm, rid: "iossimulator-x64",
                bindingsPackageId: X64SimBindingsPackageId,
                programNamespace: "X64PackFixture",
                useGreeter: true);
            VerifyConsumerX64SimSlice(scratch, nupkgDir, bindingsOut,
                consumerTag: "tvos-sim-x64", tfm: X64SimTvosTfm, rid: "tvossimulator-x64",
                bindingsPackageId: X64SimBindingsPackageId,
                programNamespace: "X64PackFixture",
                useGreeter: true);

            // 7. Leg B — Apple-framework (StoreKit) consumer at iossimulator-x64
            //    and tvossimulator-x64. Asserts the wrapper xcframework's iOS/tvOS
            //    sim slice embeds x86_64. The StoreKit module surface is referenced
            //    only for compile (`typeof(...)`) — no real StoreKit calls — so the
            //    gate does not need a configured StoreKit account.
            Log.Information("  [9/9] Consuming StoreKit (Leg B/C) + TipKit bridge (Leg D) Apple-framework bindings");
            VerifyConsumerX64SimSlice(scratch, nupkgDir, storeKitBindingsOut,
                consumerTag: "ios-sim-x64-storekit", tfm: X64SimIosTfm, rid: "iossimulator-x64",
                bindingsPackageId: X64SimAppleBindingsPackageId,
                programNamespace: "StoreKit",
                useGreeter: false);
            VerifyConsumerX64SimSlice(scratch, nupkgDir, storeKitBindingsOut,
                consumerTag: "tvos-sim-x64-storekit", tfm: X64SimTvosTfm, rid: "tvossimulator-x64",
                bindingsPackageId: X64SimAppleBindingsPackageId,
                programNamespace: "StoreKit",
                useGreeter: false);

            // Leg C — same as Leg B but consuming the device-first binding pack.
            // Pre-H1 fix this would resolve an arm64-only sim slice and the lipo
            // check on the embedded framework would fail with "missing x86_64".
            VerifyConsumerX64SimSlice(scratch, nupkgDir, storeKitDeviceFirstBindingsOut,
                consumerTag: "ios-sim-x64-storekit-devicefirst", tfm: X64SimIosTfm, rid: "iossimulator-x64",
                bindingsPackageId: X64SimAppleBindingsDeviceFirstPackageId,
                programNamespace: "StoreKit",
                useGreeter: false);
            VerifyConsumerX64SimSlice(scratch, nupkgDir, storeKitDeviceFirstBindingsOut,
                consumerTag: "tvos-sim-x64-storekit-devicefirst", tfm: X64SimTvosTfm, rid: "tvossimulator-x64",
                bindingsPackageId: X64SimAppleBindingsDeviceFirstPackageId,
                programNamespace: "StoreKit",
                useGreeter: false);

            // Leg D — TipKit SwiftUI-bridge binding, device-first. The embedded
            // Frameworks/ now also carries TipKitBridge.framework; VerifyConsumer
            // walks every embedded framework binary and requires x86_64 in ALL of
            // them, so it certifies the bridge sim slice the second-bridge-slice
            // compile + atomic swap produced. `TipKit.Tips` is the force-load
            // anchor (TipKit's configuration namespace, emits in every supported
            // SDK); no real TipKit call, so no app context is needed.
            VerifyConsumerX64SimSlice(scratch, nupkgDir, tipKitDeviceFirstBindingsOut,
                consumerTag: "ios-sim-x64-tipkit-devicefirst", tfm: X64SimIosTfm, rid: "iossimulator-x64",
                bindingsPackageId: X64SimTipKitDeviceFirstPackageId,
                programNamespace: "TipKit",
                useGreeter: false,
                appleAnchorType: "Tips",
                requiredFramework: "TipKitBridge");
            VerifyConsumerX64SimSlice(scratch, nupkgDir, tipKitDeviceFirstBindingsOut,
                consumerTag: "tvos-sim-x64-tipkit-devicefirst", tfm: X64SimTvosTfm, rid: "tvossimulator-x64",
                bindingsPackageId: X64SimTipKitDeviceFirstPackageId,
                programNamespace: "TipKit",
                useGreeter: false,
                appleAnchorType: "Tips",
                requiredFramework: "TipKitBridge");

            Log.Information("=== X64SimGate: PASS — iossimulator-x64 + tvossimulator-x64 packaging round-trip green for third-party + Apple-framework bindings (sim-first, device-first, and SwiftUI-bridge) ===");
        });

    // Fail fast if Apple has dropped the x86_64-apple-ios-simulator swiftinterface
    // slice from StoreKit (or moved its framework). This is the "StoreKit2 reporter"
    // target; without it, Leg B mis-attributes a vanished upstream slice to our SDK.
    static void AssertStoreKitX64SliceAvailable()
    {
        var sdkPathProc = ProcessTasks.StartProcess("xcrun", "--sdk iphonesimulator --show-sdk-path",
                logOutput: false)
            .AssertWaitForExit()
            .AssertZeroExitCode();
        var sdkPath = sdkPathProc.Output.StdToText().Trim();
        var iface = Path.Combine(sdkPath, "System", "Library", "Frameworks", "StoreKit.framework",
            "Modules", "StoreKit.swiftmodule", "x86_64-apple-ios-simulator.swiftinterface");
        if (!File.Exists(iface))
            throw new InvalidOperationException(
                $"X64SimGate: required slice not found at '{iface}'. Apple's iOS simulator SDK no longer ships an " +
                "x86_64 StoreKit swiftinterface — Leg B cannot certify the StoreKit2 reporter path. " +
                "Update this gate (drop StoreKit, pick a still-x86_64 framework, or remove Leg B).");
    }

    // Fail fast if Apple has dropped the x86_64 simulator swiftinterface slice for
    // TipKit on either the iOS or tvOS simulator SDK. Leg D consumes the TipKit
    // binding on iossimulator-x64 AND tvossimulator-x64, and the binding's wrapper
    // sim slice can only be fat where the source framework ships x86_64. Without
    // this precheck, a vanished upstream slice would be mis-attributed to the
    // SDK's bridge second-slice path.
    static void AssertTipKitX64SliceAvailable()
    {
        foreach (var (sdk, triple) in new[]
        {
            ("iphonesimulator", "x86_64-apple-ios-simulator"),
            ("appletvsimulator", "x86_64-apple-tvos-simulator"),
        })
        {
            var sdkPathProc = ProcessTasks.StartProcess("xcrun", $"--sdk {sdk} --show-sdk-path",
                    logOutput: false)
                .AssertWaitForExit()
                .AssertZeroExitCode();
            var sdkPath = sdkPathProc.Output.StdToText().Trim();
            var iface = Path.Combine(sdkPath, "System", "Library", "Frameworks", "TipKit.framework",
                "Modules", "TipKit.swiftmodule", $"{triple}.swiftinterface");
            if (!File.Exists(iface))
                throw new InvalidOperationException(
                    $"X64SimGate: required slice not found at '{iface}'. Apple's {sdk} SDK no longer ships an " +
                    "x86_64 TipKit swiftinterface — Leg D cannot certify the SwiftUI-bridge second-slice path. " +
                    "Update this gate (pick a still-x86_64 bridge-producing framework, or remove Leg D).");
        }
    }

    // Write the third-party SwiftFramework binding library project. 2-TFM (iOS
    // + tvOS) — macOS / Catalyst are X64PackGate's job. Same fat source xcframework
    // X64PackGate builds, so the fixture stays a single source of truth.
    static void WriteX64SimBindingsProject(AbsolutePath bindingsDir, AbsolutePath nupkgDir, AbsolutePath sourceXcfw)
    {
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{X64SimGateVersion}">
              <PropertyGroup>
                <TargetFrameworks>{X64SimIosTfm};{X64SimTvosTfm}</TargetFrameworks>
                <PackageId>{X64SimBindingsPackageId}</PackageId>
                <PackageVersion>{X64SimGateVersion}</PackageVersion>
                <IsPackable>true</IsPackable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftFramework Include="{sourceXcfw}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(bindingsDir / "X64SimBindings.csproj", csproj);
        File.WriteAllText(bindingsDir / "NuGet.config", X64SimNuGetConfig(nupkgDir));
    }

    // Write the StoreKit Apple-framework binding library project. SDK default
    // SwiftTargetArchitectures=auto → fat wrapper where the source slice is fat.
    static void WriteX64SimStoreKitBindingsProject(AbsolutePath bindingsDir, AbsolutePath nupkgDir)
    {
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{X64SimGateVersion}">
              <PropertyGroup>
                <TargetFrameworks>{X64SimIosTfm};{X64SimTvosTfm}</TargetFrameworks>
                <PackageId>{X64SimAppleBindingsPackageId}</PackageId>
                <PackageVersion>{X64SimGateVersion}</PackageVersion>
                <IsPackable>true</IsPackable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftAppleFrameworkTarget Include="StoreKit" Module="StoreKit" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(bindingsDir / "X64SimStoreKitBindings.csproj", csproj);
        File.WriteAllText(bindingsDir / "NuGet.config", X64SimNuGetConfig(nupkgDir));
    }

    // Leg C variant: SwiftPlatformTarget=device + explicit SwiftTargetArchitectures=arm64,x86_64.
    // Device-first makes the wrapper first-slice the arm64-only device slice, so the SDK's
    // _CompileAppleFrameworkSecondWrapperSlice produces the SIM slice as the "second" slice;
    // the H1 fix requires that second-slice compile to emit a fat arm64+x86_64 sim binary.
    // Adding the explicit arch list also exercises the M1 fix: ResolveAppleFrameworkAutoArchBasis
    // must derive the basis from PlatformInfo.SimulatorSlice (fat) rather than the active
    // DeviceSlice (arm-only) so TryDecideWrapperArchitectures accepts x86_64 instead of
    // rejecting it with SWIFTBIND052 before the SDK ever gets to fat-fold the sim slice.
    static void WriteX64SimStoreKitDeviceFirstBindingsProject(AbsolutePath bindingsDir, AbsolutePath nupkgDir)
    {
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{X64SimGateVersion}">
              <PropertyGroup>
                <TargetFrameworks>{X64SimIosTfm};{X64SimTvosTfm}</TargetFrameworks>
                <PackageId>{X64SimAppleBindingsDeviceFirstPackageId}</PackageId>
                <PackageVersion>{X64SimGateVersion}</PackageVersion>
                <IsPackable>true</IsPackable>
                <SwiftPlatformTarget>device</SwiftPlatformTarget>
                <SwiftTargetArchitectures>arm64,x86_64</SwiftTargetArchitectures>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftAppleFrameworkTarget Include="StoreKit" Module="StoreKit" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(bindingsDir / "X64SimStoreKitDeviceFirstBindings.csproj", csproj);
        File.WriteAllText(bindingsDir / "NuGet.config", X64SimNuGetConfig(nupkgDir));
    }

    // Leg D: a TipKit Apple-framework binding, device-first. TipKit exposes
    // SwiftUI views, so the binding emits a TipKitBridge.xcframework alongside the
    // wrapper. Device-first (SwiftPlatformTarget=device) makes the wrapper AND the
    // bridge first-slice the arm64-only device slice, so the SDK's
    // _CompileAppleFrameworkSecondBridgeSlice produces the SIMULATOR bridge slice
    // as the "second" slice — which must emit a fat arm64+x86_64 binary and commit
    // through the atomic park-aside swap, or the iossimulator-x64 / tvossimulator-x64
    // consumer resolves an arm64-only bridge slice and the lipo check fails. The
    // explicit arm64,x86_64 arch list mirrors the StoreKit device-first leg.
    static void WriteX64SimTipKitDeviceFirstBindingsProject(AbsolutePath bindingsDir, AbsolutePath nupkgDir)
    {
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{X64SimGateVersion}">
              <PropertyGroup>
                <TargetFrameworks>{X64SimIosTfm};{X64SimTvosTfm}</TargetFrameworks>
                <PackageId>{X64SimTipKitDeviceFirstPackageId}</PackageId>
                <PackageVersion>{X64SimGateVersion}</PackageVersion>
                <IsPackable>true</IsPackable>
                <SwiftPlatformTarget>device</SwiftPlatformTarget>
                <SwiftTargetArchitectures>arm64,x86_64</SwiftTargetArchitectures>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftAppleFrameworkTarget Include="TipKit" Module="TipKit" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(bindingsDir / "X64SimTipKitDeviceFirstBindings.csproj", csproj);
        File.WriteAllText(bindingsDir / "NuGet.config", X64SimNuGetConfig(nupkgDir));
    }

    // Build a consumer iOS/tvOS app that PackageReferences the packed bindings at
    // the given x86_64 simulator RID. Asserts the .app bundle's embedded
    // framework binary is fat (arm64+x86_64) or x86_64-containing — i.e. .NET-for-
    // Apple's ResolveNativeReferences picked a slice that actually serves the RID.
    void VerifyConsumerX64SimSlice(
        AbsolutePath scratch, AbsolutePath nupkgDir, AbsolutePath bindingsOut,
        string consumerTag, string tfm, string rid,
        string bindingsPackageId, string programNamespace, bool useGreeter,
        string appleAnchorType = "AppTransaction", string? requiredFramework = null)
    {
        var appDir = scratch / $"app-{consumerTag}";
        if (Directory.Exists(appDir)) appDir.DeleteDirectory();
        appDir.CreateDirectory();
        WriteX64SimConsumerApp(appDir, nupkgDir, bindingsOut, tfm, rid, bindingsPackageId, programNamespace, useGreeter, appleAnchorType);

        DotNetBuild(s => s
            .SetProjectFile(appDir / "X64SimApp.csproj")
            .SetConfiguration("Release")
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        // .NET-for-Apple names the bundle `<AssemblyName>.app` — both iOS and tvOS
        // use the project name unchanged. (RuntimeTestsApp.tvOS produces
        // `RuntimeTestsApp.tvOS.app` only because the project name itself carries
        // the suffix.)
        var appBundleDir = appDir / "bin" / "Release" / tfm / rid / "X64SimApp.app";
        if (!Directory.Exists(appBundleDir))
            Assert.Fail($"X64SimGate ({consumerTag}): consumer app bundle not produced at {appBundleDir}");

        var frameworksDir = appBundleDir / "Frameworks";
        if (!Directory.Exists(frameworksDir))
            Assert.Fail($"X64SimGate ({consumerTag}): no Frameworks/ dir in {appBundleDir}; " +
                        "the consumer build dropped every NativeReference.");

        // Find every embedded framework's Mach-O and assert it carries x86_64.
        // The interesting framework varies (X64PackFixture, StoreKitSwiftBindings,
        // SBApple, …), so we walk the whole Frameworks/ dir
        // and let lipo speak. We require at LEAST ONE embedded framework binary
        // and EVERY embedded framework binary to contain x86_64 — a missing
        // x86_64 in any embedded framework would crash at sim launch.
        var failures = new List<string>();
        int frameworkCount = 0;
        var embeddedNames = new List<string>();
        foreach (var fwDir in Directory.EnumerateDirectories(frameworksDir, "*.framework"))
        {
            var fwName = Path.GetFileNameWithoutExtension(fwDir);
            var binary = Path.Combine(fwDir, fwName);
            if (!File.Exists(binary))
                continue; // Stub framework with no Mach-O — Apple system frameworks ship like this.
            frameworkCount++;
            embeddedNames.Add(fwName);
            var archs = LipoArchs(binary);
            if (!archs.Contains("x86_64", StringComparer.Ordinal))
            {
                failures.Add($"{fwName}: archs [{string.Join(", ", archs)}] missing x86_64 " +
                             $"(binary: {binary})");
            }
        }

        if (frameworkCount == 0)
            Assert.Fail($"X64SimGate ({consumerTag}): {frameworksDir} contained no embedded framework " +
                        "binaries — ResolveNativeReferences picked nothing.");
        // When a specific framework is required (Leg D's SwiftUI bridge), assert it
        // actually embedded — otherwise the x86_64 loop above would pass vacuously
        // on the wrapper alone and never certify the bridge second-slice path. The
        // match is exact on the framework name (not a substring), so a sibling like
        // a hypothetical 'TipKitBridgeHelper' could never satisfy the 'TipKitBridge'
        // requirement while the real bridge is absent.
        if (requiredFramework != null &&
            !embeddedNames.Any(n => string.Equals(n, requiredFramework, StringComparison.Ordinal)))
            Assert.Fail($"X64SimGate ({consumerTag}): required framework '{requiredFramework}' " +
                        $"was not embedded — found only [{string.Join(", ", embeddedNames)}]. The " +
                        "bridge xcframework's sim slice did not flow to the consumer.");
        if (failures.Count > 0)
        {
            Log.Error("X64SimGate ({Tag}) FAILED — {Count} embedded framework(s) missing x86_64 slice:",
                consumerTag, failures.Count);
            foreach (var f in failures)
                Log.Error("  {Detail}", f);
            Assert.Fail($"X64SimGate ({consumerTag}): {failures.Count} embedded framework(s) missing x86_64.");
        }

        Log.Information("X64SimGate ({Tag}) OK — {Count} embedded framework binary(ies) carry x86_64 at {App}",
            consumerTag, frameworkCount, appBundleDir);
    }

    static void WriteX64SimConsumerApp(
        AbsolutePath appDir, AbsolutePath nupkgDir, AbsolutePath bindingsOut,
        string tfm, string rid, string bindingsPackageId, string programNamespace, bool useGreeter,
        string appleAnchorType = "AppTransaction")
    {
        // For .NET-for-Apple sim builds, MtouchLink=None keeps the linker from
        // pruning code; ResolveNativeReferences still embeds the framework via
        // the bindings package's transitive NativeReference. ApplicationId is
        // suffixed per tag/rid so concurrent gate runs don't collide on the
        // simulator bundle id.
        var appIdSuffix = $"{tfm.Replace('.', 'd').Replace('-', '_')}-{rid.Replace('-', '_')}";
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{tfm}</TargetFramework>
                <RuntimeIdentifier>{rid}</RuntimeIdentifier>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <ApplicationId>com.swiftbindings.x64simgate.{appIdSuffix}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
                <MtouchLink>None</MtouchLink>
                <NoWarn>$(NoWarn);CA1416;IL2104;IL2026</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{bindingsPackageId}" Version="{X64SimGateVersion}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(appDir / "X64SimApp.csproj", csproj);

        // Two program shapes. The third-party fixture path exercises the
        // Greeter/Describe round-trip (mirrors X64PackGate's Leg A program),
        // forcing the linker to retain the binding types so ResolveNativeReferences
        // embeds the framework. The Apple-framework path just `typeof`-references
        // a StoreKit binding type — calling real StoreKit on launch would require
        // a configured app context, which sim host validation does not have. The
        // typeof keeps the assembly + native reference live for the linker.
        string program;
        if (useGreeter)
        {
            program = $$"""
                // Copyright (c) 2026 Justin Wojciechowski.
                // Licensed under the MIT License.
                using System.Runtime.InteropServices;

                // Force-load the binding assembly + retain ResolveNativeReferences hint.
                using var greeter = new global::{{programNamespace}}.Greeter("Hello");
                var greeting = greeter.Greet("X64Sim");
                var sum = greeter.Sum(40, 2);
                var describe = global::{{programNamespace}}.Functions.Describe(7);
                Console.WriteLine($"arch={RuntimeInformation.ProcessArchitecture} greeting={greeting} sum={sum} describe={describe}");
                """;
        }
        else
        {
            // typeof keeps the binding-assembly reference alive for the linker
            // without invoking the Apple framework (which may need a configured
            // runtime context that a packaging gate cannot provide). MtouchLink=None
            // above ensures the assembly + its NativeReference(s) are embedded even
            // for the compile-only typeof shape. The anchor type is the framework's
            // headline type (StoreKit → AppTransaction; TipKit → Tips) and emits in
            // every supported SDK.
            program = $$"""
                // Copyright (c) 2026 Justin Wojciechowski.
                // Licensed under the MIT License.
                using System.Reflection;
                using System.Runtime.InteropServices;

                // Force-load the bindings assembly so ResolveNativeReferences embeds
                // its NativeReference(s) (the wrapper xcframework, and — for a
                // SwiftUI-bridge framework — the companion bridge xcframework).
                // Walks types instead of calling them — the framework may need a
                // configured context at runtime and this gate is build-only.
                var asm = typeof(global::{{programNamespace}}.{{appleAnchorType}}).Assembly;
                Console.WriteLine($"arch={RuntimeInformation.ProcessArchitecture} loaded={asm.GetName().Name} types={asm.GetTypes().Length}");
                """;
        }
        File.WriteAllText(appDir / "Program.cs", program);

        var nugetConfig = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="x64simgate-local" value="{nupkgDir}" />
                <add key="x64simgate-bindings" value="{bindingsOut}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="x64simgate-local">
                  <package pattern="SwiftBindings.*" />
                </packageSource>
                <packageSource key="x64simgate-bindings">
                  <package pattern="{bindingsPackageId}" />
                </packageSource>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """;
        File.WriteAllText(appDir / "NuGet.config", nugetConfig);
    }

    static string X64SimNuGetConfig(AbsolutePath nupkgDir) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="x64simgate-local" value="{nupkgDir}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="x64simgate-local">
              <package pattern="SwiftBindings.*" />
            </packageSource>
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """;
}
