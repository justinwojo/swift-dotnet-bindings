// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Opt-in, host-only: the swiftc lowering oracle over the BindingTests corpus. Compiles the dependency
// and main modules with swiftc for the slices of the platform the corpus was regenerated for (the
// iOS device and both simulator triples; with --platform tvos the tvOS device and both simulator
// triples; with --platform maccatalyst both Mac Catalyst triples),
// emits their LLVM IR, and compares every callee's predicted physical signature and every frozen
// struct's layout with what swiftc produced. Writes one report per triple, toolchain included, under
// artifacts/. It also records this toolchain's lowering receipts into the SDK's qualification record
// (build/baselines/swift-lowering-evidence.json): one per corpus module on that platform's triples,
// read against that platform's own plan sidecars and wrappers, and one per hand-written transition
// table on every slice its table claims. The record holds one receipt per qualified compiler family,
// so it is rerun under each toolchain profile, once per routed platform. The probe and fixture
// modules run in the unit tests on every `nuke test`; this target adds the whole corpus.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    AbsolutePath SwiftLoweringOracleScratch => RootDirectory / "artifacts" / "swift-lowering-oracle";

    Target SwiftLoweringOracle => _ => _
        .DependsOn(Compile)
        .OnlyWhenStatic(() => OperatingSystem.IsMacOS())
        .After(NativeThunkProbe)
        .Executes(RunSwiftLoweringOracle);

    void RunSwiftLoweringOracle()
    {
        // The iOS corpus is the compile-only gate's regeneration, which the oracle reads in place. Mac
        // Catalyst and tvOS have no compile-only regeneration, so the oracle regenerates each the way
        // its leg does (tvOS through the iOS-family path). Its plan sidecars name the targets it routes,
        // which the probe-route requests keep on their Swift-convention imports whether or not the
        // record already holds that platform's receipts; until it does, those requests cannot be
        // satisfied and the regeneration needs --permissive.
        var platform = ResolvedPlatform;
        if (!SwiftBridgeRoutedPlatforms.Contains(platform.Name, StringComparer.Ordinal))
            Assert.Fail($"swift-lowering-oracle: {platform.Name} does not route through bridges; the corpus is recorded for {string.Join(" and ", SwiftBridgeRoutedPlatforms)}");
        if (platform.Name == ApplePlatform.MacCatalyst.Name)
        {
            RunBuildXcframework(platformOverride: platform);
            RunRegenerateMacOSBindings(strict: Strict, platformOverride: platform);
        }
        else if (platform.Name == ApplePlatform.TvOS.Name)
        {
            RunBuildXcframework(platformOverride: platform);
            RunRegenerateBindings(strict: Strict, platformOverride: platform);
        }
        if (AssertRoutedCorpusPlatform("swift-lowering-oracle") != platform.Name)
            Assert.Fail($"swift-lowering-oracle: the BindingTests output is not a {platform.Name} regeneration; run `nuke binding-tests --compile-only` first");

        var scratch = SwiftLoweringOracleScratch;
        if (Directory.Exists(scratch))
            scratch.DeleteDirectory();
        scratch.CreateDirectory();
        var reports = scratch / "reports";
        var manifest = scratch / "corpus.json";
        // A receipt per artifact: each corpus module of the regenerated BindingTests output, and each
        // hand-written table, whose committed product names its plan targets.
        var wrappers = new System.Collections.Generic.Dictionary<string, string>
        {
            [$"{ModuleName}SwiftBindings"] = BtOutputDir / $"{ModuleName}.Wrapper.swift",
            [$"{DepModuleName}SwiftBindings"] = BtOutputDir / "dep-swift" / $"{DepModuleName}.Wrapper.swift",
        };
        var artifacts = SwiftBridgeCorpusPlanFiles();
        var handWritten = HandWrittenTransitionTables
            .Select(t => new
            {
                table = t.Table.ToString(),
                plans = (SwiftBridgeProductsDir / $"{JsonDocument.Parse(File.ReadAllText(t.Table)).RootElement.GetProperty("assembly").GetString()}.swift-call-plans.json").ToString(),
            })
            .ToArray();
        foreach (var required in wrappers.Values.Concat(artifacts).Concat(handWritten.Select(h => h.plans)).Where(p => !File.Exists(p)))
            Assert.Fail($"{required} is missing: run `nuke binding-tests --compile-only` (and `nuke swift-bridge-products`) first");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            platform = platform.Name,
            dependencyModule = DepModuleName,
            mainModule = ModuleName,
            dependency = GetDepSourceFiles().OrderBy(f => f, StringComparer.Ordinal),
            main = GetMainSourceFiles().OrderBy(f => f, StringComparer.Ordinal),
            reports = reports.ToString(),
            scratch = (scratch / "compile").ToString(),
            wrappers,
            probe = (SourceDir / "Swift.Bindings" / "tests" / "UnitTests" / "LoweringOracle" / "Evidence" / "HandWrittenTargets.swift").ToString(),
            artifacts,
            handWritten,
            evidence = SwiftLoweringEvidencePath.ToString(),
        }));

        Log.Information("=== Swift lowering oracle over {Main} and {Dependency} ===", ModuleName, DepModuleName);
        // The test process inherits this process's environment, so the manifest is named here.
        Environment.SetEnvironmentVariable("SWIFT_LOWERING_ORACLE_CORPUS", manifest);
        try
        {
            DotNetTest(s => s
                .SetProjectFile(SourceDir / "Swift.Bindings" / "tests" / "UnitTests")
                .EnableNoBuild()
                .SetConfiguration("Debug")
                .SetFilter("FullyQualifiedName~SwiftLoweringOracleTests")
                .SetProcessAdditionalArguments($"-- RunConfiguration.DotNetHostPath=\"{DotNetHostPath}\""));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SWIFT_LOWERING_ORACLE_CORPUS", null);
            foreach (var report in Directory.Exists(reports) ? Directory.GetFiles(reports, "*.txt").OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>())
            {
                var lines = File.ReadAllLines(report);
                foreach (var line in lines.Where(l => l.StartsWith("toolchain:", StringComparison.Ordinal) || l.StartsWith("evidence:", StringComparison.Ordinal)
                    || l.Contains(" match, ", StringComparison.Ordinal)))
                    Log.Information("{Report}: {Line}", Path.GetFileNameWithoutExtension(report), line);
            }
            // The IR is large; the reports are what the run leaves behind.
            var compiled = scratch / "compile";
            if (Directory.Exists(compiled))
                compiled.DeleteDirectory();
        }
        Log.Information("Reports: {Reports}; evidence: {Evidence}", reports, SwiftLoweringEvidencePath);
    }
}
