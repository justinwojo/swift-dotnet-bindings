// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// The Swift bridges' host-only targets. `swift-bridge-products` writes the plan sidecars of the
// hand-written transition tables (the runtime's and the Apple supplement's) under
// build/baselines/swift-call-plans/; a unit test fails when they are stale. A table that routes
// through bridges has its plans judged by the SDK's lowering qualification record over the slices
// the table claims. `swift-bridge-gate` reads those and the corpus sidecars of the regenerated
// BindingTests output, re-judges each by the record over the artifact scope it records, and emits
// and validates every bridge on arm64 and x86_64, assembled for each platform a sidecar claims. The
// same record (which `swift-lowering-oracle` writes, one receipt per artifact and qualified compiler
// family) is what an iOS or Mac Catalyst BindingTests regeneration passes the generator, which routes
// every transition it qualifies through that transition's bridge.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

partial class Build
{
    AbsolutePath SwiftBridgeProductsDir => RootDirectory / "build" / "baselines" / "swift-call-plans";

    AbsolutePath SwiftLoweringEvidencePath => RootDirectory / "build" / "baselines" / "swift-lowering-evidence.json";

    /// <summary>
    /// The platforms whose BindingTests regeneration routes through native bridges: those whose slices
    /// the qualification record holds the corpus's receipts for (the iOS device and simulator triples,
    /// and both Mac Catalyst triples). A platform it does not cover keeps its direct Swift imports.
    /// </summary>
    static readonly string[] SwiftBridgeRoutedPlatforms = ["ios", "maccatalyst"];

    /// <summary>Whether a BindingTests regeneration for <paramref name="platformOverride"/> (null: iOS) routes through native bridges.</summary>
    static bool BridgeEvidenceApplies(ApplePlatform? platformOverride) =>
        SwiftBridgeRoutedPlatforms.Contains((platformOverride ?? ApplePlatform.IOS).Name, StringComparer.Ordinal);

    /// <summary>
    /// The members of <c>SwiftBridgeProbe</c> that the qualification regeneration asks to route through
    /// their native bridges (<c>--swift-bridge-route</c>). Each takes a C callback, so by default it
    /// would take the closure <c>@_cdecl</c> wrapper; the request keeps it on its Swift-convention import
    /// so the corpus exercises a bridge with self, a Swift error, an indirect result and an owned class
    /// argument. The generator fails the regeneration when one of them is not routed. Platforms that do
    /// not route regenerate without evidence and leave them on the wrapper.
    /// </summary>
    static readonly string[] SwiftBridgeProbeRoutes =
    [
        "$s20SwiftBindingsTestLib0A11BridgeProbeC10accumulate_7throughs5Int64VAG_A2GXCtKF",
        "$s20SwiftBindingsTestLib0A11BridgeProbeC6derive_7throughAA13TrackedObjectCAG_s5Int64VAIXCtF",
        "$s20SwiftBindingsTestLib0A11BridgeProbeC7combine__7throughAA17LargeScalarStructVs5Int64V_A3IXCtF",
    ];

    AbsolutePath SwiftBridgeGateScratch => RootDirectory / "artifacts" / "swift-bridges";

    /// <summary>
    /// The platform the BindingTests output was last regenerated for, which must be one that routes
    /// through bridges: a check of the corpus sidecars is otherwise a check of a generation that
    /// never judged them.
    /// </summary>
    string AssertRoutedCorpusPlatform(string what)
    {
        var sidecar = BtOutputDir / TargetPlatformSidecarName;
        var platform = File.Exists(sidecar) ? File.ReadAllText(sidecar).Trim() : null;
        if (platform is null || !SwiftBridgeRoutedPlatforms.Contains(platform, StringComparer.Ordinal))
            Assert.Fail($"{what}: the BindingTests output was regenerated for {platform ?? "no recorded platform"}, which does not route " +
                        $"through bridges; regenerate it for {string.Join(" or ", SwiftBridgeRoutedPlatforms)} first");
        return platform!;
    }

    /// <summary>
    /// The sidecars a module's generation writes beside its C# that describe its Swift transitions:
    /// its call facts, transition ledger, call plans and residual guards. A dependency's travel with
    /// its C# into the BindingTests output, where the transition gates read them.
    /// </summary>
    static string[] SwiftTransitionSidecarNames(string module) =>
    [
        $"{module}.swift-call-facts.json", $"{module}.swift-transition-ledger.json",
        $"{module}.swift-call-plans.json", $"{module}.swift-transition-guards.json",
    ];

    /// <summary>The hand-written tables and the source directories of the assembly each belongs to.</summary>
    (AbsolutePath Table, AbsolutePath[] Sources)[] HandWrittenTransitionTables =>
    [
        (SourceDir / "Swift.Runtime" / "src" / "transition-facts.json", [SourceDir / "Swift.Runtime" / "src"]),
        (SourceDir / "Swift.Bindings.Apple" / "transition-facts.json", [SourceDir / "Swift.Bindings.Apple" / "Sources"]),
    ];

    /// <summary>The plan sidecars of the current BindingTests regeneration (iOS or Mac Catalyst), which routes through the bridges.</summary>
    string[] SwiftBridgeCorpusPlanFiles() =>
        new[] { BtOutputDir / $"{ModuleName}.swift-call-plans.json", BtOutputDir / $"{DepModuleName}.swift-call-plans.json" }
            .Select(p => p.ToString())
            .ToArray();

    /// <summary>Every plan sidecar the bridges are built from: the corpus's, then the products'.</summary>
    string[] SwiftBridgePlanFiles() =>
        SwiftBridgeCorpusPlanFiles()
            .Concat(Directory.Exists(SwiftBridgeProductsDir)
                ? Directory.GetFiles(SwiftBridgeProductsDir, "*.swift-call-plans.json").Order(StringComparer.Ordinal)
                : [])
            .ToArray();

    /// <summary>
    /// Whether the table routes its executable transitions through bridges (its <c>bridges</c>
    /// section): its products are then a routed generation, written from the lowering evidence.
    /// </summary>
    static bool HandWrittenTableRoutes(AbsolutePath table)
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(table));
        return document.RootElement.TryGetProperty("bridges", out var bridges) && bridges.ValueKind == System.Text.Json.JsonValueKind.Object;
    }

    /// <summary>The plan products of the tables that route through bridges, which the gate checks as routed generations.</summary>
    string[] SwiftBridgeRoutedProductFiles() =>
        HandWrittenTransitionTables.Where(t => HandWrittenTableRoutes(t.Table))
            .Select(t => System.Text.Json.JsonDocument.Parse(File.ReadAllText(t.Table)).RootElement.GetProperty("assembly").GetString())
            .Select(assembly => (SwiftBridgeProductsDir / $"{assembly}.swift-call-plans.json").ToString())
            .ToArray();

    Target SwiftBridgeProducts => _ => _
        .DependsOn(Compile)
        .Description("Write the plan sidecars of the hand-written transition tables under build/baselines/swift-call-plans/, " +
                     "and the managed shims and native bridge assemblies of a table that routes through bridges (the runtime's). " +
                     "A runtime whose bridge assemblies change needs src/Swift.Runtime/swift/build-runtime.sh rerun.")
        .Executes(() =>
        {
            EnsureGeneratorBuilt(ensureSwiftInterfaceParser: false);
            foreach (var (table, sources) in HandWrittenTransitionTables)
            {
                var arguments = new List<string>
                {
                    $"\"{GeneratorDll}\"", "--emit-hand-written-plans",
                    $"--transition-facts \"{table}\"", $"-o \"{SwiftBridgeProductsDir}\"",
                };
                arguments.AddRange(sources.Select(s => $"--plan-sources \"{s}\""));
                if (HandWrittenTableRoutes(table))
                    arguments.Add($"--swift-lowering-qualification \"{SwiftLoweringEvidencePath}\"");
                RunGenerator(arguments, $"hand-written plans of {table}");
            }
        });

    Target SwiftBridgeGate => _ => _
        .DependsOn(Compile)
        .OnlyWhenStatic(() => OperatingSystem.IsMacOS())
        .After(SwiftLoweringOracle, SwiftBridgeProducts)
        .Description("Emit and validate the native bridge of every plan (corpus and hand-written tables) on arm64 and x86_64, " +
                     "assembled for every platform the sidecars claim.")
        .Executes(() =>
        {
            EnsureGeneratorBuilt(ensureSwiftInterfaceParser: false);
            var plans = SwiftBridgePlanFiles();
            var routed = SwiftBridgeCorpusPlanFiles().Concat(SwiftBridgeRoutedProductFiles()).ToArray();
            foreach (var missing in plans.Where(p => !File.Exists(p)))
                Assert.Fail($"{missing} is missing: run `nuke binding-tests --compile-only` (or a `--catalyst` leg) first");
            AssertRoutedCorpusPlatform("the Swift bridge gate");
            if (Directory.Exists(SwiftBridgeGateScratch))
                SwiftBridgeGateScratch.DeleteDirectory();
            var arguments = new List<string>
            {
                $"\"{GeneratorDll}\"", "--swift-bridges",
                $"--swift-lowering-qualification \"{SwiftLoweringEvidencePath}\"", $"-o \"{SwiftBridgeGateScratch}\"",
            };
            // The corpus was regenerated with this evidence, and a routing table's products were written
            // from it, so their routing must agree with it.
            arguments.AddRange(plans.Select(p => routed.Contains(p) ? $"--swift-routed-call-plans \"{p}\"" : $"--swift-call-plans \"{p}\""));
            RunGenerator(arguments, "the Swift bridge gate");
            Log.Information("Swift bridge gate report: {Report}", SwiftBridgeGateScratch / "bridge-gate.txt");
        });

    void RunGenerator(IEnumerable<string> arguments, string what)
    {
        var process = ProcessTasks.StartProcess("dotnet", string.Join(" ", arguments), workingDirectory: RootDirectory, logOutput: true);
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Fail($"{what} failed (exit code {process.ExitCode}).");
    }
}
