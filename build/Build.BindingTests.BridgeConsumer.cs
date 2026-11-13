// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.BridgeConsumer.cs — opt-in ordinary consumer of the Swift bridges, on .NET 10 and
// .NET 11 for iOS and Mac Catalyst.
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
// AOT-compiles on the arm64 simulator as well as the device; on Mac Catalyst, the code Mono's JIT or
// interpreter generates at run time). The app also calls both bridges from a register sentinel and checks
// the module's live-object count before and after. The host checks each mode's SDK-collected receipt
// against the oracle and validates the bridges of the packed and linked images.
//
// Mac Catalyst (--catalyst) binds a universal (arm64 + x86_64) fixture and builds a universal app, whose
// per-RID builds each bind the module before the workload merges them: one managed assembly serves both
// slices, so every RID's build must route the same members, and the merged app runs natively on arm64
// and under Rosetta on x86_64.
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
using SwiftBindings.TransitionGate;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    [Parameter("Opt-in: bind a module through SwiftBindings.Sdk with default settings and no hand-authored lowering evidence, consume it as a package, SDK-direct and through a ProjectReference, and prove on the simulator, a device and/or Mac Catalyst that its calls run through the bridges the SDK routed. .NET 10: --sim (Mono AOT), --device (NativeAOT), --device --mono-aot (Mono full-AOT), --catalyst (a universal app run natively on arm64 and under Rosetta on x86_64). .NET 11: --coreclr-interp. Never part of the default run or --compile-only.")]
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
    enum BcfCaller { Interpreter, App, MonoInterpreter, MonoJit }

    /// <summary>One platform a mode builds an app for. A Mac Catalyst app is universal.</summary>
    sealed record BcfPlatform(bool OnDevice, bool MonoAot, bool Catalyst = false)
    {
        public string Name => Catalyst ? "catalyst" : OnDevice ? MonoAot ? "device-monoaot" : "device" : "simulator";

        /// <summary>The slices of the built app the host runs, each by its RID.</summary>
        public IReadOnlyList<BcfRun> Runs => Catalyst
            ? [new BcfRun("catalyst-arm64", "maccatalyst-arm64", UnderRosetta: false), new BcfRun("catalyst-x64", "maccatalyst-x64", UnderRosetta: true)]
            : [new BcfRun(Name, OnDevice ? "ios-arm64" : "iossimulator-arm64", UnderRosetta: false)];
    }

    /// <summary>One run of a built app: the slice it runs, natively or under Rosetta.</summary>
    sealed record BcfRun(string Name, string Rid, bool UnderRosetta);

    /// <summary>
    /// The platforms bound from one fixture xcframework: its slices, the oracle that cross-checks them,
    /// and the binding's target framework.
    /// </summary>
    sealed record BcfFamily(string Platform, IReadOnlyList<BcfPlatform> Platforms, AbsolutePath Root, AbsolutePath Receipts)
    {
        public bool Catalyst => Platform == "maccatalyst";

        /// <summary>The evidence slices the fixture ships, by the triple each is compiled for.</summary>
        public IReadOnlyList<string> SliceTriples => Catalyst
            ? [ApplePlatform.MacCatalyst.SimulatorTarget, ApplePlatform.MacCatalystX64.SimulatorTarget]
            : [ApplePlatform.IOS.DeviceTarget!, ApplePlatform.IOS.SimulatorTarget];
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

    // .NET 11 runs the CoreCLR interpreter everywhere this leg runs. .NET 10 runs Mono, which the SDK
    // AOT-compiles on the device, the arm64 simulator and the arm64 Mac Catalyst slice alike, so the
    // P/Invoke wrapper is in the app image. The x86_64 Mac Catalyst slice is JIT-compiled, except that
    // a binding package's targets turn the Mono interpreter on there, because that JIT has known
    // Swift-interop defects; the interpreter's native-call path, linked into the app, then calls the
    // bridge. An SDK-direct app and a plain ProjectReference consumer import no such targets.
    BcfCaller BcfExpectedCaller(BcfPlatform platform, BcfMode mode, BcfRun run) =>
        RuntimeLane.IsNet11 ? BcfCaller.Interpreter
        : platform.Catalyst && run.Rid == "maccatalyst-x64" ? mode == BcfMode.Package ? BcfCaller.MonoInterpreter : BcfCaller.MonoJit
        : BcfCaller.App;

    void RunBridgeConsumerLeg(bool sim, bool device, bool monoAot, bool catalyst)
    {
        var modes = ParseBridgeConsumerModes(BridgeConsumerModes);
        Log.Information("=================================================");
        Log.Information(" BindingTests — ordinary consumer of the Swift bridges (--bridge-consumer)");
        Log.Information("   {Framework}   sim: {Sim}   device: {Device}{MonoAot}   catalyst: {Catalyst}   modes: {Modes}", RuntimeLane.Framework, sim, device,
            monoAot ? " (Mono full-AOT)" : "", catalyst, string.Join(", ", modes.Select(BcfModeName)));
        Log.Information("=================================================");

        if (RuntimeLane.IsNet11 && !RuntimeLane.CoreClrInterpreter)
            throw new Exception("--bridge-consumer measures the .NET 11 CoreCLR interpreter on .NET 11. Run it with --coreclr-interp: " +
                "bash build.sh --toolchain-profile net11-xcode27 -- binding-tests --bridge-consumer --coreclr-interp [--sim] [--device] [--catalyst]");
        // Also stages SwiftInterfaceParser, which the SDK package ships and every binding build runs.
        EnsureGeneratorBuilt();
        var stagedParser = SwiftInterfaceParserStagingDir / "SwiftInterfaceParser";
        if (!File.Exists(stagedParser))
            throw new InvalidOperationException($"--bridge-consumer: SwiftInterfaceParser is missing at '{stagedParser}'.");

        if (Directory.Exists(BcfScratch)) BcfScratch.DeleteDirectory();
        BcfReceipts.CreateDirectory();
        var nupkgDir = BcfScratch / "packages";
        nupkgDir.CreateDirectory();

        // Each family binds its own fixture xcframework: iOS (device + simulator slices) and Mac
        // Catalyst (one universal slice).
        var families = new List<BcfFamily>();
        var ios = new List<BcfPlatform>();
        if (sim) ios.Add(new BcfPlatform(OnDevice: false, MonoAot: false));
        if (device) ios.Add(new BcfPlatform(OnDevice: true, MonoAot: monoAot));
        if (ios.Count > 0)
            families.Add(new BcfFamily("ios", ios, BcfScratch / "ios", BcfReceipts / "ios"));
        if (catalyst)
            families.Add(new BcfFamily("maccatalyst", [new BcfPlatform(OnDevice: false, MonoAot: false, Catalyst: true)],
                BcfScratch / "maccatalyst", BcfReceipts / "maccatalyst"));

        var failures = new List<string>();
        var passed = false;
        try
        {
            using var scope = new VersionScope(BcfLaneVersion, RootDirectory, BcfLaneAppleVersion);
            BuildLocalPackageFeed(nupkgDir, scope, "bridge-consumer", BcfLaneVersion, BcfLaneAppleVersion, BcfPackageId);
            AssertRuntimeNativePackage(nupkgDir / $"SwiftBindings.Runtime.{BcfLaneVersion}.nupkg");
            var element = BridgeConsumerElementRoute();

            foreach (var family in families)
            {
                family.Root.CreateDirectory();
                family.Receipts.CreateDirectory();
                var xcframework = BuildBridgeConsumerXcframework(family);

                // An independent cross-check, never an input: the oracle records what swiftc gives each of the
                // fixture's plan targets on every slice, and each SDK-collected receipt must agree with it.
                var oracle = RecordBridgeConsumerOracle(family, xcframework);

                foreach (var mode in modes)
                    failures.AddRange(mode switch
                    {
                        BcfMode.Package => RunBridgeConsumerPackage(family, nupkgDir, xcframework, oracle, element),
                        BcfMode.Direct => RunBridgeConsumerDirect(family, nupkgDir, xcframework, oracle, element),
                        _ => RunBridgeConsumerProjectReference(family, nupkgDir, xcframework, oracle, element),
                    });
            }

            if (failures.Count > 0)
            {
                foreach (var f in failures) Log.Error("  ✗ {Failure}", f);
                throw new Exception($"--bridge-consumer: {failures.Count} problem(s); receipts under {BcfReceipts}.");
            }
            Log.Information("--bridge-consumer PASSED ({Modes} on {Platforms}). Receipts: {Dir}",
                string.Join(", ", modes.Select(BcfModeName)),
                string.Join(", ", families.SelectMany(f => f.Platforms).SelectMany(p => p.Runs).Select(r => r.Name)), BcfReceipts);
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
    IEnumerable<string> RunBridgeConsumerPackage(BcfFamily family, AbsolutePath nupkgDir, AbsolutePath xcframework, AbsolutePath oracle,
        BcfRoute element)
    {
        var fixtureDir = family.Root / "package-binding";
        var fixtureOut = family.Root / "package-output";
        fixtureDir.CreateDirectory();
        fixtureOut.CreateDirectory();
        // Each family packs the binding under the same id and version: restore must not reuse the
        // previous family's copy from the global packages folder.
        EvictBridgeConsumerBindingPackage();
        WriteBridgeConsumerBinding(family, fixtureDir, nupkgDir, xcframework, packable: true);
        Log.Information("=== bridge-consumer [package] {Platform}: packing the binding with default settings ===", family.Platform);
        DotNetPack(s => s
            .SetProject(fixtureDir / $"{BcfModule}.csproj")
            .SetConfiguration("Release")
            .SetOutputDirectory(fixtureOut)
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));

        var binding = BridgeConsumerBinding(family, BcfMode.Package, fixtureDir, "Release", oracle, appRids: []);
        AssertBridgeConsumerPackedWrapper(family, fixtureOut, binding.Plans);

        var failures = new List<string>();
        foreach (var platform in family.Platforms)
        {
            var appDir = family.Root / $"package-app-{platform.Name}";
            appDir.CreateDirectory();
            WriteBridgeConsumerApp(appDir, BcfMode.Package, platform, element, xcframework, projectReference: null);
            File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureOut, "BridgeConsumerFixture.*"));
            failures.AddRange(RunBridgeConsumerApp(family, BcfMode.Package, appDir, platform, element, _ => binding));
        }
        return failures;
    }

    static void EvictBridgeConsumerBindingPackage()
    {
        var cached = (AbsolutePath)(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"))
            / BcfPackageId.ToLowerInvariant();
        foreach (var version in Directory.Exists(cached) ? Directory.GetDirectories(cached) : [])
            if (Path.GetFileName(version).StartsWith(BcfVersion, StringComparison.Ordinal))
                ((AbsolutePath)version).DeleteDirectory();
    }

    // The app is the binding: its own csproj imports SwiftBindings.Sdk, so each platform's build
    // generates, collects its own receipt and routes. A universal Catalyst app does so once per RID.
    IEnumerable<string> RunBridgeConsumerDirect(BcfFamily family, AbsolutePath nupkgDir, AbsolutePath xcframework, AbsolutePath oracle,
        BcfRoute element)
    {
        var failures = new List<string>();
        foreach (var platform in family.Platforms)
        {
            var appDir = family.Root / $"direct-app-{platform.Name}";
            appDir.CreateDirectory();
            File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));
            WriteBridgeConsumerApp(appDir, BcfMode.Direct, platform, element, xcframework, projectReference: null);
            failures.AddRange(RunBridgeConsumerApp(family, BcfMode.Direct, appDir, platform, element,
                configuration => BridgeConsumerBinding(family, BcfMode.Direct, appDir, configuration, oracle,
                    appRids: platform.Runs.Select(r => r.Rid).ToList())));
        }
        return failures;
    }

    // The app takes a ProjectReference to a binding project, which the app's build builds in its own configuration.
    IEnumerable<string> RunBridgeConsumerProjectReference(BcfFamily family, AbsolutePath nupkgDir, AbsolutePath xcframework, AbsolutePath oracle,
        BcfRoute element)
    {
        var fixtureDir = family.Root / "projectref-binding";
        fixtureDir.CreateDirectory();
        WriteBridgeConsumerBinding(family, fixtureDir, nupkgDir, xcframework, packable: false);
        var failures = new List<string>();
        foreach (var platform in family.Platforms)
        {
            var appDir = family.Root / $"projectref-app-{platform.Name}";
            appDir.CreateDirectory();
            File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));
            WriteBridgeConsumerApp(appDir, BcfMode.ProjectReference, platform, element, xcframework,
                projectReference: fixtureDir / $"{BcfModule}.csproj");
            failures.AddRange(RunBridgeConsumerApp(family, BcfMode.ProjectReference, appDir, platform, element,
                configuration => BridgeConsumerBinding(family, BcfMode.ProjectReference, fixtureDir, configuration, oracle,
                    appRids: platform.Runs.Select(r => r.Rid).ToList())));
        }
        return failures;
    }

    // The binding project an ordinary consumer writes: SwiftBindings.Sdk, one <SwiftFramework>, defaults.
    void WriteBridgeConsumerBinding(BcfFamily family, AbsolutePath fixtureDir, AbsolutePath nupkgDir, AbsolutePath xcframework, bool packable)
    {
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{BcfLaneVersion}">
              <PropertyGroup>
                <TargetFramework>{RuntimeLane.Framework}-{family.Platform}</TargetFramework>
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

    // What the binding builds in `projectDir` produced under `configuration`: each one's sidecar that
    // routed the module and the receipt that authorized it. Each receipt must agree with the oracle and
    // show the settle target on every slice the fixture ships, and each sidecar must route settle. A
    // universal Catalyst app that is its own binding builds it once per RID (`appRids`) before the
    // workload merges the slices: every RID's build must exist and route exactly the same members
    // through the same bridges, so the app reaches Swift the same way on every slice.
    BcfBinding BridgeConsumerBinding(BcfFamily family, BcfMode mode, AbsolutePath projectDir, string configuration, AbsolutePath oracle,
        IReadOnlyList<string> appRids)
    {
        var obj = projectDir / "obj" / configuration;
        var label = $"[{BcfModeName(mode)}] {family.Platform} {projectDir.Name} ({configuration})";
        var plansName = $"{BcfModule}.swift-call-plans.json";
        // Each binding build writes its products under its own swift-binding/ intermediate directory.
        var builds = (Directory.Exists(obj) ? Directory.GetFiles(obj, plansName, SearchOption.AllDirectories) : [])
            .Select(p => BcfBindingDirectory((AbsolutePath)p))
            .Distinct()
            .OrderBy(d => d.ToString(), StringComparer.Ordinal)
            .ToList();
        if (builds.Count == 0)
            throw new Exception($"--bridge-consumer {label}: no binding build under {obj} wrote {plansName}.");

        // A universal app builds a binding it is itself once per RID, and each RID's build must exist;
        // a referenced binding project is built either once or once per RID, as the workload decides.
        // The universal app's outer build, which only orchestrates the per-RID builds and the merge,
        // also generates the binding (generation runs ahead of reference resolution) but compiles and
        // ships nothing, so it is held to the same routing and receipt but has no guards to judge.
        string? RidOf(AbsolutePath bindingDir) => appRids.Contains(bindingDir.Parent.Name) ? bindingDir.Parent.Name : null;
        var ridBuilds = builds.Where(b => RidOf(b) is not null).ToList();
        var outerBuilds = builds.Where(b => RidOf(b) is null).ToList();
        var perRid = appRids.Count > 1 && ridBuilds.Count == appRids.Count && appRids.All(r => ridBuilds.Any(b => RidOf(b) == r))
            && outerBuilds.Count <= 1;
        var requirePerRid = mode == BcfMode.Direct && appRids.Count > 1;
        if (!perRid && (requirePerRid || builds.Count != 1))
            throw new Exception($"--bridge-consumer {label}: expected {(requirePerRid ? $"one binding build per app RID ({string.Join(", ", appRids)}), plus at most the universal outer build's," : "one binding build")} " +
                $"under {obj}; found {builds.Count}: {string.Join(", ", builds)}.");

        BcfBinding? chosen = null;
        List<string>? firstRouting = null;
        foreach (var bindingDir in builds)
        {
            var rid = perRid ? RidOf(bindingDir) : null;
            var outer = perRid && rid is null;
            var copyLabel = rid is not null ? $"{label} {rid}" : outer ? $"{label} outer build" : label;
            AbsolutePath One(string fileName)
            {
                var found = Directory.GetFiles(bindingDir, fileName, SearchOption.AllDirectories);
                if (found.Length != 1)
                    throw new Exception($"--bridge-consumer {copyLabel}: expected one {fileName} under {bindingDir}, found {found.Length}.");
                return (AbsolutePath)found[0];
            }

            var plans = One(plansName);
            var receipt = One("swift-lowering-receipt.json");
            var journal = One($"{BcfModule}.swift-bridge-routing.json");
            var tag = $"{BcfModeName(mode)}-{configuration.ToLowerInvariant()}{(rid is not null ? $"-{rid}" : outer ? "-outer" : "")}";
            File.Copy(plans, family.Receipts / $"{tag}-{plans.Name}", overwrite: true);
            File.Copy(receipt, family.Receipts / $"{tag}-{receipt.Name}", overwrite: true);
            File.Copy(journal, family.Receipts / $"{tag}-{journal.Name}", overwrite: true);

            AssertBridgeConsumerFullyRouted(copyLabel, plans);
            // The assembly this build compiled the binding into sits beside its swift-binding/ directory.
            var assemblyName = mode == BcfMode.Direct ? BcfAppName : BcfModule;
            if (!outer)
                AssertBridgeConsumerResidualGuards(family, copyLabel, tag, bindingDir.Parent / $"{assemblyName}.dll",
                    One($"{BcfModule}.swift-transition-guards.json").Parent, plans);
            var settle = BridgeConsumerSettleRoute(plans);
            AssertBridgeConsumerCollectedReceipt(family, copyLabel, receipt, oracle, settle);

            var routing = BcfBridged(plans).Select(e => $"{e.GetProperty("member").GetString()} -> {e.GetProperty("symbol").GetString()}")
                .Order(StringComparer.Ordinal).ToList();
            if (firstRouting is null)
                firstRouting = routing;
            else if (!routing.SequenceEqual(firstRouting))
                throw new Exception($"--bridge-consumer {copyLabel}: this build routed different members than {builds[0].Parent.Name}'s: " +
                    $"only here [{string.Join(", ", routing.Except(firstRouting))}], only there [{string.Join(", ", firstRouting.Except(routing))}].");

            // Any build serves the run's settle bridge: they all route the same member through the same bridge.
            if (!outer)
                chosen ??= new BcfBinding(plans, receipt, settle);
        }
        if (builds.Count > 1)
            Log.Information("  {Label}: {Count} binding builds ({Builds}) route the same {Routed} member(s) through the same bridges", label, builds.Count,
                string.Join(", ", builds.Select(b => RidOf(b) ?? "outer")), firstRouting!.Count);
        return chosen!;
    }

    // The swift-binding/ intermediate directory a binding build's product sits in.
    static AbsolutePath BcfBindingDirectory(AbsolutePath product)
    {
        for (var dir = product.Parent; dir != null; dir = dir.Parent)
            if (dir.Name == "swift-binding")
                return dir;
        throw new Exception($"--bridge-consumer: {product} is not under a swift-binding/ intermediate directory.");
    }

    // The residual-guard check over the assembly this consumer build compiled the binding into, after
    // the SDK's wrapper compile routed what its receipt authorized: every Swift-convention transition
    // still direct is reachable only through a member that refuses first where it is unsafe, and the
    // guards agree with the sidecar the finalizer rewrote (a routed transition's guard refuses nothing).
    void AssertBridgeConsumerResidualGuards(BcfFamily family, string label, string tag, AbsolutePath assembly, AbsolutePath sidecars,
        AbsolutePath plans)
    {
        if (!File.Exists(assembly))
            throw new Exception($"--bridge-consumer {label}: no compiled {assembly.Name} at {assembly} to check for residual guards.");
        var nugetCache = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var runtime = Directory.GetFiles(Path.Combine(nugetCache, "swiftbindings.runtime", BcfLaneVersion), "Swift.Runtime.dll", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName).FirstOrDefault()
            ?? throw new Exception($"--bridge-consumer {label}: SwiftBindings.Runtime {BcfLaneVersion} is not in {nugetCache}.");
        var report = SwiftTransitionScanner.Scan(assembly, new SwiftTransitionScanOptions
        {
            ReferenceDirectories = [assembly.Parent, runtime!, System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()],
        });
        var result = ResidualGuardCheck.Check(assembly, report);
        var problems = ResidualGuardCheck.CrossCheckSidecars(result, sidecars, []);
        var summary = ResidualGuardCheck.ToSummary(result) + string.Concat(problems.Select(p => $"  sidecar {p}\n"));
        File.WriteAllText(family.Receipts / $"{tag}-residual-guards.txt", summary);
        var errors = report.InspectionErrors.Select(e => $"inspection error {e.Member}: {e.Message}")
            .Concat(result.Violations.Select(v => $"{v.Rule} {v.Member}: {v.Reason}"))
            .Concat(result.Errors.Select(e => $"inspection error {e.Member}: {e.Message}"))
            .Concat(problems.Select(p => $"sidecar {p}"))
            .ToList();
        if (errors.Count > 0)
            throw new Exception($"--bridge-consumer {label}: the residual-guard check failed on {assembly}:\n  " + string.Join("\n  ", errors));

        // What the binding hands an ordinary app: the members its plans cover, how many reach Swift
        // through a bridge, and how many guard calls are left that can refuse.
        var coverage = JsonDocument.Parse(File.ReadAllText(plans)).RootElement.GetProperty("coverage").EnumerateArray().ToList();
        var planned = coverage.Count(e => e.GetProperty("status").GetString() == "Planned");
        var routed = coverage.Count(e => e.TryGetProperty("bridged", out var b) && b.ValueKind == JsonValueKind.True);
        var line = $"{RuntimeLane.Framework} {tag}: {planned} planned member(s), {routed} routed through a bridge; " +
                   $"{result.Hazards.Length} direct transition(s), {result.Holders.Length} holder(s), {result.GuardCalls} guard call(s), " +
                   $"{result.RoutedGuardCalls} refusing nothing, {result.GuardCalls - result.RoutedGuardCalls} able to refuse";
        File.AppendAllText(family.Receipts / "routing-summary.txt", line + "\n");
        Log.Information("  {Label}: residual guards passed — {Line}", label, line);
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

    // The family's fixture framework. iOS: a two-slice (device + simulator) xcframework, each slice
    // linking the register sentinel. Mac Catalyst: one universal (arm64 + x86_64) slice, as real
    // libraries ship it; the sentinel is arm64 code, so only the arm64 half links it.
    AbsolutePath BuildBridgeConsumerXcframework(BcfFamily family)
    {
        var buildRoot = family.Root / "build";
        buildRoot.CreateDirectory();
        var sourceDir = BindingTestsDir / "Sources" / BcfModule;
        var swiftSources = Directory.GetFiles(sourceDir, "*.swift").OrderBy(f => f, StringComparer.Ordinal).ToList();
        var sentinel = sourceDir / "bridge-sentinel-arm64.S";
        if (swiftSources.Count == 0 || !File.Exists(sentinel))
            throw new Exception($"--bridge-consumer: fixture sources missing under {sourceDir}.");

        var create = new CreateXcframeworkSettings();
        if (family.Catalyst)
        {
            var catalyst = ApplePlatform.MacCatalyst;
            var sentinelObject = buildRoot / "bridge-sentinel-arm64.o";
            XcRunTool($"clang -c \"{sentinel}\" -o \"{sentinelObject}\" -target {catalyst.SimulatorTarget} " +
                $"-isysroot \"{XcRun.GetSdkPath(catalyst.SimulatorSdkName)}\"");
            create = create.AddFrameworkPath(BuildFatFrameworkSlice(buildRoot, "maccatalyst", swiftSources,
                armTarget: catalyst.SimulatorTarget, armSuffix: catalyst.SimulatorModuleSuffix,
                x64Target: ApplePlatform.MacCatalystX64.SimulatorTarget, x64Suffix: ApplePlatform.MacCatalystX64.SimulatorModuleSuffix,
                sdkName: catalyst.SimulatorSdkName, minOs: catalyst.MinOsVersion, plistPlatform: catalyst.SimulatorPlistPlatform,
                module: BcfModule, armExtraSources: [sentinelObject.ToString()]));
        }
        else
        {
            var ios = ApplePlatform.IOS;
            var slices = new[]
            {
                (Id: ios.DeviceSliceId!, Target: ios.DeviceTarget!, Sdk: ios.DeviceSdkName!, Suffix: ios.DeviceModuleSuffix!, Plist: ios.DevicePlistPlatform!),
                (Id: ios.SimulatorSliceId, Target: ios.SimulatorTarget, Sdk: ios.SimulatorSdkName, Suffix: ios.SimulatorModuleSuffix, Plist: ios.SimulatorPlistPlatform),
            };
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
        }
        var xcframework = family.Root / "native" / $"{BcfModule}.xcframework";
        xcframework.Parent.CreateDirectory();
        XcodeBuild.ExecuteCreateXcframework(create.SetOutputPath(xcframework));
        return xcframework;
    }

    // Generates once to learn the fixture's plan targets, then lets the oracle record what swiftc
    // gives each one on every slice of the family. The result is compared with, never fed to, the SDK.
    AbsolutePath RecordBridgeConsumerOracle(BcfFamily family, AbsolutePath xcframework)
    {
        var planDir = family.Root / "plan-generation";
        planDir.CreateDirectory();
        Log.Information("=== bridge-consumer {Platform}: generating once to learn the fixture's call plans ===", family.Platform);
        var args = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{xcframework}\"",
            $"-o \"{planDir}\"",
            $"--platform {family.Platform}",
            $"--target-framework {RuntimeLane.Framework}-{family.Platform}",
            "--skip-wrapper-compilation",
            "--no-verify-csharp",
        };
        var proc = ProcessTasks.StartProcess("dotnet", string.Join(" ", args), workingDirectory: RootDirectory, logOutput: false);
        proc.WaitForExit();
        var log = family.Receipts / "plan-generation.log";
        File.WriteAllLines(log, proc.Output.Select(o => o.Text));
        if (proc.ExitCode != 0)
            throw new Exception($"--bridge-consumer: the generator exited {proc.ExitCode}; log at {log}.");
        var sidecar = planDir / $"{BcfModule}.swift-call-plans.json";
        var wrapper = planDir / $"{BcfModule}.Wrapper.swift";
        foreach (var required in new[] { sidecar, wrapper })
            if (!File.Exists(required))
                throw new Exception($"--bridge-consumer: the generator wrote no {required.Name} in {planDir}.");

        var evidence = family.Root / "oracle-lowering-evidence.json";
        var reports = family.Receipts / "oracle";
        var manifest = family.Root / "oracle-corpus.json";
        var compiled = family.Root / "oracle-compile";
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            platform = family.Platform,
            mainModule = BcfModule,
            main = Directory.GetFiles(BindingTestsDir / "Sources" / BcfModule, "*.swift").OrderBy(f => f, StringComparer.Ordinal),
            reports = reports.ToString(),
            scratch = compiled.ToString(),
            wrappers = new Dictionary<string, string> { [BcfWrapperModule] = wrapper },
            probe = (SourceDir / "Swift.Bindings" / "tests" / "UnitTests" / "LoweringOracle" / "Evidence" / "HandWrittenTargets.swift").ToString(),
            artifacts = new[] { sidecar.ToString() },
            evidence = evidence.ToString(),
        }));

        Log.Information("=== bridge-consumer {Platform}: recording the oracle's swiftc lowering evidence (cross-check only) ===", family.Platform);
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
            if (Directory.Exists(compiled)) compiled.DeleteDirectory();
        }
        if (!File.Exists(evidence))
            throw new Exception($"--bridge-consumer: the oracle wrote no evidence at {evidence}.");
        File.Copy(evidence, family.Receipts / evidence.Name, overwrite: true);
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
    void AssertBridgeConsumerCollectedReceipt(BcfFamily family, string label, AbsolutePath collected, AbsolutePath oracle, BcfRoute bridged)
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
        foreach (var triple in family.SliceTriples.Where(t => !actual.Slices.ContainsKey(t)))
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

    // The binding package's wrapper must export its bridges, validated against the binding's sidecar on
    // every slice: iOS ships a device and a simulator slice, Mac Catalyst one universal slice.
    void AssertBridgeConsumerPackedWrapper(BcfFamily family, AbsolutePath fixtureOut, AbsolutePath fixturePlans)
    {
        var nupkg = fixtureOut / $"{BcfPackageId}.{BcfLaneVersion}.nupkg";
        if (!File.Exists(nupkg))
            throw new Exception($"--bridge-consumer: the binding package was not produced at {nupkg}.");
        var extract = family.Root / "packed-wrapper";
        if (Directory.Exists(extract)) extract.DeleteDirectory();
        var images = new List<string>();
        using (var archive = ZipFile.OpenRead(nupkg))
        {
            // A Mac framework may be versioned (Versions/A/<binary>), so match the binary by name
            // anywhere under the wrapper framework.
            foreach (var entry in archive.Entries.Where(e =>
                         e.FullName.Contains($"/{BcfWrapperModule}.framework/", StringComparison.Ordinal) && e.Name == BcfWrapperModule))
            {
                var target = extract / entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                target.Parent.CreateDirectory();
                entry.ExtractToFile(target, overwrite: true);
                images.Add(target);
                Log.Information("  packed wrapper slice: {Entry}", entry.FullName);
            }
        }
        if (family.Catalyst)
        {
            if (images.Count != 1)
                throw new Exception($"--bridge-consumer: {nupkg.Name} carries {images.Count} wrapper image(s); expected the one universal Mac Catalyst slice.");
            AssertBcfArchitectures(images[0], ["arm64", "x86_64"], $"the packed wrapper of {nupkg.Name}");
        }
        else if (images.Count < 2)
            throw new Exception($"--bridge-consumer: {nupkg.Name} carries {images.Count} wrapper slice(s); expected the device and simulator slices.");
        ValidateLinkedBridges(images, fixturePlans, $"the packed wrapper of {nupkg.Name}", family.Platform);
        extract.DeleteDirectory();
    }

    // A universal image must carry every architecture the app runs.
    static void AssertBcfArchitectures(string image, IReadOnlyList<string> expected, string what)
    {
        var lipo = ProcessTasks.StartProcess("/usr/bin/xcrun", $"lipo -archs \"{image}\"", logOutput: false);
        lipo.AssertZeroExitCode();
        var archs = string.Join(" ", lipo.Output.Where(o => o.Type == OutputType.Std).Select(o => o.Text))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var missing = expected.Where(a => !archs.Contains(a)).ToList();
        if (missing.Count > 0)
            throw new Exception($"--bridge-consumer: {what} ({image}) carries {string.Join(", ", archs)}; missing {string.Join(", ", missing)}.");
    }

    // Every image's bridges, on every architecture it carries, must export what the sidecar routed.
    void ValidateLinkedBridges(IReadOnlyList<string> images, AbsolutePath plans, string what, string platform)
    {
        EnsureGeneratorBuilt(ensureSwiftInterfaceParser: false);
        var scratch = BcfScratch / "linked-check" / Guid.NewGuid().ToString("N");
        scratch.CreateDirectory();
        try
        {
            var arguments = new List<string> { $"\"{GeneratorDll}\"", $"--swift-call-plans \"{plans}\"", $"-o \"{scratch}\"", $"--platform {platform}" };
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

    // Builds one app, runs each of its slices, then judges every run. `bindingFor` gives the binding the
    // app's build consumed, in the app's configuration; for a binding built with the app (direct,
    // projectref) it checks that build's receipt against the oracle. The app is told the settle bridge
    // that binding routed on its command line, for its sentinel.
    IReadOnlyList<string> RunBridgeConsumerApp(BcfFamily family, BcfMode mode, AbsolutePath appDir, BcfPlatform platform, BcfRoute element,
        Func<string, BcfBinding> bindingFor)
    {
        var config = RuntimeLane.BuildConfiguration(platform.OnDevice, platform.MonoAot);
        var engine = RuntimeLane.IsNet11 ? RuntimeLane.Engine(platform.OnDevice)
            : platform.OnDevice ? platform.MonoAot ? "mono-aot" : "nativeaot" : platform.Catalyst ? "mono" : "mono-jit";
        var label = $"[{BcfModeName(mode)}] {platform.Name} ({RuntimeLane.Framework} {engine}, {config})";

        var failures = new List<string>();
        AbsolutePath appPath;
        BcfBinding binding;
        try
        {
            appPath = platform.Catalyst
                ? BuildBridgeConsumerCatalystApp(appDir, config, label)
                : BuildPackageConsumer(appDir, BcfAppName, platform.OnDevice, "--bridge-consumer", label, platform.MonoAot);
            binding = bindingFor(config);
        }
        catch (Exception ex)
        {
            failures.Add($"{label}: {ex.Message}");
            return failures;
        }
        var plans = binding.Plans;
        IReadOnlyList<BcfRoute> routes = [binding.Settle, element];

        // The bridges as linked into the app the host ran: every architecture of a universal image.
        var frameworks = platform.Catalyst ? appPath / "Contents" / "Frameworks" : appPath / "Frameworks";
        var runtimeImage = BcfFrameworkBinary(frameworks, BcfRuntimeLibrary);
        var wrapperImage = BcfFrameworkBinary(frameworks, BcfWrapperModule);
        foreach (var image in new[] { runtimeImage, wrapperImage })
            if (!File.Exists(image))
                failures.Add($"{label}: the app bundle has no {image.Name} at {image}.");
        if (failures.Count > 0)
            return failures;
        try
        {
            if (platform.Catalyst)
                foreach (var image in new[] { runtimeImage, wrapperImage, BcfFrameworkBinary(frameworks, BcfModule) })
                    AssertBcfArchitectures(image, ["arm64", "x86_64"], $"{label} {image.Name}");
            ValidateLinkedBridges([runtimeImage], SwiftBridgeProductsDir / "Swift.Runtime.swift-call-plans.json", $"{label} {BcfRuntimeLibrary}", family.Platform);
            ValidateLinkedBridges([wrapperImage], plans, $"{label} {BcfWrapperModule}", family.Platform);
        }
        catch (Exception ex)
        {
            failures.Add($"{label}: {ex.Message}");
            return failures;
        }

        foreach (var run in platform.Runs)
        {
            var runLabel = platform.Catalyst ? $"{label} {run.Rid}{(run.UnderRosetta ? " under Rosetta" : "")}" : label;
            var tag = $"{BcfModeName(mode)}-{run.Name}";
            string[] arguments = ["--bcf-settle-bridge", binding.Settle.Bridge];
            LaunchResult result;
            if (platform.Catalyst)
            {
                Log.Information("=== --bridge-consumer ({Label}): launching ===", runLabel);
                result = LaunchHostApp(appPath / "Contents" / "MacOS" / BcfAppName, arguments, run.UnderRosetta, TimeSpan.FromSeconds(Timeout));
            }
            else
                result = LaunchPackageConsumer(appPath, BcfAppName, BcfBundleId, platform.OnDevice, "--bridge-consumer", runLabel, arguments);
            File.WriteAllText(family.Receipts / $"{tag}-console.txt", result.Output);
            Log.Information("=== CONSUMER OUTPUT ({Label}) ===", runLabel);
            foreach (var line in result.Output.Split('\n').Where(l => l.Contains("BCF CHECK") || l.Contains("BCF SENTINEL") || l.Contains("Runtime flavor")
                         || l.Contains("TEST ")))
                Log.Information("  {Line}", line.TrimEnd());

            if (LaunchDiagnostics.LauncherNeverStartedApp(result))
            {
                failures.Add($"{runLabel}: the launcher never started the app, so nothing was measured.");
                continue;
            }
            if (RuntimeLane.IsNet11)
            {
                try { AssertLiveRuntimeLane(result, platform.OnDevice); }
                catch (InvalidOperationException ex) { failures.Add($"{runLabel}: {ex.Message}"); }
            }
            else if (BcfNet10FlavorProblem(result.Output, platform) is { } flavor)
                failures.Add($"{runLabel}: {flavor}");

            failures.AddRange(Regex.Matches(result.Output, @"BCF CHECK (\S+) FAIL ?(.*)").Select(m => $"{runLabel}: in-app check {m.Groups[1].Value} failed: {m.Groups[2].Value.Trim()}"));
            if (result.Result != TestResult.Success)
                failures.Add($"{runLabel}: the app did not report TEST SUCCESS (result {result.Result}).");
            // The register sentinel is arm64 code; on every other slice the app says it skipped it.
            if (platform.Catalyst && run.Rid != "maccatalyst-arm64" && !result.Output.Contains("BCF SENTINEL skipped", StringComparison.Ordinal))
                failures.Add($"{runLabel}: the app did not report skipping the arm64 register sentinel.");

            var objRoot = appDir / "obj";
            var analysis = AnalyzeBridgeConsumerStacks(result.Output, routes, BcfExpectedCaller(platform, mode, run),
                image => UnstrippedImageSymbols(objRoot, run.Rid, image));
            File.WriteAllText(family.Receipts / $"{tag}-stacks.txt", analysis.Report);
            Log.Information("{Report}", analysis.Report);
            failures.AddRange(analysis.Problems.Select(p => $"{runLabel}: {p}"));
        }
        return failures;
    }

    // A framework's binary: shallow on iOS, or reached through Versions/Current in a Mac bundle.
    static AbsolutePath BcfFrameworkBinary(AbsolutePath frameworks, string name)
    {
        var framework = frameworks / $"{name}.framework";
        var shallow = framework / name;
        var versioned = framework / "Versions" / "A" / name;
        return File.Exists(shallow) || !File.Exists(versioned) ? shallow : versioned;
    }

    // A universal Mac Catalyst app: one `dotnet build` with no RID, which the workload turns into one
    // inner build per RID and a merged bundle. The shipped bundle is the one directly under the
    // configuration's output, not a per-RID inner copy.
    AbsolutePath BuildBridgeConsumerCatalystApp(AbsolutePath appDir, string config, string label)
    {
        Log.Information("=== --bridge-consumer ({Label}): building the universal consumer ===", label);
        DotNetBuild(s => s
            .SetProjectFile(appDir / $"{BcfAppName}.csproj")
            .SetConfiguration(config)
            .EnableNoLogo()
            .SetVerbosity(DotNetVerbosity.quiet));
        var bundles = Directory.GetDirectories(appDir / "bin", $"{BcfAppName}.app", SearchOption.AllDirectories)
            .Where(d => d.Split(Path.DirectorySeparatorChar).Contains(config))
            .OrderBy(d => d.Length).ThenBy(d => d, StringComparer.Ordinal)
            .ToList();
        var appPath = bundles.FirstOrDefault()
            ?? throw new Exception($"--bridge-consumer ({label}): {BcfAppName}.app not found under {appDir / "bin"} after the build.");
        if (bundles.Any(b => b != appPath && Path.GetDirectoryName(b)!.Length == Path.GetDirectoryName(appPath)!.Length))
            throw new Exception($"--bridge-consumer ({label}): more than one candidate universal bundle: {string.Join(", ", bundles)}.");
        Log.Information("    app bundle: {Path}", appPath);
        return (AbsolutePath)appPath;
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
        BcfCaller.MonoInterpreter => caller.Image == BcfAppName && caller.Symbol == "do_icall"
            ? null : $"expected the Mono interpreter's native call (do_icall) in {BcfAppName}",
        // Code the JIT generated as the app ran lies in no loaded image.
        BcfCaller.MonoJit => caller.Image == "?" ? null : "expected code Mono's JIT generated at run time, in no image",
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
        var catalyst = platform.Catalyst;
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
                <TargetFramework>{RuntimeLane.Framework}-{(catalyst ? "maccatalyst" : "ios")}</TargetFramework>
                {(catalyst
                    ? $"<RuntimeIdentifiers>{string.Join(';', platform.Runs.Select(r => r.Rid))}</RuntimeIdentifiers>"
                    : $"<RuntimeIdentifier>{platform.Runs[0].Rid}</RuntimeIdentifier>")}
                {MixedConsumerRuntimeProperties(RuntimeLane, device, platform.MonoAot)}
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <ApplicationId>{BcfBundleId}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>{(catalyst ? "17.0" : "15.0")}</SupportedOSPlatformVersion>
                {(direct ? "<IsPackable>false</IsPackable>\n<TreatWarningsAsErrors>false</TreatWarningsAsErrors>" : "")}
                <NoWarn>$(NoWarn);CA1416;CA1422{(device ? ";IL2121" : "")}{(direct ? ";CS0649;CS0114;CS8604" : "")}</NoWarn>
              </PropertyGroup>
              <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'iossimulator-arm64' OR $(TargetFramework.EndsWith('-maccatalyst'))">
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
                {{(catalyst
                    ? "static void Main(string[] args) => Probe.Run();"
                    : "static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));")}}
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
                    // The sentinel is arm64 code: the fixture links it into its arm64 slice only.
                    if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                    {
                        Console.WriteLine($"BCF SENTINEL skipped on {RuntimeInformation.ProcessArchitecture}");
                        return;
                    }
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
        if (catalyst)
        {
            // The Mac Catalyst 27 SDK fails the build (MT7199) without a scene manifest. The probe
            // runs from Main and never calls UIApplication.Main, so UIKit creates no scene and the
            // manifest names no scene configuration or delegate.
            File.WriteAllText(appDir / "Info.plist",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                "<plist version=\"1.0\">\n<dict>\n" +
                "    <key>UIApplicationSceneManifest</key>\n" +
                "    <dict>\n" +
                "        <key>UIApplicationSupportsMultipleScenes</key>\n" +
                "        <false/>\n" +
                "    </dict>\n" +
                "</dict>\n</plist>\n");
            return;
        }
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
