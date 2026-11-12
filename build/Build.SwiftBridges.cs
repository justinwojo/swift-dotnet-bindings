// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// The Swift bridges' host-only targets. `swift-bridge-products` writes the plan sidecars of the
// hand-written transition tables (the runtime's and the Apple supplement's) under
// build/baselines/swift-call-plans/; a unit test fails when they are stale. `swift-bridge-gate`
// reads those and the corpus sidecars of the regenerated BindingTests output, joins the swiftc
// lowering evidence `swift-lowering-oracle` records, and emits and validates every bridge on arm64
// and x86_64. The same evidence file is what an iOS BindingTests regeneration passes the generator,
// which routes every transition it proves through that transition's bridge.

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
    /// Whether a BindingTests regeneration for <paramref name="platformOverride"/> (null: iOS) routes
    /// through native bridges. The lowering evidence is recorded for the iOS device and simulator
    /// triples only; a platform it does not name keeps its direct Swift imports.
    /// </summary>
    static bool BridgeEvidenceApplies(ApplePlatform? platformOverride) =>
        platformOverride == null || platformOverride.Name == "ios";

    AbsolutePath SwiftBridgeGateScratch => RootDirectory / "artifacts" / "swift-bridges";

    /// <summary>The hand-written tables and the source directories of the assembly each belongs to.</summary>
    (AbsolutePath Table, AbsolutePath[] Sources)[] HandWrittenTransitionTables =>
    [
        (SourceDir / "Swift.Runtime" / "src" / "transition-facts.json", [SourceDir / "Swift.Runtime" / "src"]),
        (SourceDir / "Swift.Bindings.Apple" / "transition-facts.json", [SourceDir / "Swift.Bindings.Apple" / "Sources"]),
    ];

    /// <summary>The plan sidecars of the iOS BindingTests regeneration, which routes through the bridges.</summary>
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
                    arguments.Add($"--swift-lowering-evidence \"{SwiftLoweringEvidencePath}\"");
                RunGenerator(arguments, $"hand-written plans of {table}");
            }
        });

    Target SwiftBridgeGate => _ => _
        .DependsOn(Compile)
        .OnlyWhenStatic(() => OperatingSystem.IsMacOS())
        .After(SwiftLoweringOracle, SwiftBridgeProducts)
        .Description("Emit and validate the native bridge of every plan (corpus and hand-written tables) on arm64 and x86_64.")
        .Executes(() =>
        {
            EnsureGeneratorBuilt(ensureSwiftInterfaceParser: false);
            var plans = SwiftBridgePlanFiles();
            var routed = SwiftBridgeCorpusPlanFiles().Concat(SwiftBridgeRoutedProductFiles()).ToArray();
            foreach (var missing in plans.Where(p => !File.Exists(p)))
                Assert.Fail($"{missing} is missing: run `nuke binding-tests --compile-only` first");
            if (Directory.Exists(SwiftBridgeGateScratch))
                SwiftBridgeGateScratch.DeleteDirectory();
            var arguments = new List<string>
            {
                $"\"{GeneratorDll}\"", "--swift-bridges",
                $"--swift-lowering-evidence \"{SwiftLoweringEvidencePath}\"", $"-o \"{SwiftBridgeGateScratch}\"",
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
