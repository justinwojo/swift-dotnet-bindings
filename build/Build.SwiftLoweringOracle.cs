// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Opt-in, host-only: the swiftc lowering oracle over the BindingTests corpus. Compiles the dependency
// and main modules with swiftc for the device and both simulator triples, emits their LLVM IR, and
// compares every callee's predicted physical signature and every frozen struct's layout with what
// swiftc produced. Writes one report per triple, toolchain included, under artifacts/. It also
// records this toolchain's lowering receipts into the SDK's qualification record
// (build/baselines/swift-lowering-evidence.json): one per corpus module on the iOS triples, and one
// per hand-written transition table on every slice its table claims. The record holds one receipt
// per qualified compiler family, so it is rerun under each toolchain profile. The probe and fixture
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
