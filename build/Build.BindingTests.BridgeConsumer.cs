// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.BridgeConsumer.cs — opt-in ordinary consumer of the Swift bridges, on .NET 10 and
// .NET 11 for iOS.
//
// The generator and SwiftBindings.Runtime route each Swift-convention call the swiftc lowering
// evidence authorizes through a native bridge (SBSR_), reached through a C twin: the .NET 11 CoreCLR
// interpreter mishandles the Swift calling convention, and Mono's x20 handling exposes .NET 10 apps.
// This leg proves the way an ordinary consumer gets the bridges: a third-party-shaped module bound
// through SwiftBindings.Sdk with default settings and no hand-authored <SwiftLoweringEvidence>. The
// SDK's own wrapper compile collects the module's lowering receipt and routes what it authorizes, in
// the same build. The binding is consumed three ways, each built by an ordinary app restoring a
// throwaway Runtime/Sdk/Apple feed of this tree:
// - package: the binding is packed and the app takes one PackageReference;
// - direct: the app's own csproj imports SwiftBindings.Sdk and declares the <SwiftFramework>;
// - projectref: the app takes a ProjectReference to the binding project.
//
// The app calls two transitions and checks what each did:
// - BridgeAccount.settled, a throwing getter on a class isolated to a custom global actor (self, a
//   Swift error and a scalar result). The @_cdecl wrapper refuses custom-global-actor members, so
//   its only import is the Swift-convention one the bridge replaces: success, error, success, with
//   the receiver's state checked after each.
// - Array's subscript getter through the Runtime's bridge, by indexing the array the module returns.
// Every measured call records its stack in Swift. The host requires the routed bridge under the Swift
// code and the lane's managed caller under the bridge, on every call: the CoreCLR interpreter's call
// stub (.NET 11), or code compiled into the app (.NET 10: NativeAOT, and Mono, which the iOS SDK
// AOT-compiles on the arm64 simulator as well as the device). The app also calls both bridges from a register
// sentinel and checks the module's live-object count before and after. The host checks each mode's
// SDK-collected receipt against the oracle and validates the bridges of the packed and linked images.
//
// Opt-in. .NET 11 runs it with --coreclr-interp. Never part of the default run or --compile-only.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    [Parameter("Opt-in: bind a module through SwiftBindings.Sdk with default settings and no hand-authored lowering evidence, consume it as a package, SDK-direct and through a ProjectReference, and prove on the simulator and/or a device that its calls run through the bridges the SDK routed. .NET 10: --sim (Mono JIT), --device (NativeAOT), --device --mono-aot (Mono full-AOT). .NET 11: --coreclr-interp. Never part of the default run or --compile-only.")]
    readonly bool BridgeConsumer;

    [Parameter("With --bridge-consumer: the consumption modes to run, from package, direct and projectref. Default: all three.")]
    readonly string[] BridgeConsumerModes = [];

    const string BcfVersion = "0.0.0-bridgeconsumer";
    const string BcfModule = "BridgeConsumerFixture";
    const string BcfWrapperModule = "BridgeConsumerFixtureSwiftBindings";
    const string BcfPackageId = "BridgeConsumerFixture.Binding";
    const string BcfAppName = "BridgeConsumerApp";
    const string BcfBundleId = "com.swiftbindings.bridgeconsumer";
    const string BcfRuntimeLibrary = "SwiftBindingsRuntime";

    string BcfLaneVersion => MixedPackageLaneVersion(BcfVersion, RuntimeLane);
    string BcfLaneAppleVersion => MixedPackageAppleVersion("bridge", RuntimeLane);
    AbsolutePath BcfScratch => RootDirectory / "artifacts" / "bridge-consumer";
    AbsolutePath BcfReceipts => BcfScratch / "receipts";

    /// <summary>How the app consumes the binding.</summary>
    enum BcfMode { Package, Direct, ProjectReference }

    /// <summary>What must call a bridge on a lane: the managed code that made the Swift call.</summary>
    enum BcfCaller { Interpreter, App }

    /// <summary>One platform a mode runs on.</summary>
    sealed record BcfPlatform(bool OnDevice, bool MonoAot)
    {
        public string Name => OnDevice ? MonoAot ? "device-monoaot" : "device" : "simulator";
        public string Rid => OnDevice ? "ios-arm64" : "iossimulator-arm64";
    }

    /// <summary>One routed bridge the host expects on a measured call.</summary>
    sealed record BcfRoute(string Tag, string Member, string Target, string Bridge, string Image);

    /// <summary>What one binding build produced: its routing sidecar and the receipt it collected.</summary>
    sealed record BcfBinding(AbsolutePath Plans, AbsolutePath Receipt, BcfRoute Settle);

    static IReadOnlyList<BcfMode> ParseBridgeConsumerModes(IReadOnlyList<string> requested)
    {
        if (requested.Count == 0)
            return [BcfMode.Package, BcfMode.Direct, BcfMode.ProjectReference];
        var modes = new List<BcfMode>();
        foreach (var name in requested.SelectMany(r => r.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var mode = name.ToLowerInvariant() switch
            {
                "package" => BcfMode.Package,
                "direct" => BcfMode.Direct,
                "projectref" or "projectreference" => BcfMode.ProjectReference,
                _ => throw new ArgumentException($"--bridge-consumer-modes: unknown mode '{name}'; expected package, direct or projectref."),
            };
            if (!modes.Contains(mode)) modes.Add(mode);
        }
        return modes;
    }

    static string BcfModeName(BcfMode mode) => mode switch
    {
        BcfMode.Package => "package",
        BcfMode.Direct => "direct",
        _ => "projectref",
    };

    // .NET 10 runs Mono, which the iOS SDK AOT-compiles on the device and on the arm64 simulator alike
    // (it runs the AOT compiler for iossimulator-arm64), so the P/Invoke wrapper is in the app image.
    BcfCaller BcfExpectedCaller(BcfPlatform platform) =>
        RuntimeLane.IsNet11 ? BcfCaller.Interpreter : BcfCaller.App;

    void RunBridgeConsumerLeg(bool sim, bool device, bool monoAot)
    {
        var modes = ParseBridgeConsumerModes(BridgeConsumerModes);
        Log.Information("=================================================");
        Log.Information(" BindingTests — ordinary consumer of the Swift bridges (--bridge-consumer)");
        Log.Information("   {Framework}   sim: {Sim}   device: {Device}{MonoAot}   modes: {Modes}", RuntimeLane.Framework, sim, device,
            monoAot ? " (Mono full-AOT)" : "", string.Join(", ", modes.Select(BcfModeName)));
        Log.Information("=================================================");

        if (RuntimeLane.IsNet11 && !RuntimeLane.CoreClrInterpreter)
            throw new Exception("--bridge-consumer measures the .NET 11 CoreCLR interpreter on .NET 11. Run it with --coreclr-interp: " +
                "bash build.sh --toolchain-profile net11-xcode27 -- binding-tests --bridge-consumer --coreclr-interp [--sim] [--device]");
        // Also stages SwiftInterfaceParser, which the SDK package ships and every binding build runs.
        EnsureGeneratorBuilt();
        var stagedParser = SwiftInterfaceParserStagingDir / "SwiftInterfaceParser";
        if (!File.Exists(stagedParser))
            throw new InvalidOperationException($"--bridge-consumer: SwiftInterfaceParser is missing at '{stagedParser}'.");

        if (Directory.Exists(BcfScratch)) BcfScratch.DeleteDirectory();
        BcfReceipts.CreateDirectory();
        var nupkgDir = BcfScratch / "packages";
        nupkgDir.CreateDirectory();

        var platforms = new List<BcfPlatform>();
        if (sim) platforms.Add(new BcfPlatform(OnDevice: false, MonoAot: false));
        if (device) platforms.Add(new BcfPlatform(OnDevice: true, MonoAot: monoAot));

        var xcframework = BuildBridgeConsumerXcframework(BcfScratch / "build");

        // An independent cross-check, never an input: the oracle records what swiftc gives each of the
        // fixture's plan targets on every slice, and each SDK-collected receipt must agree with it.
        var oracle = RecordBridgeConsumerOracle(xcframework);

        var failures = new List<string>();
        var passed = false;
        try
        {
            using var scope = new VersionScope(BcfLaneVersion, RootDirectory, BcfLaneAppleVersion);
            BuildLocalPackageFeed(nupkgDir, scope, "bridge-consumer", BcfLaneVersion, BcfLaneAppleVersion, BcfPackageId);
            AssertRuntimeNativePackage(nupkgDir / $"SwiftBindings.Runtime.{BcfLaneVersion}.nupkg");
            var element = BridgeConsumerElementRoute();

            foreach (var mode in modes)
                failures.AddRange(mode switch
                {
                    BcfMode.Package => RunBridgeConsumerPackage(nupkgDir, xcframework, oracle, element, platforms),
                    BcfMode.Direct => RunBridgeConsumerDirect(nupkgDir, xcframework, oracle, element, platforms),
                    _ => RunBridgeConsumerProjectReference(nupkgDir, xcframework, oracle, element, platforms),
                });

            if (failures.Count > 0)
            {
                foreach (var f in failures) Log.Error("  ✗ {Failure}", f);
                throw new Exception($"--bridge-consumer: {failures.Count} problem(s); receipts under {BcfReceipts}.");
            }
            Log.Information("--bridge-consumer PASSED ({Modes} on {Platforms}). Receipts: {Dir}",
                string.Join(", ", modes.Select(BcfModeName)), string.Join(", ", platforms.Select(p => p.Name)), BcfReceipts);
            passed = true;
        }
        finally
        {
            EvictThrowawayPackages(BcfLaneVersion, BcfLaneAppleVersion, BcfPackageId);
            if (Directory.Exists(nupkgDir)) nupkgDir.DeleteDirectory();
            // The scratch bindings and consumers are large (a NativeAOT publish is several hundred MB):
            // a passing run keeps only its receipts; a failing one keeps everything for the post-mortem.
            if (passed)
                foreach (var dir in Directory.GetDirectories(BcfScratch).Where(d => (AbsolutePath)d != BcfReceipts))
                    ((AbsolutePath)dir).DeleteDirectory();
        }
    }

    // MARK: - Consumption modes

    // The binding packed once (Release), then consumed through one PackageReference on every platform.
    IEnumerable<string> RunBridgeConsumerPackage(AbsolutePath nupkgDir, AbsolutePath xcframework, AbsolutePath oracle,
        BcfRoute element, IReadOnlyList<BcfPlatform> platforms)
    {
        var fixtureDir = BcfScratch / "package-binding";
        var fixtureOut = BcfScratch / "package-output";
        fixtureDir.CreateDirectory();
        fixtureOut.CreateDirectory();
        WriteBridgeConsumerBinding(fixtureDir, nupkgDir, xcframework, packable: true);
        Log.Information("=== bridge-consumer [package]: packing the binding with default settings ===");
        DotNetPack(s => s
            .SetProject(fixtureDir / $"{BcfModule}.csproj")
            .SetConfiguration("Release")
            .SetOutputDirectory(fixtureOut)
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        var binding = BridgeConsumerBinding(BcfMode.Package, fixtureDir, "Release", oracle);
        AssertBridgeConsumerPackedWrapper(fixtureOut, binding.Plans);

        var failures = new List<string>();
        foreach (var platform in platforms)
        {
            var appDir = BcfScratch / $"package-app-{platform.Name}";
            appDir.CreateDirectory();
            WriteBridgeConsumerApp(appDir, BcfMode.Package, platform, element, xcframework, projectReference: null);
            File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureOut, "BridgeConsumerFixture.*"));
            failures.AddRange(RunBridgeConsumerApp(BcfMode.Package, appDir, platform, element, _ => binding));
        }
        return failures;
    }

    // The app is the binding: its own csproj imports SwiftBindings.Sdk, so each platform's build
    // generates, collects its own receipt and routes.
    IEnumerable<string> RunBridgeConsumerDirect(AbsolutePath nupkgDir, AbsolutePath xcframework, AbsolutePath oracle,
        BcfRoute element, IReadOnlyList<BcfPlatform> platforms)
    {
        var failures = new List<string>();
        foreach (var platform in platforms)
        {
            var appDir = BcfScratch / $"direct-app-{platform.Name}";
            appDir.CreateDirectory();
            File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));
            WriteBridgeConsumerApp(appDir, BcfMode.Direct, platform, element, xcframework, projectReference: null);
            failures.AddRange(RunBridgeConsumerApp(BcfMode.Direct, appDir, platform, element,
                configuration => BridgeConsumerBinding(BcfMode.Direct, appDir, configuration, oracle)));
        }
        return failures;
    }

    // The app takes a ProjectReference to a binding project, which the app's build builds in its own configuration.
    IEnumerable<string> RunBridgeConsumerProjectReference(AbsolutePath nupkgDir, AbsolutePath xcframework, AbsolutePath oracle,
        BcfRoute element, IReadOnlyList<BcfPlatform> platforms)
    {
        var fixtureDir = BcfScratch / "projectref-binding";
        fixtureDir.CreateDirectory();
        WriteBridgeConsumerBinding(fixtureDir, nupkgDir, xcframework, packable: false);
        var failures = new List<string>();
        foreach (var platform in platforms)
        {
            var appDir = BcfScratch / $"projectref-app-{platform.Name}";
            appDir.CreateDirectory();
            File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));
            WriteBridgeConsumerApp(appDir, BcfMode.ProjectReference, platform, element, xcframework,
                projectReference: fixtureDir / $"{BcfModule}.csproj");
            failures.AddRange(RunBridgeConsumerApp(BcfMode.ProjectReference, appDir, platform, element,
                configuration => BridgeConsumerBinding(BcfMode.ProjectReference, fixtureDir, configuration, oracle)));
        }
        return failures;
    }

    // The binding project an ordinary consumer writes: SwiftBindings.Sdk, one <SwiftFramework>, defaults.
    void WriteBridgeConsumerBinding(AbsolutePath fixtureDir, AbsolutePath nupkgDir, AbsolutePath xcframework, bool packable)
    {
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{BcfLaneVersion}">
              <PropertyGroup>
                <TargetFramework>{RuntimeLane.Framework}-ios</TargetFramework>
                <PackageId>{BcfPackageId}</PackageId>
                <PackageVersion>{BcfLaneVersion}</PackageVersion>
                <IsPackable>{(packable ? "true" : "false")}</IsPackable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416;CS8604</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <SwiftFramework Include="{xcframework}" />
              </ItemGroup>
            </Project>
            """;
        if (csproj.Contains("SwiftLoweringEvidence", StringComparison.Ordinal))
            throw new InvalidOperationException("--bridge-consumer: the ordinary consumer's binding must not carry hand-authored lowering evidence.");
        File.WriteAllText(fixtureDir / $"{BcfModule}.csproj", csproj);
        File.WriteAllText(fixtureDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));
    }

    // What one binding build in `projectDir` produced under `configuration`: the sidecar that routed
    // the module and the receipt that authorized it. The receipt must agree with the oracle and show
    // the settle target on every slice the fixture ships, and the sidecar must route settle.
    BcfBinding BridgeConsumerBinding(BcfMode mode, AbsolutePath projectDir, string configuration, AbsolutePath oracle)
    {
        var obj = projectDir / "obj" / configuration;
        var label = $"[{BcfModeName(mode)}] {projectDir.Name} ({configuration})";
        AbsolutePath One(string fileName)
        {
            var found = Directory.Exists(obj) ? Directory.GetFiles(obj, fileName, SearchOption.AllDirectories) : [];
            if (found.Length != 1)
                throw new Exception($"--bridge-consumer {label}: expected one {fileName} under {obj}, found {found.Length}.");
            return (AbsolutePath)found[0];
        }

        var plans = One($"{BcfModule}.swift-call-plans.json");
        var receipt = One("swift-lowering-receipt.json");
        var journal = One($"{BcfModule}.swift-bridge-routing.json");
        var tag = $"{BcfModeName(mode)}-{configuration.ToLowerInvariant()}";
        File.Copy(plans, BcfReceipts / $"{tag}-{plans.Name}", overwrite: true);
        File.Copy(receipt, BcfReceipts / $"{tag}-{receipt.Name}", overwrite: true);
        File.Copy(journal, BcfReceipts / $"{tag}-{journal.Name}", overwrite: true);

        AssertBridgeConsumerFullyRouted(label, plans);
        var settle = BridgeConsumerSettleRoute(plans);
        AssertBridgeConsumerCollectedReceipt(label, receipt, oracle, settle);
        return new BcfBinding(plans, receipt, settle);
    }

    // Every member the fixture's binding plans must reach the app through its bridge: one left on its
    // Swift-convention import is the transition the interpreter and Mono cannot make, wherever the app
    // happens to call it.
    static void AssertBridgeConsumerFullyRouted(string label, AbsolutePath plans)
    {
        var unrouted = JsonDocument.Parse(File.ReadAllText(plans)).RootElement.GetProperty("coverage").EnumerateArray()
            .Where(e => e.GetProperty("status").GetString() == "Planned"
                        && !(e.TryGetProperty("bridged", out var b) && b.ValueKind == JsonValueKind.True))
            .Select(e => $"{e.GetProperty("member").GetString()}: {(e.TryGetProperty("evidence", out var why) ? why.GetString() : "no evidence")}")
            .ToList();
        if (unrouted.Count > 0)
            throw new Exception($"--bridge-consumer {label}: {unrouted.Count} planned member(s) still reach Swift through a direct import:\n  " +
                string.Join("\n  ", unrouted));
        Log.Information("  {Label}: every planned member is routed through its bridge", label);
    }

    // MARK: - Fixture module and the oracle

    // The slices BuildBridgeConsumerXcframework builds, by the triple each is compiled for.
    static readonly string[] BcfSliceTriples = [ApplePlatform.IOS.DeviceTarget!, ApplePlatform.IOS.SimulatorTarget];

    // A two-slice (device + simulator) dynamic framework, each slice linking the register sentinel.
    AbsolutePath BuildBridgeConsumerXcframework(AbsolutePath buildRoot)
    {
        buildRoot.CreateDirectory();
        var sourceDir = BindingTestsDir / "Sources" / BcfModule;
        var swiftSources = Directory.GetFiles(sourceDir, "*.swift").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var sentinel = sourceDir / "bridge-sentinel-arm64.S";
        if (swiftSources.Count == 0 || !File.Exists(sentinel))
            throw new Exception($"--bridge-consumer: fixture sources missing under {sourceDir}.");

        var ios = ApplePlatform.IOS;
        var slices = new[]
        {
            (Id: ios.DeviceSliceId!, Target: ios.DeviceTarget!, Sdk: ios.DeviceSdkName!, Suffix: ios.DeviceModuleSuffix!, Plist: ios.DevicePlistPlatform!),
            (Id: ios.SimulatorSliceId, Target: ios.SimulatorTarget, Sdk: ios.SimulatorSdkName, Suffix: ios.SimulatorModuleSuffix, Plist: ios.SimulatorPlistPlatform),
        };
        var create = new CreateXcframeworkSettings();
        foreach (var slice in slices)
        {
            var sliceDir = buildRoot / slice.Id;
            sliceDir.CreateDirectory();
            var sdkPath = XcRun.GetSdkPath(slice.Sdk);
            var sentinelObject = sliceDir / "bridge-sentinel-arm64.o";
            XcRunTool($"clang -c \"{sentinel}\" -o \"{sentinelObject}\" -target {slice.Target} -isysroot \"{sdkPath}\"");
            var frameworkDir = sliceDir / $"{BcfModule}.framework";
            CompileModuleSlice(BcfModule, slice.Target, sdkPath, slice.Suffix, ios.MinOsVersion, slice.Plist, frameworkDir,
                swiftSources.Append(sentinelObject.ToString()).ToList(), frameworkSearchPaths: [sliceDir.ToString()]);
            create = create.AddFrameworkPath(frameworkDir);
        }
        var xcframework = BcfScratch / "native" / $"{BcfModule}.xcframework";
        xcframework.Parent.CreateDirectory();
        XcodeBuild.ExecuteCreateXcframework(create.SetOutputPath(xcframework));
        return xcframework;
    }

    // Generates once to learn the fixture's plan targets, then lets the oracle record what swiftc
    // gives each one on every required triple. The result is compared with, never fed to, the SDK.
    AbsolutePath RecordBridgeConsumerOracle(AbsolutePath xcframework)
    {
        var planDir = BcfScratch / "plan-generation";
        planDir.CreateDirectory();
        Log.Information("=== bridge-consumer: generating once to learn the fixture's call plans ===");
        var args = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{xcframework}\"",
            $"-o \"{planDir}\"",
            "--platform ios",
            $"--target-framework {RuntimeLane.Framework}-ios",
            "--skip-wrapper-compilation",
            "--no-verify-csharp",
        };
        var proc = ProcessTasks.StartProcess("dotnet", string.Join(" ", args), workingDirectory: RootDirectory, logOutput: false);
        proc.WaitForExit();
        File.WriteAllLines(BcfReceipts / "plan-generation.log", proc.Output.Select(o => o.Text));
        if (proc.ExitCode != 0)
            throw new Exception($"--bridge-consumer: the generator exited {proc.ExitCode}; log at {BcfReceipts / "plan-generation.log"}.");
        var sidecar = planDir / $"{BcfModule}.swift-call-plans.json";
        var wrapper = planDir / $"{BcfModule}.Wrapper.swift";
        foreach (var required in new[] { sidecar, wrapper })
            if (!File.Exists(required))
                throw new Exception($"--bridge-consumer: the generator wrote no {required.Name} in {planDir}.");

        var evidence = BcfScratch / "oracle-lowering-evidence.json";
        var reports = BcfReceipts / "oracle";
        var manifest = BcfScratch / "oracle-corpus.json";
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            mainModule = BcfModule,
            main = Directory.GetFiles(BindingTestsDir / "Sources" / BcfModule, "*.swift").OrderBy(f => f, StringComparer.Ordinal),
            reports = reports.ToString(),
            scratch = (BcfScratch / "oracle-compile").ToString(),
            wrappers = new Dictionary<string, string> { [BcfWrapperModule] = wrapper },
            probe = (SourceDir / "Swift.Bindings" / "tests" / "UnitTests" / "LoweringOracle" / "Evidence" / "HandWrittenTargets.swift").ToString(),
            artifacts = new[] { sidecar.ToString() },
            evidence = evidence.ToString(),
        }));

        Log.Information("=== bridge-consumer: recording the oracle's swiftc lowering evidence (cross-check only) ===");
        Environment.SetEnvironmentVariable("SWIFT_LOWERING_ORACLE_CORPUS", manifest);
        try
        {
            DotNetTest(s => s
                .SetProjectFile(SourceDir / "Swift.Bindings" / "tests" / "UnitTests")
                .SetConfiguration("Debug")
                .SetFilter("FullyQualifiedName~SwiftLoweringOracleTests.Corpus_EveryPredictionMatchesSwiftc")
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath=\"{DotNetHostPath}\""));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SWIFT_LOWERING_ORACLE_CORPUS", null);
            var compiled = BcfScratch / "oracle-compile";
            if (Directory.Exists(compiled)) compiled.DeleteDirectory();
        }
        if (!File.Exists(evidence))
            throw new Exception($"--bridge-consumer: the oracle wrote no evidence at {evidence}.");
        File.Copy(evidence, BcfReceipts / evidence.Name, overwrite: true);
        foreach (var report in Directory.GetFiles(reports, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
            foreach (var line in File.ReadAllLines(report).Where(l => l.StartsWith("evidence:", StringComparison.Ordinal)))
                Log.Information("  {Triple}: {Line}", Path.GetFileNameWithoutExtension(report), line);
        return evidence;
    }

    // The SDK's wrapper compile collects the module's lowering receipt from the IR of the wrapper it
    // links and of the lowering probe that calls each plan target through the library's public
    // interface. It must hold the build's own evidence: under the oracle's compiler and SDKs, every
    // symbol it shows exactly as the oracle recorded it on the same slice. The bridged transition's
    // target is the positive control: the wrapper does not call it, so only the probe can show it, and
    // the receipt must show it, as the oracle does, on every slice the fixture ships.
    void AssertBridgeConsumerCollectedReceipt(string label, AbsolutePath collected, AbsolutePath oracle, BcfRoute bridged)
    {
        var expected = BcfReceiptOf(oracle, BcfModule);
        var actual = BcfReceiptOf(collected, BcfModule);
        var problems = new List<string>();
        if (actual.Compiler != expected.Compiler)
            problems.Add($"the receipt was collected by {actual.Compiler}; the oracle by {expected.Compiler}");
        if (actual.Slices.Count == 0)
            problems.Add("the receipt covers no slice");
        foreach (var (triple, slice) in actual.Slices)
        {
            if (!expected.Slices.TryGetValue(triple, out var oracleSlice))
            {
                problems.Add($"{triple}: the oracle recorded no evidence for a slice the SDK receipt covers");
                continue;
            }
            if (slice.Sdk != oracleSlice.Sdk)
                problems.Add($"{triple}: collected against {slice.Sdk}; the oracle against {oracleSlice.Sdk}");
            var agree = 0;
            foreach (var (symbol, signature) in slice.Symbols)
            {
                if (!oracleSlice.Symbols.TryGetValue(symbol, out var oracleSignature))
                    problems.Add($"{triple}: the SDK receipt shows {symbol} as {signature}; the oracle shows no {symbol}");
                else if (oracleSignature != signature)
                    problems.Add($"{triple}: the SDK receipt shows {symbol} as {signature}; the oracle as {oracleSignature}");
                else
                    agree++;
            }
            // Every shown symbol was compared with the oracle above; the bridged target must be one of them.
            if (slice.Symbols.TryGetValue(bridged.Target, out var bridgedSignature))
                Log.Information("  {Label} receipt {Triple}: shows the bridged target {Target} as {Signature}", label, triple, bridged.Target, bridgedSignature);
            else
                problems.Add($"{triple}: the SDK receipt does not show the bridged target {bridged.Target} of {bridged.Member}" +
                    (slice.Unobserved.TryGetValue(bridged.Target, out var why) ? $"; it records it unobserved: {why}" : "; it does not record it at all"));
            if (!oracleSlice.Symbols.ContainsKey(bridged.Target))
                problems.Add($"{triple}: the oracle does not show the bridged target {bridged.Target}, so agreeing with it proves nothing");
            Log.Information("  {Label} receipt {Triple}: {Agree} of {Shown} shown symbol(s) agree with the oracle ({OracleShown} shown there), {Unobserved} unobserved",
                label, triple, agree, slice.Symbols.Count, oracleSlice.Symbols.Count, slice.Unobserved.Count);
        }
        foreach (var triple in BcfSliceTriples.Where(t => !actual.Slices.ContainsKey(t)))
            problems.Add($"the receipt covers no {triple} slice, which the fixture's xcframework ships");
        if (problems.Count > 0)
        {
            foreach (var p in problems) Log.Error("  ✗ {Label}: {Problem}", label, p);
            throw new Exception($"--bridge-consumer {label}: the SDK-collected lowering receipt {collected} disagrees with the oracle in {problems.Count} place(s).");
        }
    }

    sealed record BcfSlice(string Sdk, Dictionary<string, string> Symbols, Dictionary<string, string> Unobserved);
    sealed record BcfReceipt(string Compiler, Dictionary<string, BcfSlice> Slices);

    // The one receipt of `artifact` in a lowering-evidence file.
    static BcfReceipt BcfReceiptOf(AbsolutePath path, string artifact)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var receipts = document.RootElement.GetProperty("receipts").EnumerateArray()
            .Where(r => r.GetProperty("artifact").GetString() == artifact)
            .ToList();
        if (receipts.Count != 1)
            throw new Exception($"--bridge-consumer: expected one receipt of {artifact} in {path}, found {receipts.Count}.");
        var receipt = receipts[0];
        var slices = new Dictionary<string, BcfSlice>(StringComparer.Ordinal);
        foreach (var slice in receipt.GetProperty("slices").EnumerateObject())
        {
            var symbols = slice.Value.GetProperty("symbols").EnumerateObject()
                .ToDictionary(s => s.Name, s => s.Value.GetString()!, StringComparer.Ordinal);
            var unobserved = slice.Value.TryGetProperty("unobserved", out var u) && u.ValueKind == JsonValueKind.Object
                ? u.EnumerateObject().ToDictionary(s => s.Name, s => s.Value.GetString()!, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            slices[slice.Name] = new BcfSlice(slice.Value.GetProperty("sdk").GetString()!, symbols, unobserved);
        }
        return new BcfReceipt(receipt.GetProperty("compiler").GetString()!, slices);
    }

    // MARK: - Routes and packed-image checks

    static IEnumerable<JsonElement> BcfBridged(AbsolutePath path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("coverage").EnumerateArray()
            .Where(e => e.TryGetProperty("bridged", out var b) && b.ValueKind == JsonValueKind.True)
            .Select(e => e.Clone()).ToList();

    static BcfRoute BcfSingleRoute(IEnumerable<JsonElement> entries, Func<string, bool> member, string tag, string image, string what, AbsolutePath sidecar)
    {
        var matches = entries.Where(e => member(e.GetProperty("member").GetString()!)).ToList();
        if (matches.Count != 1)
            throw new Exception($"--bridge-consumer: expected exactly one bridged {what} in {sidecar}, found {matches.Count}. " +
                "The build did not route it through a bridge; check its receipt and the sidecar.");
        var e = matches[0];
        var route = new BcfRoute(tag, e.GetProperty("member").GetString()!, e.GetProperty("entryPoint").GetString()!,
            e.GetProperty("symbol").GetString()!, image);
        Log.Information("  route {Tag}: {Member} -> {Image}!{Bridge} -> {Target}", route.Tag, route.Member, route.Image, route.Bridge, route.Target);
        return route;
    }

    // The settle getter's bridge, read from the sidecar of the build that routed it.
    static BcfRoute BridgeConsumerSettleRoute(AbsolutePath plans) =>
        BcfSingleRoute(BcfBridged(plans), m => m.Contains("::PInvoke_settled_Get", StringComparison.Ordinal),
            "settle", BcfWrapperModule, "BridgeAccount.settled getter", plans);

    // Array's subscript getter, which the Runtime routes for every consumer.
    BcfRoute BridgeConsumerElementRoute()
    {
        var plans = SwiftBridgeProductsDir / "Swift.Runtime.swift-call-plans.json";
        return BcfSingleRoute(BcfBridged(plans), m => m == "Swift.SwiftArrayPInvokes::Get", "element", BcfRuntimeLibrary, "SwiftArrayPInvokes.Get", plans);
    }

    // The binding package's wrapper must export its bridges, validated against the binding's sidecar on every slice.
    void AssertBridgeConsumerPackedWrapper(AbsolutePath fixtureOut, AbsolutePath fixturePlans)
    {
        var nupkg = fixtureOut / $"{BcfPackageId}.{BcfLaneVersion}.nupkg";
        if (!File.Exists(nupkg))
            throw new Exception($"--bridge-consumer: the binding package was not produced at {nupkg}.");
        var extract = BcfScratch / "packed-wrapper";
        if (Directory.Exists(extract)) extract.DeleteDirectory();
        var images = new List<string>();
        using (var archive = ZipFile.OpenRead(nupkg))
        {
            foreach (var entry in archive.Entries.Where(e =>
                         e.FullName.EndsWith($"/{BcfWrapperModule}.framework/{BcfWrapperModule}", StringComparison.Ordinal)))
            {
                var target = extract / entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                target.Parent.CreateDirectory();
                entry.ExtractToFile(target, overwrite: true);
                images.Add(target);
                Log.Information("  packed wrapper slice: {Entry}", entry.FullName);
            }
        }
        if (images.Count < 2)
            throw new Exception($"--bridge-consumer: {nupkg.Name} carries {images.Count} wrapper slice(s); expected the device and simulator slices.");
        ValidateLinkedBridges(images, fixturePlans, $"the packed wrapper of {nupkg.Name}");
        extract.DeleteDirectory();
    }

    void ValidateLinkedBridges(IReadOnlyList<string> images, AbsolutePath plans, string what)
    {
        EnsureGeneratorBuilt(ensureSwiftInterfaceParser: false);
        var scratch = BcfScratch / "linked-check" / Guid.NewGuid().ToString("N");
        scratch.CreateDirectory();
        try
        {
            var arguments = new List<string> { $"\"{GeneratorDll}\"", $"--swift-call-plans \"{plans}\"", $"-o \"{scratch}\"" };
            arguments.AddRange(images.Select(i => $"--validate-linked-bridges \"{i}\""));
            RunGenerator(arguments, $"--bridge-consumer: the linked-bridge check of {what}");
            Log.Information("  linked-bridge check OK — {What} ({Count} image(s))", what, images.Count);
        }
        finally
        {
            if (Directory.Exists(scratch)) scratch.DeleteDirectory();
        }
    }

    // MARK: - Consumer

    // Builds, installs and runs one app, then judges it. `bindingFor` gives the binding the app's build
    // consumed, in the app's configuration; for a binding built with the app (direct, projectref) it
    // checks that build's receipt against the oracle. The app is told the settle bridge that binding
    // routed on its command line, for its sentinel.
    IReadOnlyList<string> RunBridgeConsumerApp(BcfMode mode, AbsolutePath appDir, BcfPlatform platform, BcfRoute element,
        Func<string, BcfBinding> bindingFor)
    {
        var config = RuntimeLane.BuildConfiguration(platform.OnDevice, platform.MonoAot);
        var engine = RuntimeLane.IsNet11 ? RuntimeLane.Engine(platform.OnDevice)
            : platform.OnDevice ? platform.MonoAot ? "mono-aot" : "nativeaot" : "mono-jit";
        var label = $"[{BcfModeName(mode)}] {platform.Name} ({RuntimeLane.Framework} {engine}, {config})";
        var tag = $"{BcfModeName(mode)}-{platform.Name}";

        var failures = new List<string>();
        AbsolutePath appPath;
        BcfBinding binding;
        try
        {
            appPath = BuildPackageConsumer(appDir, BcfAppName, platform.OnDevice, "--bridge-consumer", label, platform.MonoAot);
            binding = bindingFor(config);
        }
        catch (Exception ex)
        {
            failures.Add($"{label}: {ex.Message}");
            return failures;
        }
        var plans = binding.Plans;
        IReadOnlyList<BcfRoute> routes = [binding.Settle, element];

        var result = LaunchPackageConsumer(appPath, BcfAppName, BcfBundleId, platform.OnDevice, "--bridge-consumer", label,
            ["--bcf-settle-bridge", binding.Settle.Bridge]);
        File.WriteAllText(BcfReceipts / $"{tag}-console.txt", result.Output);
        Log.Information("=== CONSUMER OUTPUT ({Label}) ===", label);
        foreach (var line in result.Output.Split('\n').Where(l => l.Contains("BCF CHECK") || l.Contains("BCF SENTINEL") || l.Contains("Runtime flavor")
                     || l.Contains("TEST ")))
            Log.Information("  {Line}", line.TrimEnd());

        if (LaunchDiagnostics.LauncherNeverStartedApp(result))
        {
            failures.Add($"{label}: the launcher never started the app, so nothing was measured.");
            return failures;
        }
        if (RuntimeLane.IsNet11)
            AssertLiveRuntimeLane(result, platform.OnDevice);
        else if (BcfNet10FlavorProblem(result.Output, platform) is { } flavor)
            failures.Add($"{label}: {flavor}");

        failures.AddRange(Regex.Matches(result.Output, @"BCF CHECK (\S+) FAIL ?(.*)").Select(m => $"{label}: in-app check {m.Groups[1].Value} failed: {m.Groups[2].Value.Trim()}"));
        if (result.Result != TestResult.Success)
            failures.Add($"{label}: the app did not report TEST SUCCESS (result {result.Result}).");

        var objRoot = appDir / "obj";
        var analysis = AnalyzeBridgeConsumerStacks(result.Output, routes, BcfExpectedCaller(platform),
            image => UnstrippedImageSymbols(objRoot, platform.Rid, image));
        File.WriteAllText(BcfReceipts / $"{tag}-stacks.txt", analysis.Report);
        Log.Information("{Report}", analysis.Report);
        failures.AddRange(analysis.Problems.Select(p => $"{label}: {p}"));

        // The bridges as linked into the app the phone or simulator ran.
        var frameworks = appPath / "Frameworks";
        var runtimeImage = frameworks / $"{BcfRuntimeLibrary}.framework" / BcfRuntimeLibrary;
        var wrapperImage = frameworks / $"{BcfWrapperModule}.framework" / BcfWrapperModule;
        foreach (var image in new[] { runtimeImage, wrapperImage })
            if (!File.Exists(image))
                failures.Add($"{label}: the app bundle has no {image.Name} at {image}.");
        if (failures.Count == 0)
        {
            ValidateLinkedBridges([runtimeImage], SwiftBridgeProductsDir / "Swift.Runtime.swift-call-plans.json", $"{label} {BcfRuntimeLibrary}");
            ValidateLinkedBridges([wrapperImage], plans, $"{label} {BcfWrapperModule}");
        }
        return failures;
    }

    // .NET 10 has no live-lane identity of its own; the app's runtime-flavor banner names the engine.
    static string? BcfNet10FlavorProblem(string output, BcfPlatform platform)
    {
        var banner = DeviceRuntimeFlavorGate.ExtractBanner(output);
        if (banner is null)
            return "the app printed no runtime-flavor banner, so the engine it ran on is unproven.";
        var mono = banner.Contains("IsMonoRuntime=True", StringComparison.Ordinal);
        var nativeAot = banner.Contains("IsNativeAotRuntime=True", StringComparison.Ordinal);
        var nativeAotLane = platform.OnDevice && !platform.MonoAot;
        return nativeAotLane == nativeAot && mono == !nativeAotLane
            ? null
            : $"the app reported '{banner}', expected {(nativeAotLane ? "NativeAOT" : "Mono")}.";
    }

    sealed record BcfStackAnalysis(string Report, IReadOnlyList<string> Problems);

    // Whether `caller`, the frame under a bridge, is the managed code the lane runs Swift calls from.
    static string? BcfCallerProblem(LaneWitness? caller, BcfCaller expected) => expected switch
    {
        _ when caller is null => "no frame under the bridge",
        BcfCaller.Interpreter => RuntimeLaneIdentity.Classify(caller, BcfAppName) == RuntimeExecutionLane.Interpreter
            ? null : "expected the CoreCLR interpreter's call stub",
        // NativeAOT and Mono AOT compile the managed code, P/Invoke stubs included, into the app.
        _ => caller.Image == BcfAppName ? null : $"expected code compiled into {BcfAppName}",
    };

    // Every measured call's stack: the routed bridge must sit directly under the Swift code and the
    // lane's managed caller directly under the bridge.
    static BcfStackAnalysis AnalyzeBridgeConsumerStacks(string output, IReadOnlyList<BcfRoute> routes, BcfCaller expectedCaller,
        Func<string, IReadOnlyList<ImageSymbol>?> symbolsFor)
    {
        var frameLine = new Regex(@"BCF FRAME (\d+) (\S+) (\d+) 0x([0-9a-f]+) 0x([0-9a-f]+) (\S+) (\S+)", RegexOptions.CultureInvariant);
        var raw = new List<(int Serial, string Tag, int Index, LaneWitness Witness)>();
        foreach (Match m in frameLine.Matches(output))
        {
            var serial = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var index = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            var symbol = m.Groups[7].Value == "-" ? null : m.Groups[7].Value;
            raw.Add((serial, m.Groups[2].Value, index, new LaneWitness($"{m.Groups[2].Value}#{serial}#{index}", m.Groups[6].Value, symbol,
                ulong.Parse(m.Groups[4].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                ulong.Parse(m.Groups[5].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))));
        }
        var resolved = RuntimeLaneIdentity.Symbolicate(raw.Select(r => r.Witness).ToList(), symbolsFor);
        var frames = raw.Select((r, i) => (r.Serial, r.Tag, r.Index, Witness: resolved[i])).ToList();

        var problems = new List<string>();
        var report = new StringBuilder();
        report.AppendLine($"expected caller: {expectedCaller}");
        foreach (var route in routes)
        {
            var calls = frames.Where(f => f.Tag == route.Tag).GroupBy(f => f.Serial).OrderBy(g => g.Key).ToList();
            // Three settle calls (success, error, success) and three element reads. A read may reach
            // object(at:) more than once, so only settle's count is exact.
            const int expected = 3;
            var exact = route.Tag == "settle";
            report.AppendLine($"{route.Tag}: {calls.Count} measured call(s) through {route.Image}!{route.Bridge} -> {route.Target}");
            if (exact ? calls.Count != expected : calls.Count < expected)
                problems.Add($"{route.Tag}: {calls.Count} measured call(s) recorded a stack; expected {(exact ? "exactly" : "at least")} {expected}.");
            foreach (var call in calls)
            {
                var stack = call.OrderBy(f => f.Index).Select(f => f.Witness).ToList();
                var at = stack.FindIndex(w => w.Symbol != null && w.Symbol.StartsWith("SBSR_", StringComparison.Ordinal));
                var caller = at >= 0 && at + 1 < stack.Count ? stack[at + 1] : null;
                var callee = at > 0 ? stack[at - 1] : null;
                var callerProblem = BcfCallerProblem(caller, expectedCaller);
                report.AppendLine($"  call {call.Key}: callee {callee?.Describe() ?? "-"} | bridge {(at >= 0 ? stack[at].Describe() : "none")} | caller {caller?.Describe() ?? "-"} @0x{caller?.Address ?? 0:x} => {callerProblem ?? "ok"}");
                if (at < 0)
                {
                    problems.Add($"{route.Tag} call {call.Key}: no bridge frame on the stack ({string.Join(" < ", stack.Take(8).Select(w => w.Describe()))}).");
                    continue;
                }
                if (stack[at].Symbol != route.Bridge || stack[at].Image != route.Image)
                    problems.Add($"{route.Tag} call {call.Key}: the bridge frame is {stack[at].Describe()}, expected {route.Image}!{route.Bridge}.");
                if (callerProblem is not null)
                    problems.Add($"{route.Tag} call {call.Key}: the bridge's caller is {caller?.Describe() ?? "-"}; {callerProblem}.");
                // The binding's bridge calls the member's own entry point; the getter reaches object(at:) through stdlib internals.
                if (exact && callee?.Symbol != route.Target)
                    problems.Add($"{route.Tag} call {call.Key}: the bridge called {callee?.Describe() ?? "-"}, expected {route.Target}.");
            }
        }
        return new BcfStackAnalysis(report.ToString(), problems);
    }

    // MARK: - Consumer app

    void WriteBridgeConsumerApp(AbsolutePath appDir, BcfMode mode, BcfPlatform platform, BcfRoute element,
        AbsolutePath xcframework, AbsolutePath? projectReference)
    {
        var device = platform.OnDevice;
        var direct = mode == BcfMode.Direct;
        // An SDK-direct app imports SwiftBindings.Sdk, which already emits DisableRuntimeMarshalling (a
        // second one is CS0579); an app that references a binding adds it itself.
        var reference = mode switch
        {
            BcfMode.Package => $"""<PackageReference Include="{BcfPackageId}" Version="{BcfLaneVersion}" />""",
            BcfMode.ProjectReference => $"""<ProjectReference Include="{projectReference}" />""",
            _ => $"""<SwiftFramework Include="{xcframework}" />""",
        };
        var csproj = $"""
            <Project Sdk="{(direct ? $"SwiftBindings.Sdk/{BcfLaneVersion}" : "Microsoft.NET.Sdk")}">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{RuntimeLane.Framework}-ios</TargetFramework>
                <RuntimeIdentifier>{platform.Rid}</RuntimeIdentifier>
                {MixedConsumerRuntimeProperties(RuntimeLane, device, platform.MonoAot)}
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <ApplicationId>{BcfBundleId}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
                {(direct ? "<IsPackable>false</IsPackable>\n<TreatWarningsAsErrors>false</TreatWarningsAsErrors>" : "")}
                <NoWarn>$(NoWarn);CA1416;CA1422{(device ? ";IL2121" : "")}{(direct ? ";CS0649;CS0114;CS8604" : "")}</NoWarn>
              </PropertyGroup>
              <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'iossimulator-arm64'">
                <MtouchLink>None</MtouchLink>
              </PropertyGroup>
              <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'ios-arm64'">
                <CodesignKey>Apple Development: Justin Wojciechowski (2YJ2TQ6Q7Q)</CodesignKey>
                <CodesignProvision>Swift Bindings Dev</CodesignProvision>
                <TeamIdentifierPrefix>HM68F9PH26</TeamIdentifierPrefix>
              </PropertyGroup>
              {(direct ? "" : """<ItemGroup><AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" /></ItemGroup>""")}
              <ItemGroup>
                <None Include="Info.plist" />
              </ItemGroup>
              <ItemGroup>
                {reference}
              </ItemGroup>
            </Project>
            """;
        if (csproj.Contains("SwiftLoweringEvidence", StringComparison.Ordinal))
            throw new InvalidOperationException("--bridge-consumer: the ordinary consumer must not carry hand-authored lowering evidence.");
        File.WriteAllText(appDir / $"{BcfAppName}.csproj", csproj);

        var program = $$"""
            // Copyright (c) 2026 Justin Wojciechowski.
            // Licensed under the MIT License.
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using BridgeConsumerFixture;
            using CoreFoundation;
            using Foundation;
            using Swift.Runtime;
            using UIKit;

            namespace BridgeConsumerApp;

            public static class Application
            {
                static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
            }

            public class AppDelegate : UIApplicationDelegate
            {
                public override bool FinishedLaunching(UIApplication application, NSDictionary? launchOptions) => true;

                [System.Diagnostics.CodeAnalysis.DynamicDependency(
                    System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces, typeof(SceneDelegate))]
                public override UISceneConfiguration GetConfiguration(UIApplication application,
                    UISceneSession connectingSceneSession, UISceneConnectionOptions options) =>
                    new("Default Configuration", connectingSceneSession.Role)
                    {
                        DelegateClass = new ObjCRuntime.Class(typeof(SceneDelegate))
                    };
            }

            [Register("SceneDelegate")]
            public class SceneDelegate : UIWindowSceneDelegate
            {
                private static bool _probeStarted;

                public override UIWindow? Window { get; set; }

                public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
                {
                    if (scene is not UIWindowScene windowScene)
                        return;
                    Window = new UIWindow(windowScene);
                    Window.RootViewController = new UIViewController();
                    Window.MakeKeyAndVisible();
                    if (_probeStarted)
                        return;
                    _probeStarted = true;
                    DispatchQueue.MainQueue.DispatchAsync(Probe.Run);
                }
            }

            static unsafe class Probe
            {
                const string ElementBridge = "{{element.Bridge}}";
                static int _failures;

                static void Check(string name, bool ok, string detail)
                {
                    Console.WriteLine($"BCF CHECK {name} {(ok ? "ok" : "FAIL")} {detail}");
                    if (!ok) _failures++;
                }

                public static void Run()
                {
                    try
                    {
                        {{MixedConsumerRuntimeBanner()}}
                        Functions.GetBridgeConsumerTakeCaptures();
                        var liveBefore = Functions.GetBridgeConsumerLiveObjects();
                        Measure();
                        Console.WriteLine(Functions.GetBridgeConsumerTakeCaptures());
                        Sentinels();
                        // The sentinel calls record stacks under the sentinel, not managed code; drop them.
                        Functions.GetBridgeConsumerTakeCaptures();
                        for (var i = 0; i < 3; i++)
                        {
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                        }
                        var liveAfter = Functions.GetBridgeConsumerLiveObjects();
                        Check("ownership-balanced", liveAfter == liveBefore, $"live objects before {liveBefore}, after {liveAfter}");
                        Console.WriteLine("RESULTS FLUSHED");
                        Console.WriteLine(_failures == 0 ? "TEST SUCCESS" : $"TEST FAILURE: {_failures} check(s) failed");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("RESULTS FLUSHED");
                        Console.WriteLine("TEST FAILURE: " + ex);
                    }
                }

                // self + error + scalar result, and the array getter, each through its bridge from managed code.
                // The constructors take int arguments on purpose: in SDK-direct mode this code compiles into
                // the binding's own assembly, so a handle-adopting constructor visible there would capture
                // the call ahead of the public Int64 initializer and adopt the integer as an object pointer.
                [MethodImpl(MethodImplOptions.NoInlining)]
                static void Measure()
                {
                    int opening = 7;
                    using (var account = new BridgeAccount(opening))
                    {
                        Check("ctor-int-argument", account.Total == opening, $"total {account.Total} after new BridgeAccount({opening})");
                        var handle = ((ISwiftObject)account).SwiftHandle;
                        account.Pending = 5;
                        var first = account.Settled;
                        Check("settle-success", first == 12 && account.Total == 12 && account.Pending == 0,
                            $"returned {first}, total {account.Total}, pending {account.Pending}");

                        account.Pending = -4;
                        string? thrown = null;
                        try
                        {
                            var unexpected = account.Settled;
                            thrown = $"no exception (returned {unexpected})";
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"BCF ERROR {ex.GetType().FullName}: {ex.Message}");
                            (ex as IDisposable)?.Dispose();
                        }
                        Check("settle-error", thrown == null && account.Total == 12 && account.Pending == -4,
                            $"{thrown ?? "threw"}, total {account.Total}, pending {account.Pending}");

                        account.Pending = 3;
                        var second = account.Settled;
                        Check("settle-after-error", second == 15 && account.Total == 15 && account.Pending == 0,
                            $"returned {second}, total {account.Total}, pending {account.Pending}");
                        Check("receiver-intact", ((ISwiftObject)account).SwiftHandle == handle, "the receiver's handle survived the error");
                    }

                    // The binding projects the array as a read-only list over the Swift array it owns;
                    // the projection is disposable, and every indexer read is one subscript-getter call.
                    var items = Functions.BridgeConsumerItems(3, 40);
                    using (var lease = (IDisposable)items)
                    {
                        Check("items-count", items.Count == 3, $"count {items.Count}");
                        var readsBefore = Functions.GetBridgeConsumerElementReads();
                        var values = new long[3];
                        for (var i = 0; i < 3; i++)
                        {
                            using var item = items[i];
                            values[i] = item.Value;
                        }
                        var reads = Functions.GetBridgeConsumerElementReads() - readsBefore;
                        Check("items-cocoa-backed", reads == 3, $"{reads} of 3 element reads reached the NSArray");
                        Check("items-values", values[0] == 40 && values[1] == 41 && values[2] == 42, string.Join(",", values));
                    }
                }

                [DllImport("/usr/lib/swift/libswiftCore.dylib")]
                static extern void swift_release(nint obj);

                [DllImport("/usr/lib/swift/libswiftCore.dylib")]
                static extern void swift_errorRelease(nint error);

                static nint Export(string framework, string symbol)
                {
                    var path = Path.Combine(NSBundle.MainBundle.PrivateFrameworksPath!, framework + ".framework", framework);
                    return NativeLibrary.GetExport(NativeLibrary.Load(path), symbol);
                }

                // The settle bridge the binding's build routed, which the host read from its sidecar and
                // passes on the command line.
                static string SettleBridge()
                {
                    var arguments = NSProcessInfo.ProcessInfo.Arguments;
                    var at = Array.IndexOf(arguments, "--bcf-settle-bridge");
                    if (at < 0 || at + 1 >= arguments.Length)
                        throw new InvalidOperationException("launched without --bcf-settle-bridge <symbol>");
                    return arguments[at + 1];
                }

                // Each bridge called from native code that loads sentinels into x19-x28 and d8-d15 and
                // reports any the bridge did not preserve.
                [MethodImpl(MethodImplOptions.NoInlining)]
                static void Sentinels()
                {
                    var sentinel = (delegate* unmanaged<nint, ulong*, ulong*, ulong>)Export("{{BcfModule}}", "bcf_bridge_sentinel");
                    var settleBridge = Export("{{BcfWrapperModule}}", SettleBridge());
                    var elementBridge = Export("{{BcfRuntimeLibrary}}", ElementBridge);
                    ulong* args = stackalloc ulong[8];
                    ulong result = 0;

                    int opening = 100;
                    using (var account = new BridgeAccount(opening))
                    {
                        var self = (ulong)((ISwiftObject)account).SwiftHandle;
                        nint error = 0;
                        account.Pending = 5;
                        args[0] = self; args[1] = (ulong)&error;
                        var mask = sentinel(settleBridge, args, &result);
                        Check("sentinel-settle", mask == 0 && result == 105 && error == 0 && account.Total == 105,
                            $"mask 0x{mask:x}, result {result}, error 0x{error:x}, total {account.Total}");
                        Console.WriteLine($"BCF SENTINEL settle mask=0x{mask:x}");

                        error = 0;
                        account.Pending = -2;
                        args[0] = self; args[1] = (ulong)&error;
                        mask = sentinel(settleBridge, args, &result);
                        Check("sentinel-settle-error", mask == 0 && error != 0 && account.Total == 105,
                            $"mask 0x{mask:x}, error 0x{error:x}, total {account.Total}");
                        Console.WriteLine($"BCF SENTINEL settle-error mask=0x{mask:x}");
                        if (error != 0) swift_errorRelease(error);
                    }

                    // The projection keeps its Swift array private, so the sentinel reads an array of the
                    // same element type the app builds itself: the getter, and so its bridge, is the same.
                    int firstValue = 7, secondValue = 8;
                    using (var first = new BridgeItem(firstValue))
                    using (var second = new BridgeItem(secondValue))
                    using (var items = new Swift.SwiftArray<BridgeItem>(new[] { first, second }))
                    {
                        using var payload = items.PayloadBuffer;
                        nint element = 0;
                        args[0] = 1; args[1] = (ulong)payload.Buffer;
                        args[2] = (ulong)TypeMetadata.GetTypeMetadataOrThrow<BridgeItem>().Handle; args[3] = (ulong)&element;
                        var mask = sentinel(elementBridge, args, &result);
                        var expected = ((ISwiftObject)second).SwiftHandle;
                        Check("sentinel-element", mask == 0 && element == expected, $"mask 0x{mask:x}, element 0x{element:x}, expected 0x{expected:x}");
                        Console.WriteLine($"BCF SENTINEL element mask=0x{mask:x}");
                        if (element != 0) swift_release(element);
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
            "    <key>UIApplicationSceneManifest</key>\n" +
            "    <dict>\n" +
            "        <key>UIApplicationSupportsMultipleScenes</key>\n" +
            "        <false/>\n" +
            "        <key>UISceneConfigurations</key>\n" +
            "        <dict>\n" +
            "            <key>UIWindowSceneSessionRoleApplication</key>\n" +
            "            <array>\n" +
            "                <dict>\n" +
            "                    <key>UISceneConfigurationName</key>\n" +
            "                    <string>Default Configuration</string>\n" +
            "                    <key>UISceneDelegateClassName</key>\n" +
            "                    <string>SceneDelegate</string>\n" +
            "                </dict>\n" +
            "            </array>\n" +
            "        </dict>\n" +
            "    </dict>\n" +
            "</dict>\n</plist>\n");
    }
}
