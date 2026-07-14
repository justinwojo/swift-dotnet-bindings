// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.BindingTests.PartialSuccessKitchen.cs — opt-in "partial-success kitchen" product gate.
//
// Proves the day-1 product promise for a third-party consumer: a tiny pure-Swift library that
// intentionally contains a few hard, unsupported shapes still yields a CLEAN PARTIAL binding —
// the generator exits 0, the emitted C# compiles, and the skip report honestly accounts for every
// dropped shape with a defensible disposition (no Review-tier surprises, no dangling wrapper
// symbols). The fixture lives at BindingTests/Sources/PartialSuccessKitchen/ and deliberately mixes
// two must-emit "positive control" types with a dozen skip shapes (SwiftUI View, PATs/existentials,
// closure-bearing members, parameter packs, internal-parent members, Codable synthesis, …).
//
// This is a compile-gate-shaped leg (host-only: build a standalone simulator-slice xcframework,
// generate, compile-check the generated C#, assert the report) with NO app build and NO sim/device
// run — so it is fast and hermetic, but it is OPT-IN and never part of the default `nuke
// binding-tests` run or `--compile-only`. It is fail-closed on all of its assertions: a non-zero
// generator exit, a C# compile failure, an unmet design floor, or drift against the frozen
// build/baselines/partial-success-kitchen-baseline.json all fail the gate.

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
    [Parameter("Opt-in: build the PartialSuccessKitchen fixture, generate + compile it, and assert the skip report against build/baselines/partial-success-kitchen-baseline.json. Host-only; never part of the default run or --compile-only.")]
    readonly bool PartialSuccessKitchen;

    const string KitchenModule = "PartialSuccessKitchen";
    const string KitchenWrapperModule = "PartialSuccessKitchenSwiftBindings";

    // The two must-emit positive controls (§5 of the fixture design). Their presence in the
    // generated C# is what proves the binding is a genuine PARTIAL success, not an empty shell.
    static readonly string[] KitchenPositiveControls = { "KitchenOk", "KitchenOkClass" };

    AbsolutePath KitchenSourceDir => BindingTestsDir / "Sources" / KitchenModule;
    AbsolutePath KitchenScratch => RootDirectory / "artifacts" / "partial-success-kitchen";
    AbsolutePath KitchenBaselinePath => BaselinesDir / "partial-success-kitchen-baseline.json";

    void RunPartialSuccessKitchenGate()
    {
        Log.Information("=================================================");
        Log.Information(" BindingTests — partial-success kitchen gate");
        Log.Information("=================================================");

        EnsureGeneratorBuilt();

        var scratch = KitchenScratch;
        if (Directory.Exists(scratch)) scratch.DeleteDirectory();
        scratch.CreateDirectory();

        // 1. Build a standalone simulator-slice xcframework from the kitchen source. One slice is
        //    enough: generation reads the swiftinterface + ABI JSON, and this gate never links or
        //    runs the binding on a device.
        var xcframework = BuildKitchenXcframework(scratch / "build");

        // 2. Generate — capture the live process exit code (the primary product signal).
        var outputDir = scratch / "output";
        outputDir.CreateDirectory();
        var (exitCode, generatorLog) = RunKitchenGenerator(xcframework, outputDir);

        if (exitCode != 0)
        {
            Log.Error("--partial-success-kitchen: generator exited {ExitCode} (expected 0). A module of intentionally-"
                + "unsupported shapes must still generate a clean partial binding.", exitCode);
            foreach (var line in generatorLog) Log.Error("  [generator] {Line}", line);
            throw new Exception($"--partial-success-kitchen: generator exit {exitCode} ≠ 0 (fail-closed).");
        }

        // SWIFTBIND108 (dangling wrapper EntryPoint) is a hard generation-time integrity failure, so
        // exit 0 already implies it did not fire — assert on the captured log too so a future change
        // that demotes it can never slip a dangling symbol through this gate silently.
        if (generatorLog.Any(l => l.Contains("SWIFTBIND108", StringComparison.Ordinal)))
            throw new Exception("--partial-success-kitchen: generator log contains SWIFTBIND108 (dangling wrapper EntryPoint) — integrity must stay hard.");

        // 3. Compile-check the generated C# — a real `dotnet build`, not a string assert.
        AssertKitchenCompiles(outputDir);

        // 4. Positive controls must be present in the emitted C# (the "partial" is not an empty shell).
        AssertKitchenPositiveControls(outputDir);

        // 5. Parse the skip report and enforce the design floors + exact drift against the baseline.
        var reportPath = outputDir / "binding-report.json";
        if (!File.Exists(reportPath))
            throw new Exception($"--partial-success-kitchen: binding-report.json missing at {reportPath} — cannot verify the skip report.");
        var report = KitchenReportProjection.ParseReport(File.ReadAllText(reportPath));

        var floorFailures = PartialSuccessKitchenBaseline.CheckFloors(report);
        if (floorFailures.Count > 0)
        {
            foreach (var f in floorFailures) Log.Error("  ✗ floor: {Failure}", f);
            throw new Exception($"--partial-success-kitchen: {floorFailures.Count} design floor(s) violated — see log.");
        }
        LogKitchenReport(report);

        var baseline = PartialSuccessKitchenBaseline.Load(KitchenBaselinePath);
        var baselineEmpty = baseline.ByReason.Count == 0 && baseline.ByDisposition.Count == 0;
        if (baselineEmpty)
        {
            // First green run seeds the frozen budget. Floors already passed above, so the seed can
            // never capture a degenerate report. Commit the emitted baseline alongside the fixture.
            var seeded = PartialSuccessKitchenBaseline.FromReport(report, ReadHeadShaShort());
            seeded.Save(KitchenBaselinePath);
            Log.Warning("--partial-success-kitchen: no committed baseline found — SEEDED {Path} from this run. "
                + "Review and commit it; subsequent runs compare exactly against it.", KitchenBaselinePath);
        }
        else
        {
            var drift = baseline.Compare(report);
            if (drift.Count > 0)
            {
                foreach (var d in drift) Log.Error("  ✗ drift: {Drift}", d);
                throw new Exception($"--partial-success-kitchen: {drift.Count} report drift(s) vs {KitchenBaselinePath.Name}. "
                    + "Either the generator regressed on these shapes, or the change is intentional and the baseline must be "
                    + "reseeded in the same commit.");
            }
            Log.Information("--partial-success-kitchen: report matches frozen baseline (git_sha {Sha}).", baseline.GitSha);
        }

        Log.Information("--partial-success-kitchen PASSED — generator exit 0, C# compiles, {Controls} positive controls emitted, "
            + "ReviewCount 0, report honest.", KitchenPositiveControls.Length);
    }

    // Builds a single simulator-slice xcframework from the kitchen Swift source, reusing the shared
    // CompileModuleSlice recipe (dylib + swiftinterface + ABI JSON + TBD + plist) so the generator
    // sees exactly the artifact shape it consumes for the main test lib.
    AbsolutePath BuildKitchenXcframework(AbsolutePath buildRoot)
    {
        if (Directory.Exists(buildRoot)) buildRoot.DeleteDirectory();
        buildRoot.CreateDirectory();

        var ios = ApplePlatform.IOS;
        var sdkPath = XcRun.GetSdkPath(ios.SimulatorSdkName);
        var simBuildDir = buildRoot / ios.SimulatorSliceId;
        var frameworkDir = simBuildDir / $"{KitchenModule}.framework";

        var sources = Directory.GetFiles(KitchenSourceDir, "*.swift", SearchOption.AllDirectories).ToList();
        if (sources.Count == 0)
            throw new Exception($"--partial-success-kitchen: no Swift sources found under {KitchenSourceDir}.");

        Log.Information("=== partial-success-kitchen: building {Module} simulator slice ({Count} source file(s)) ===",
            KitchenModule, sources.Count);
        CompileModuleSlice(
            KitchenModule, ios.SimulatorTarget, sdkPath,
            ios.SimulatorModuleSuffix, ios.MinOsVersion, ios.SimulatorPlistPlatform,
            frameworkDir, sources, frameworkSearchPaths: new[] { simBuildDir.ToString() });

        var xcframeworkPath = buildRoot / $"{KitchenModule}.xcframework";
        if (Directory.Exists(xcframeworkPath)) xcframeworkPath.DeleteDirectory();
        XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings()
            .AddFrameworkPath(frameworkDir)
            .SetOutputPath(xcframeworkPath));

        Log.Information("  built kitchen xcframework: {Path}", xcframeworkPath);
        return xcframeworkPath;
    }

    // Runs the generator against the kitchen xcframework and returns its exit code + captured output.
    // --strict-inputs is intentionally OFF: the kitchen proves the DEFAULT product path (a plain
    // consumer generate), where unsupported shapes are honest skips, not degraded-input hard fails.
    (int ExitCode, IReadOnlyList<string> Log) RunKitchenGenerator(AbsolutePath xcframework, AbsolutePath outputDir)
    {
        var genArgs = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{xcframework}\"",
            $"-o \"{outputDir}\"",
            $"--async-library {KitchenWrapperModule}",
        };

        Log.Information("=== partial-success-kitchen: generating bindings ===");
        var proc = ProcessTasks.StartProcess("dotnet", string.Join(" ", genArgs),
            workingDirectory: BindingTestsDir, logOutput: false);
        proc.WaitForExit();
        File.WriteAllText(outputDir / "generator-exit-code", proc.ExitCode.ToString());
        return (proc.ExitCode, proc.Output.Select(o => o.Text).ToList());
    }

    // Compile-checks the generated C# with a hermetic csproj that references only Swift.Runtime +
    // Swift.Bindings.Apple (the SwiftUI bridge stubs) — the same shape as CompileCheck.csproj, but
    // scoped to the kitchen output so a compile error here is unambiguously the kitchen binding.
    void AssertKitchenCompiles(AbsolutePath outputDir)
    {
        var csprojDir = outputDir / ".compile-check";
        if (Directory.Exists(csprojDir)) csprojDir.DeleteDirectory();
        csprojDir.CreateDirectory();
        var csprojPath = csprojDir / "KitchenCompileCheck.csproj";

        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Library</OutputType>
                <TargetFramework>net10.0-ios</TargetFramework>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <Nullable>enable</Nullable>
                <NoWarn>CS0169;CS0649;CA1418;CA1420</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <AssemblyAttribute Include="System.Runtime.CompilerServices.DisableRuntimeMarshallingAttribute" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="{SourceDir / "Swift.Runtime" / "src" / "Swift.Runtime.csproj"}" />
                <ProjectReference Include="{SourceDir / "Swift.Bindings.Apple" / "Swift.Bindings.Apple.csproj"}" />
              </ItemGroup>
              <ItemGroup>
                <Compile Include="{outputDir / $"{KitchenModule}.cs"}" />
                <Compile Include="{outputDir / $"{KitchenModule}.Types.*.cs"}" />
                <Compile Include="{outputDir / $"{KitchenModule}.SwiftUIBridge.cs"}"
                         Condition="Exists('{outputDir / $"{KitchenModule}.SwiftUIBridge.cs"}')" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(csprojPath, csproj);

        Log.Information("=== partial-success-kitchen: compile-checking generated C# ===");
        DotNetBuild(s => s
            .SetProjectFile(csprojPath)
            .SetConfiguration("Debug")
            .SetVerbosity(DotNetVerbosity.quiet));
        Log.Information("  generated C# compiled cleanly.");
    }

    // Confirms each positive-control type is DECLARED in the emitted C# — a partial binding that
    // dropped its must-emit surface would be a false "success". Match a real declaration, not a
    // stray reference, by anchoring on `class {Name}` / `struct {Name}`.
    void AssertKitchenPositiveControls(AbsolutePath outputDir)
    {
        var emitted = string.Concat(Directory
            .EnumerateFiles(outputDir, $"{KitchenModule}*.cs", SearchOption.TopDirectoryOnly)
            .Select(File.ReadAllText));

        // Boundary-anchored so one control name being a prefix of another (KitchenOk vs
        // KitchenOkClass) can't make a dropped type pass via a substring hit on its sibling.
        var missing = KitchenPositiveControls
            .Where(t => !System.Text.RegularExpressions.Regex.IsMatch(
                emitted, $@"\b(class|struct)\s+{System.Text.RegularExpressions.Regex.Escape(t)}\b"))
            .ToList();

        if (missing.Count > 0)
            throw new Exception($"--partial-success-kitchen: positive-control type(s) not emitted: {string.Join(", ", missing)}. "
                + "The must-emit surface was lost — this is not a partial success.");
        Log.Information("  positive controls emitted: {Controls}", string.Join(", ", KitchenPositiveControls));
    }

    static void LogKitchenReport(KitchenReportProjection report)
    {
        Log.Information("  kitchen skip report: ReviewCount={Review}", report.ReviewCount);
        foreach (var kv in report.ByDisposition.OrderBy(k => k.Key, StringComparer.Ordinal))
            Log.Information("    disposition {Key}: {Count}", kv.Key, kv.Value);
        foreach (var kv in report.ByReason.OrderBy(k => k.Key, StringComparer.Ordinal))
            Log.Information("    reason {Key}: {Count}", kv.Key, kv.Value);
    }
}
