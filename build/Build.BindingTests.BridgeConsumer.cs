// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.BridgeConsumer.cs — opt-in .NET 11 packaged Debug consumer of the Swift bridges.
//
// The CoreCLR interpreter, which runs every ordinary Debug .NET 11 app on iOS, mishandles the Swift
// calling convention. The generator and SwiftBindings.Runtime therefore route each Swift-convention
// call the swiftc lowering evidence proves through a native bridge (SBSR_), reached through a C
// twin. This leg shows that the way a consumer gets it: a third-party-shaped module packed through
// SwiftBindings.Sdk with the evidence on, a Runtime package from the same tree, and an ordinary
// `dotnet build -c Debug` app that restores both from a local feed and runs on the iOS simulator or a
// phone.
//
// The app calls two transitions and checks what each did:
// - BridgeAccount.accumulate (self, a Swift error and a scalar result) through the binding's own
//   bridge: success, error, success, with the receiver's state checked after each.
// - Array's subscript getter through the Runtime's bridge, by indexing the array the module returns.
// Every measured call records its stack in Swift. The host requires the routed bridge under the Swift
// code and the interpreter's call stub under the bridge, on every call. The app also calls both bridges
// from a register sentinel, checks the module's live-object count before and after, and the host
// validates the bridges of the packed and the linked images.
//
// Opt-in, .NET 11 and --coreclr-interp only. Never part of the default run or --compile-only.

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
    [Parameter("Opt-in, .NET 11 with --coreclr-interp: pack a binding generated with the Swift bridges on, consume it from an ordinary Debug app through NuGet, and prove on the simulator and/or a device that its calls run through the bridges from the interpreter. Never part of the default run or --compile-only.")]
    readonly bool BridgeConsumer;

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

    /// <summary>One routed bridge the host expects on a measured call.</summary>
    sealed record BcfRoute(string Tag, string Member, string Target, string Bridge, string Image);

    void RunBridgeConsumerLeg(bool sim, bool device)
    {
        Log.Information("=================================================");
        Log.Information(" BindingTests — packaged Debug consumer of the Swift bridges (--bridge-consumer)");
        Log.Information("   sim: {Sim}   device: {Device}", sim, device);
        Log.Information("=================================================");

        if (!RuntimeLane.IsNet11 || !RuntimeLane.CoreClrInterpreter)
            throw new Exception("--bridge-consumer measures the .NET 11 CoreCLR interpreter. Run it under the net11 toolchain profile with " +
                "--coreclr-interp: bash build.sh --toolchain-profile net11-xcode27 -- binding-tests --bridge-consumer --coreclr-interp [--sim] [--device]");
        // Also stages SwiftInterfaceParser, which the SDK package ships and the fixture pack runs.
        EnsureGeneratorBuilt();
        var stagedParser = SwiftInterfaceParserStagingDir / "SwiftInterfaceParser";
        if (!File.Exists(stagedParser))
            throw new InvalidOperationException($"--bridge-consumer: SwiftInterfaceParser is missing at '{stagedParser}'.");

        if (Directory.Exists(BcfScratch)) BcfScratch.DeleteDirectory();
        BcfReceipts.CreateDirectory();
        var nupkgDir = BcfScratch / "packages";
        var fixtureDir = BcfScratch / "fixture";
        var fixtureOut = BcfScratch / "fixture-output";
        nupkgDir.CreateDirectory();
        fixtureDir.CreateDirectory();
        fixtureOut.CreateDirectory();

        var xcframework = BuildBridgeConsumerXcframework(BcfScratch / "build");

        // The evidence is per symbol, so the fixture's own targets need it: generate once to learn
        // them (the call-plan sidecar and the wrapper source), then let the oracle record what swiftc
        // gives each one on every required triple.
        var evidence = RecordBridgeConsumerEvidence(xcframework);

        try
        {
            using var scope = new VersionScope(BcfLaneVersion, RootDirectory, BcfLaneAppleVersion);
            BuildLocalPackageFeed(nupkgDir, scope, "bridge-consumer", BcfLaneVersion, BcfLaneAppleVersion, BcfPackageId);
            AssertRuntimeNativePackage(nupkgDir / $"SwiftBindings.Runtime.{BcfLaneVersion}.nupkg");

            WriteBridgeConsumerFixture(fixtureDir, nupkgDir, xcframework, evidence);
            Log.Information("=== bridge-consumer: packing the fixture binding with the lowering evidence ===");
            DotNetPack(s => s
                .SetProject(fixtureDir / $"{BcfModule}.csproj")
                .SetConfiguration("Release")
                .SetOutputDirectory(fixtureOut)
                .EnableNoLogo()
                .SetVerbosity(DotNetVerbosity.quiet));

            var fixturePlans = Directory.GetFiles(fixtureDir, $"{BcfModule}.swift-call-plans.json", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToList();
            if (fixturePlans.Count != 1)
                throw new Exception($"--bridge-consumer: expected one {BcfModule}.swift-call-plans.json under {fixtureDir}, found {fixturePlans.Count}.");
            var routes = BridgeConsumerRoutes((AbsolutePath)fixturePlans[0]);
            AssertBridgeConsumerPackedWrapper(fixtureOut, (AbsolutePath)fixturePlans[0]);

            var failures = new List<string>();
            if (sim)
                failures.AddRange(RunBridgeConsumer(nupkgDir, fixtureOut, routes, (AbsolutePath)fixturePlans[0], onDevice: false));
            if (device)
                failures.AddRange(RunBridgeConsumer(nupkgDir, fixtureOut, routes, (AbsolutePath)fixturePlans[0], onDevice: true));
            if (failures.Count > 0)
            {
                foreach (var f in failures) Log.Error("  ✗ {Failure}", f);
                throw new Exception($"--bridge-consumer: {failures.Count} problem(s); receipts under {BcfReceipts}.");
            }
            Log.Information("--bridge-consumer PASSED. Receipts: {Dir}", BcfReceipts);
        }
        finally
        {
            EvictThrowawayPackages(BcfLaneVersion, BcfLaneAppleVersion, BcfPackageId);
            foreach (var dir in new[] { nupkgDir, fixtureOut })
                if (Directory.Exists(dir)) dir.DeleteDirectory();
        }
    }

    // MARK: - Fixture module and its lowering evidence

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

    AbsolutePath RecordBridgeConsumerEvidence(AbsolutePath xcframework)
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

        var evidence = BcfScratch / "swift-lowering-evidence.json";
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

        Log.Information("=== bridge-consumer: recording the fixture's swiftc lowering evidence ===");
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
        foreach (var report in Directory.GetFiles(reports, "*.txt").OrderBy(f => f, StringComparer.Ordinal))
            foreach (var line in File.ReadAllLines(report).Where(l => l.StartsWith("evidence:", StringComparison.Ordinal)))
                Log.Information("  {Triple}: {Line}", Path.GetFileNameWithoutExtension(report), line);
        return evidence;
    }

    void WriteBridgeConsumerFixture(AbsolutePath fixtureDir, AbsolutePath nupkgDir, AbsolutePath xcframework, AbsolutePath evidence)
    {
        var csproj = $"""
            <Project Sdk="SwiftBindings.Sdk/{BcfLaneVersion}">
              <PropertyGroup>
                <TargetFramework>{RuntimeLane.Framework}-ios</TargetFramework>
                <PackageId>{BcfPackageId}</PackageId>
                <PackageVersion>{BcfLaneVersion}</PackageVersion>
                <IsPackable>true</IsPackable>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <NoWarn>$(NoWarn);CS0649;CS0114;CA1416;CS8604</NoWarn>
                <SwiftLoweringEvidence>{evidence}</SwiftLoweringEvidence>
              </PropertyGroup>
              <ItemGroup>
                <SwiftFramework Include="{xcframework}" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(fixtureDir / $"{BcfModule}.csproj", csproj);
        File.WriteAllText(fixtureDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureNupkgDir: null));
    }

    // MARK: - Routes and packed-image checks

    // The two measured transitions and the bridge each must take, read from the sidecars that routed them.
    List<BcfRoute> BridgeConsumerRoutes(AbsolutePath fixturePlans)
    {
        static IEnumerable<JsonElement> Bridged(AbsolutePath path) =>
            JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("coverage").EnumerateArray()
                .Where(e => e.TryGetProperty("bridged", out var b) && b.ValueKind == JsonValueKind.True)
                .Select(e => e.Clone()).ToList();

        BcfRoute Single(IEnumerable<JsonElement> entries, Func<string, bool> member, string tag, string image, string what)
        {
            var matches = entries.Where(e => member(e.GetProperty("member").GetString()!)).ToList();
            if (matches.Count != 1)
                throw new Exception($"--bridge-consumer: expected exactly one bridged {what}, found {matches.Count}. " +
                    "The generator did not route it through a bridge; check the evidence and the sidecar.");
            var e = matches[0];
            return new BcfRoute(tag, e.GetProperty("member").GetString()!, e.GetProperty("entryPoint").GetString()!,
                e.GetProperty("symbol").GetString()!, image);
        }

        var accumulate = Single(Bridged(fixturePlans), m => m.Contains("::PInvoke_accumulate", StringComparison.Ordinal),
            "accumulate", BcfWrapperModule, "BridgeAccount.accumulate");
        var element = Single(Bridged(SwiftBridgeProductsDir / "Swift.Runtime.swift-call-plans.json"),
            m => m == "Swift.SwiftArrayPInvokes::Get", "element", BcfRuntimeLibrary, "SwiftArrayPInvokes.Get");
        foreach (var r in new[] { accumulate, element })
            Log.Information("  route {Tag}: {Member} -> {Image}!{Bridge} -> {Target}", r.Tag, r.Member, r.Image, r.Bridge, r.Target);
        return [accumulate, element];
    }

    // The binding package's wrapper must export its bridges, validated against the fixture's sidecar on every slice.
    void AssertBridgeConsumerPackedWrapper(AbsolutePath fixtureOut, AbsolutePath fixturePlans)
    {
        var nupkg = fixtureOut / $"{BcfPackageId}.{BcfLaneVersion}.nupkg";
        if (!File.Exists(nupkg))
            throw new Exception($"--bridge-consumer: the fixture package was not produced at {nupkg}.");
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

    IReadOnlyList<string> RunBridgeConsumer(AbsolutePath nupkgDir, AbsolutePath fixtureOut, IReadOnlyList<BcfRoute> routes,
        AbsolutePath fixturePlans, bool onDevice)
    {
        var platform = onDevice ? "device" : "simulator";
        var config = RuntimeLane.BuildConfiguration(onDevice, false);
        var label = $"{platform} ({RuntimeLane.Engine(onDevice)}, {config})";
        var rid = onDevice ? "ios-arm64" : "iossimulator-arm64";
        var appDir = BcfScratch / $"consumer-{platform}";
        if (Directory.Exists(appDir)) appDir.DeleteDirectory();
        appDir.CreateDirectory();

        WriteBridgeConsumerApp(appDir, rid, routes);
        File.WriteAllText(appDir / "NuGet.config", MixedPackNuGetConfig(nupkgDir, fixtureOut, "BridgeConsumerFixture.*"));

        var (appPath, result) = BuildAndLaunchPackageConsumer(appDir, BcfAppName, BcfBundleId, onDevice, "--bridge-consumer", label);
        File.WriteAllText(BcfReceipts / $"{platform}-console.txt", result.Output);
        Log.Information("=== CONSUMER OUTPUT ({Label}) ===", label);
        foreach (var line in result.Output.Split('\n').Where(l => l.Contains("BCF CHECK") || l.Contains("BCF SENTINEL") || l.Contains("Runtime flavor")
                     || l.Contains("TEST ")))
            Log.Information("  {Line}", line.TrimEnd());

        var failures = new List<string>();
        if (LaunchDiagnostics.LauncherNeverStartedApp(result))
        {
            failures.Add($"{label}: the launcher never started the app, so nothing was measured.");
            return failures;
        }
        AssertLiveRuntimeLane(result, onDevice);

        failures.AddRange(Regex.Matches(result.Output, @"BCF CHECK (\S+) FAIL ?(.*)").Select(m => $"{label}: in-app check {m.Groups[1].Value} failed: {m.Groups[2].Value.Trim()}"));
        if (result.Result != TestResult.Success)
            failures.Add($"{label}: the app did not report TEST SUCCESS (result {result.Result}).");

        var objRoot = appDir / "obj";
        var analysis = AnalyzeBridgeConsumerStacks(result.Output, routes,
            image => UnstrippedImageSymbols(objRoot, rid, image));
        File.WriteAllText(BcfReceipts / $"{platform}-stacks.txt", analysis.Report);
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
            ValidateLinkedBridges([runtimeImage], SwiftBridgeProductsDir / "Swift.Runtime.swift-call-plans.json", $"the {platform} app's {BcfRuntimeLibrary}");
            ValidateLinkedBridges([wrapperImage], fixturePlans, $"the {platform} app's {BcfWrapperModule}");
        }
        return failures;
    }

    sealed record BcfStackAnalysis(string Report, IReadOnlyList<string> Problems);

    // Every measured call's stack: the routed bridge must sit directly under the Swift code and the
    // interpreter's call stub directly under the bridge.
    static BcfStackAnalysis AnalyzeBridgeConsumerStacks(string output, IReadOnlyList<BcfRoute> routes,
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
        foreach (var route in routes)
        {
            var calls = frames.Where(f => f.Tag == route.Tag).GroupBy(f => f.Serial).OrderBy(g => g.Key).ToList();
            // Three accumulate calls (success, error, success) and three element reads. A read may
            // reach object(at:) more than once, so only accumulate's count is exact.
            const int expected = 3;
            report.AppendLine($"{route.Tag}: {calls.Count} measured call(s) through {route.Image}!{route.Bridge} -> {route.Target}");
            if (route.Tag == "accumulate" ? calls.Count != expected : calls.Count < expected)
                problems.Add($"{route.Tag}: {calls.Count} measured call(s) recorded a stack; expected {(route.Tag == "accumulate" ? "exactly" : "at least")} {expected}.");
            foreach (var call in calls)
            {
                var stack = call.OrderBy(f => f.Index).Select(f => f.Witness).ToList();
                var at = stack.FindIndex(w => w.Symbol != null && w.Symbol.StartsWith("SBSR_", StringComparison.Ordinal));
                var caller = at >= 0 && at + 1 < stack.Count ? stack[at + 1] : null;
                var callee = at > 0 ? stack[at - 1] : null;
                var lane = caller == null ? null : RuntimeLaneIdentity.Classify(caller, BcfAppName);
                report.AppendLine($"  call {call.Key}: callee {callee?.Describe() ?? "-"} | bridge {(at >= 0 ? stack[at].Describe() : "none")} | caller {caller?.Describe() ?? "-"} => {lane?.ToString() ?? "unclassified"}");
                if (at < 0)
                {
                    problems.Add($"{route.Tag} call {call.Key}: no bridge frame on the stack ({string.Join(" < ", stack.Take(8).Select(w => w.Describe()))}).");
                    continue;
                }
                if (stack[at].Symbol != route.Bridge || stack[at].Image != route.Image)
                    problems.Add($"{route.Tag} call {call.Key}: the bridge frame is {stack[at].Describe()}, expected {route.Image}!{route.Bridge}.");
                if (lane != RuntimeExecutionLane.Interpreter)
                    problems.Add($"{route.Tag} call {call.Key}: the bridge's caller {caller?.Describe() ?? "-"} is {lane?.ToString() ?? "not a known lane"}, expected the interpreter's call stub.");
                // The binding's bridge calls the member's own entry point; the getter reaches object(at:) through stdlib internals.
                if (route.Tag == "accumulate" && callee?.Symbol != route.Target)
                    problems.Add($"{route.Tag} call {call.Key}: the bridge called {callee?.Describe() ?? "-"}, expected {route.Target}.");
            }
        }
        return new BcfStackAnalysis(report.ToString(), problems);
    }

    // MARK: - Consumer app

    void WriteBridgeConsumerApp(AbsolutePath appDir, string rid, IReadOnlyList<BcfRoute> routes)
    {
        var device = rid == "ios-arm64";
        var accumulate = routes.Single(r => r.Tag == "accumulate");
        var element = routes.Single(r => r.Tag == "element");
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{RuntimeLane.Framework}-ios</TargetFramework>
                <RuntimeIdentifier>{rid}</RuntimeIdentifier>
                {MixedConsumerRuntimeProperties(RuntimeLane, device)}
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <ApplicationId>{BcfBundleId}</ApplicationId>
                <ApplicationDisplayVersion>1.0</ApplicationDisplayVersion>
                <ApplicationVersion>1</ApplicationVersion>
                <SupportedOSPlatformVersion>15.0</SupportedOSPlatformVersion>
                <NoWarn>$(NoWarn);CA1416;CA1422{(device ? ";IL2121" : "")}</NoWarn>
              </PropertyGroup>
              <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'iossimulator-arm64'">
                <MtouchLink>None</MtouchLink>
              </PropertyGroup>
              <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'ios-arm64'">
                <CodesignKey>Apple Development: Justin Wojciechowski (2YJ2TQ6Q7Q)</CodesignKey>
                <CodesignProvision>Swift Bindings Dev</CodesignProvision>
                <TeamIdentifierPrefix>HM68F9PH26</TeamIdentifierPrefix>
              </PropertyGroup>
              <ItemGroup>
                <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
              </ItemGroup>
              <ItemGroup>
                <None Include="Info.plist" />
              </ItemGroup>
              <ItemGroup>
                <PackageReference Include="{BcfPackageId}" Version="{BcfLaneVersion}" />
              </ItemGroup>
            </Project>
            """;
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
                const string AccumulateBridge = "{{accumulate.Bridge}}";
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
                        // The sentinel calls record stacks under the sentinel, not the interpreter; drop them.
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

                // self + error + scalar result, and the array getter, each through its bridge from interpreted code.
                [MethodImpl(MethodImplOptions.NoInlining)]
                static void Measure()
                {
                    using (var account = new BridgeAccount(7))
                    {
                        var handle = ((ISwiftObject)account).SwiftHandle;
                        var first = account.Accumulate(5, x => x);
                        Check("accumulate-success", first == 12 && account.Total == 12, $"returned {first}, total {account.Total}");

                        string? thrown = null;
                        try
                        {
                            var unexpected = account.Accumulate(4, x => -x);
                            thrown = $"no exception (returned {unexpected})";
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"BCF ERROR {ex.GetType().FullName}: {ex.Message}");
                            (ex as IDisposable)?.Dispose();
                        }
                        Check("accumulate-error", thrown == null && account.Total == 12, $"{thrown ?? "threw"}, total {account.Total}");

                        var second = account.Accumulate(3, x => x);
                        Check("accumulate-after-error", second == 15 && account.Total == 15, $"returned {second}, total {account.Total}");
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

                // Each bridge called from native code that loads sentinels into x19-x28 and d8-d15 and
                // reports any the bridge did not preserve.
                [MethodImpl(MethodImplOptions.NoInlining)]
                static void Sentinels()
                {
                    var sentinel = (delegate* unmanaged<nint, ulong*, ulong*, ulong>)Export("{{BcfModule}}", "bcf_bridge_sentinel");
                    var identity = (ulong)Export("{{BcfModule}}", "bcf_identity_map");
                    var negate = (ulong)Export("{{BcfModule}}", "bcf_negate_map");
                    var accumulateBridge = Export("{{BcfWrapperModule}}", AccumulateBridge);
                    var elementBridge = Export("{{BcfRuntimeLibrary}}", ElementBridge);
                    ulong* args = stackalloc ulong[8];
                    ulong result = 0;

                    using (var account = new BridgeAccount(100))
                    {
                        var self = (ulong)((ISwiftObject)account).SwiftHandle;
                        nint error = 0;
                        args[0] = 5; args[1] = identity; args[2] = self; args[3] = (ulong)&error;
                        var mask = sentinel(accumulateBridge, args, &result);
                        Check("sentinel-accumulate", mask == 0 && result == 105 && error == 0, $"mask 0x{mask:x}, result {result}, error 0x{error:x}");
                        Console.WriteLine($"BCF SENTINEL accumulate mask=0x{mask:x}");

                        error = 0;
                        args[0] = 2; args[1] = negate; args[2] = self; args[3] = (ulong)&error;
                        mask = sentinel(accumulateBridge, args, &result);
                        Check("sentinel-accumulate-error", mask == 0 && error != 0 && account.Total == 105, $"mask 0x{mask:x}, error 0x{error:x}, total {account.Total}");
                        Console.WriteLine($"BCF SENTINEL accumulate-error mask=0x{mask:x}");
                        if (error != 0) swift_errorRelease(error);
                    }

                    // The projection keeps its Swift array private, so the sentinel reads an array of the
                    // same element type the app builds itself: the getter, and so its bridge, is the same.
                    using (var first = new BridgeItem(7))
                    using (var second = new BridgeItem(8))
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
