// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;
using SwiftBindings.InterpIdentity;
using SwiftBindings.TransitionGate;

/// <summary>
/// <c>nuke binding-tests --interp-identity</c>: the .NET 11 lane-identity harness.
///
/// <para>It builds the dedicated InterpIdentityFixture Swift module, runs it through the generator
/// for the iOS simulator and for Mac Catalyst, consumes each generated binding from a small app
/// (Debug for the interpreter, Release for ReadyToRun), and runs every fixture in its own process
/// on four cells: iOS simulator arm64 and Mac Catalyst x86_64 under Rosetta, each interpreted and
/// precompiled.</para>
///
/// <para>Each cell must prove the lane it claims from method-level receipts (the image and symbol
/// that called each Swift entry point, the crossgen2 inputs and the composite's precompiled
/// bodies), and every fixture must meet its expectation for today's emission: positive controls
/// pass everywhere, negative controls reproduce their interpreter defect, and the x86_64 typed
/// self/error placement is recorded as measured. The harness then re-classifies each
/// interpreter cell's receipts under a ReadyToRun claim and vice versa; a classifier that accepts
/// either mislabel fails the run.</para>
///
/// <para>Opt-in and .NET 11 only. It is not part of <c>--compile-only</c> or any default run.</para>
/// </summary>
partial class Build
{
    [Parameter("Opt-in, .NET 11 only: run the lane-identity harness (generated InterpIdentityFixture binding on the iOS-simulator and Mac Catalyst x86_64 cells, interpreter and ReadyToRun). Never part of the default run or --compile-only.")]
    readonly bool InterpIdentity;

    [Parameter("Comma-separated subset of --interp-identity cells (M1,M2,M3,M4). Default: all four.")]
    readonly string? InterpIdentityCells;

    const string IifModule = "InterpIdentityFixture";
    const string IifWrapperModule = "InterpIdentityFixtureSwiftBindings";
    const string IifAppName = "InterpIdentityApp";
    const string IifBundleId = "com.swiftbindings.interpidentity";
    static readonly TimeSpan IifLaunchTimeout = TimeSpan.FromSeconds(120);

    AbsolutePath IifScratch => RootDirectory / "artifacts" / "interp-identity";
    AbsolutePath IifReceiptsDir => IifScratch / "receipts";
    AbsolutePath IifAppDir => BindingTestsDir / IifAppName;

    sealed record IifBinding(ApplePlatform Platform, AbsolutePath Directory, string ProjectName);

    void RunInterpIdentityHarness()
    {
        Log.Information("=================================================");
        Log.Information(" BindingTests — lane identity (--interp-identity)");
        Log.Information("=================================================");

        if (!RuntimeLane.IsNet11)
            throw new Exception("--interp-identity measures the .NET 11 CoreCLR interpreter and ReadyToRun lanes. " +
                "Run it under the net11 toolchain profile: bash build.sh --toolchain-profile net11-xcode27 -- binding-tests --interp-identity");

        var cells = SelectInterpIdentityCells();
        EnsureGeneratorBuilt();
        if (Directory.Exists(IifScratch)) IifScratch.DeleteDirectory();
        IifReceiptsDir.CreateDirectory();

        var bindings = new Dictionary<string, IifBinding>();
        foreach (var platform in cells.Select(c => c.Platform).Distinct())
            bindings[platform] = GenerateInterpIdentityBinding(platform);

        string? simulator = null;
        if (cells.Any(c => c.Simulator))
        {
            simulator = SimulatorUdidFor("--interp-identity");
            if (string.IsNullOrEmpty(simulator))
                simulator = SimCtl.EnsureBootedDevice().Udid;
            else
                SimCtl.Boot(simulator);
        }

        var verdicts = new List<CellVerdict>();
        var coverage = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var cell in cells)
        {
            var binding = bindings[cell.Platform];
            var (app, inventory, crossgen) = BuildInterpIdentityApp(cell, binding);
            if (!coverage.ContainsKey(cell.Platform))
                coverage[cell.Platform] = CheckInterpIdentityDirectLanes(binding, cell.Configuration, app);

            if (cell.Simulator)
                SimCtl.Install(simulator!, app);
            var receipts = new List<FixtureReceipt>();
            foreach (var fixture in InterpIdentityMatrix.Fixtures)
            {
                var console = cell.Simulator
                    ? RunInterpIdentityOnSimulator(simulator!, fixture.Name)
                    : RunInterpIdentityOnHost(app, fixture.Name);
                File.WriteAllText(IifReceiptsDir / $"{cell.Id}-{fixture.Name}.console.txt", console);
                var receipt = ConsoleReceiptParser.Parse(fixture.Name, console);
                receipts.Add(receipt);
                Log.Information("  {Cell} {Fixture,-22} {Outcome} {Detail}", cell.Id, fixture.Name, receipt.Outcome, receipt.Detail);
            }

            var verdict = CellClassifier.Classify(cell, IifAppName, binding.ProjectName, receipts, inventory, crossgen);
            verdicts.Add(verdict);
        }

        // Seeded mislabels: each cell's real receipts presented under the other lane's claim.
        var mislabels = new List<(string Cell, ExecutionLane Claim, LaneVerdict Verdict)>();
        foreach (var verdict in verdicts)
        {
            var wrong = verdict.Cell.ClaimedLane == ExecutionLane.Interpreter ? ExecutionLane.ReadyToRun : ExecutionLane.Interpreter;
            mislabels.Add((verdict.Cell.Id, wrong, LaneClassifier.Classify(wrong, verdict.Evidence)));
        }

        WriteInterpIdentityReport(verdicts, mislabels, coverage);

        var failures = new List<string>();
        foreach (var (platform, missing) in coverage)
            failures.AddRange(missing.Select(m => $"{platform}: {m}"));
        foreach (var v in verdicts)
        {
            failures.AddRange(v.Lane.Problems.Select(p => $"{v.Cell.Id} lane: {p}"));
            failures.AddRange(v.Fixtures.Where(f => !f.Satisfied).Select(f =>
                $"{v.Cell.Id} {f.Fixture.Name}: expected {f.Expectation}, got {f.Receipt.Outcome} ({f.Receipt.Detail})"));
        }
        foreach (var (cell, claim, verdict) in mislabels.Where(m => m.Verdict.Accepted))
            failures.Add($"{cell}: the classifier accepted the seeded mislabel '{claim}'");

        if (failures.Count > 0)
        {
            foreach (var f in failures) Log.Error("  ✗ {Failure}", f);
            throw new Exception($"--interp-identity: {failures.Count} problem(s); receipts under {IifReceiptsDir}.");
        }
        Log.Information("--interp-identity PASSED: {Cells} cell(s) proved their lane, every fixture met its expectation, " +
            "and every seeded mislabel was rejected. Receipts: {Dir}", verdicts.Count, IifReceiptsDir);
    }

    IReadOnlyList<IdentityCell> SelectInterpIdentityCells()
    {
        if (string.IsNullOrWhiteSpace(InterpIdentityCells))
            return InterpIdentityMatrix.Cells;
        return InterpIdentityCells.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(InterpIdentityMatrix.Cell).Distinct().ToList();
    }

    // MARK: - Fixture and generated binding

    IifBinding GenerateInterpIdentityBinding(string platformName)
    {
        var platform = ApplePlatform.FromName(platformName);
        var root = IifScratch / platformName;
        var xcframework = BuildInterpIdentityXcframework(platformName, root / "build");
        // The generated csproj references its native framework as ../<Module>.xcframework.
        var placed = root / $"{IifModule}.xcframework";
        Directory.Move(xcframework, placed);

        var output = root / "output";
        output.CreateDirectory();
        var args = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{placed}\"",
            $"-o \"{output}\"",
            $"--platform {platform.Name}",
            $"--target-framework {platform.GetTfm(BtFramework)}",
            $"--async-library {IifWrapperModule}",
            "--strict-inputs",
            // The app build below is the C# compile gate.
            "--no-verify-csharp",
        };
        // Catalyst ships as one universal slice, as real libraries do; the wrapper must cover the
        // x86_64 cell as well as arm64.
        if (platformName == "maccatalyst")
            args.Add("--target-architectures arm64,x86_64");

        Log.Information("=== interp-identity: generating the {Platform} binding ===", platform.Name);
        var proc = ProcessTasks.StartProcess("dotnet", string.Join(" ", args), workingDirectory: BindingTestsDir, logOutput: false);
        proc.WaitForExit();
        File.WriteAllLines(IifReceiptsDir / $"generator-{platformName}.log", proc.Output.Select(o => o.Text));
        if (proc.ExitCode != 0)
            throw new Exception($"--interp-identity: the generator exited {proc.ExitCode} for {platformName} (fail-closed); " +
                $"log at {IifReceiptsDir / $"generator-{platformName}.log"}.");

        var project = Directory.GetFiles(output, $"{IifModule}.Swift.*.csproj").SingleOrDefault()
            ?? throw new Exception($"--interp-identity: the generator emitted no binding project in {output}.");
        return new IifBinding(platform, output, Path.GetFileNameWithoutExtension(project));
    }

    AbsolutePath BuildInterpIdentityXcframework(string platformName, AbsolutePath buildRoot)
    {
        buildRoot.CreateDirectory();
        var sources = Directory.GetFiles(BindingTestsDir / "Sources" / IifModule, "*.swift", SearchOption.AllDirectories).ToList();
        if (sources.Count == 0)
            throw new Exception($"--interp-identity: no Swift sources under {BindingTestsDir / "Sources" / IifModule}.");

        string frameworkDir;
        if (platformName == "ios")
        {
            var ios = ApplePlatform.IOS;
            var slice = buildRoot / ios.SimulatorSliceId;
            frameworkDir = slice / $"{IifModule}.framework";
            CompileModuleSlice(IifModule, ios.SimulatorTarget, XcRun.GetSdkPath(ios.SimulatorSdkName), ios.SimulatorModuleSuffix,
                ios.MinOsVersion, ios.SimulatorPlistPlatform, frameworkDir, sources, frameworkSearchPaths: [slice.ToString()]);
        }
        else
        {
            // One universal Catalyst framework, as real libraries ship; the x86_64 cell needs it.
            frameworkDir = BuildFatFrameworkSlice(buildRoot, "maccatalyst", sources,
                armTarget: ApplePlatform.MacCatalyst.SimulatorTarget, armSuffix: ApplePlatform.MacCatalyst.SimulatorModuleSuffix,
                x64Target: ApplePlatform.MacCatalystX64.SimulatorTarget, x64Suffix: ApplePlatform.MacCatalystX64.SimulatorModuleSuffix,
                sdkName: ApplePlatform.MacCatalyst.SimulatorSdkName, minOs: ApplePlatform.MacCatalyst.MinOsVersion,
                plistPlatform: ApplePlatform.MacCatalyst.SimulatorPlistPlatform, module: IifModule);
        }

        var xcframework = buildRoot / $"{IifModule}.xcframework";
        XcodeBuild.ExecuteCreateXcframework(new CreateXcframeworkSettings().AddFrameworkPath(frameworkDir).SetOutputPath(xcframework));
        return xcframework;
    }

    // The negative controls are only controls while the generator still binds them on a direct
    // Swift transition. Scan the built binding assembly with the transition gate's own engine.
    // The app bundle supplies the framework assemblies the scan resolves carrier types against.
    IReadOnlyList<string> CheckInterpIdentityDirectLanes(IifBinding binding, string configuration, AbsolutePath app)
    {
        var dll = Directory.GetFiles(binding.Directory / "bin" / configuration, $"{binding.ProjectName}.dll", SearchOption.AllDirectories)
            .FirstOrDefault() ?? throw new Exception($"--interp-identity: built binding {binding.ProjectName}.dll not found under {binding.Directory / "bin"}.");
        var frameworkDir = Directory.GetFiles(app, "System.Private.CoreLib.dll", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
            ?? throw new Exception($"--interp-identity: {app} carries no System.Private.CoreLib.dll to resolve the scan's references against.");
        var report = SwiftTransitionScanner.Scan(dll, new SwiftTransitionScanOptions { ReferenceDirectories = [Path.GetDirectoryName(dll)!, frameworkDir!] });
        var lines = report.Violations.Select(v => $"{v.Rule} {v.Member} [{v.Signature}]").ToList();
        File.WriteAllLines(IifReceiptsDir / $"transitions-{binding.Platform.Name}.txt", lines);
        var missing = DirectLaneCoverage.Missing(report.Violations.Select(v => (v.Rule.ToString(), v.Member))).ToList();
        if (report.InspectionErrors.Length > 0)
            missing.AddRange(report.InspectionErrors.Select(e => $"transition scan inspection error: {e.Member}: {e.Message}"));
        return missing;
    }

    // MARK: - App build and receipts

    (AbsolutePath App, ReadyToRunInventory Inventory, Crossgen2Invocation Crossgen) BuildInterpIdentityApp(IdentityCell cell, IifBinding binding)
    {
        foreach (var dir in new[] { IifAppDir / "bin" / cell.RuntimeIdentifier / cell.Configuration, IifAppDir / "obj" / cell.RuntimeIdentifier / cell.Configuration })
            if (Directory.Exists(dir)) ((AbsolutePath)dir).DeleteDirectory();

        Log.Information("=== interp-identity: building {Cell} ({Label}) ===", cell.Id, cell.Label);
        var args = string.Join(" ",
            "build", $"\"{IifAppDir / $"{IifAppName}.csproj"}\"", "-c", cell.Configuration, "-v:n", "-nologo",
            $"-p:IifTargetFramework={BtFramework}",
            $"-p:IifRuntimeIdentifier={cell.RuntimeIdentifier}",
            $"-p:IifBindingDir=\"{binding.Directory}\"",
            $"-p:IifBindingProjectName={binding.ProjectName}",
            $"-p:SwiftBindingsRepoRoot=\"{RootDirectory}\"");
        var proc = ProcessTasks.StartProcess("dotnet", args, workingDirectory: IifAppDir, logOutput: false);
        proc.WaitForExit();
        var log = string.Join("\n", proc.Output.Select(o => o.Text));
        var logPath = IifReceiptsDir / $"{cell.Id}-build.log";
        File.WriteAllText(logPath, log);
        if (proc.ExitCode != 0)
            throw new Exception($"--interp-identity: {cell.Id} app build failed (exit {proc.ExitCode}); log at {logPath}.");

        var bin = IifAppDir / "bin" / cell.RuntimeIdentifier / cell.Configuration;
        var app = Directory.GetDirectories(bin, $"{IifAppName}.app", SearchOption.AllDirectories).SingleOrDefault()
            ?? throw new Exception($"--interp-identity: {cell.Id} built no {IifAppName}.app under {bin}.");

        var obj = IifAppDir / "obj" / cell.RuntimeIdentifier / cell.Configuration;
        var map = Directory.GetFiles(obj, $"{IifAppName}.r2r.map.csv", SearchOption.AllDirectories).SingleOrDefault()
            ?? throw new Exception($"--interp-identity: {cell.Id} produced no crossgen2 map under {obj}; the build did not run crossgen2.");
        var inventory = ReadyToRunInventory.ParseMapCsv(File.ReadAllText(map));
        var invocations = Crossgen2Invocation.ParseAll(log);
        if (invocations.Count != 1)
            throw new Exception($"--interp-identity: {cell.Id} build log holds {invocations.Count} crossgen2 invocations, expected exactly one; log at {logPath}.");
        WriteInterpIdentityInventory(cell, binding, inventory, invocations[0]);
        return ((AbsolutePath)app, inventory, invocations[0]);
    }

    void WriteInterpIdentityInventory(IdentityCell cell, IifBinding binding, ReadyToRunInventory inventory, Crossgen2Invocation crossgen)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {cell.Id} {cell.Label}");
        sb.AppendLine($"# crossgen2: rooted {crossgen.Rooted.Count}, -r: {crossgen.References.Count}, -u: {crossgen.Unrooted.Count}");
        sb.AppendLine($"# rooted: {string.Join(", ", crossgen.Rooted)}");
        sb.AppendLine($"# options: {string.Join(" ", crossgen.Options)}");
        sb.AppendLine($"# precompiled bodies: {inventory.Total}");
        foreach (var assembly in new[] { binding.ProjectName, IifAppName, "Swift.Runtime", "SwiftBindings.Apple", "S.P.CoreLib" })
            sb.AppendLine($"{inventory.BodiesFor(assembly),8}  {assembly}");
        sb.AppendLine($"# {binding.ProjectName} bodies:");
        var prefix = ReadyToRunInventory.SymbolPrefix(binding.ProjectName);
        foreach (var s in inventory.Symbols.Where(s => s.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            sb.AppendLine(s);
        File.WriteAllText(IifReceiptsDir / $"{cell.Id}-r2r-inventory.txt", sb.ToString());
    }

    static string RunInterpIdentityOnSimulator(string udid, string fixture)
    {
        var result = LaunchUntilAppRuns(
            () => SimCtl.Launch(udid, IifBundleId, ["--iif-fixture", fixture], IifLaunchTimeout, IifAppName),
            $"--interp-identity ({fixture})");
        return result.Output + $"\n[harness] launcher result={result.Result} exit={result.ExitCode?.ToString() ?? "-"} " +
            $"crash-log={result.CrashLogPath ?? "-"}\n";
    }

    string RunInterpIdentityOnHost(AbsolutePath app, string fixture)
    {
        var exe = app / "Contents" / "MacOS" / IifAppName;
        using var process = new Process { StartInfo = BuildHostLaunchStartInfo(exe, ["--iif-fixture", fixture], underRosetta: true) };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var exited = process.WaitForExit((int)IifLaunchTimeout.TotalMilliseconds);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }
        process.WaitForExit();
        lock (output)
            return output + $"\n[harness] exited={exited} exit={(exited ? process.ExitCode.ToString() : "timeout")}\n";
    }

    // MARK: - Report

    void WriteInterpIdentityReport(
        IReadOnlyList<CellVerdict> verdicts,
        IReadOnlyList<(string Cell, ExecutionLane Claim, LaneVerdict Verdict)> mislabels,
        IReadOnlyDictionary<string, IReadOnlyList<string>> coverage)
    {
        var json = new
        {
            schema = 1,
            tool = "interp-identity",
            framework = BtFramework,
            toolchain = ToolchainFingerprint,
            passed = verdicts.All(v => v.Passed) && mislabels.All(m => !m.Verdict.Accepted) && coverage.Values.All(c => c.Count == 0),
            directLaneCoverage = coverage.ToDictionary(kv => kv.Key, kv => kv.Value),
            cells = verdicts.Select(v => new
            {
                id = v.Cell.Id,
                label = v.Cell.Label,
                rid = v.Cell.RuntimeIdentifier,
                configuration = v.Cell.Configuration,
                claimedLane = v.Cell.ClaimedLane.ToString(),
                derivedLane = v.Lane.Derived?.ToString(),
                laneAccepted = v.Lane.Accepted,
                laneProblems = v.Lane.Problems,
                bindingBodies = v.Evidence.Inventory.BodiesFor(v.Evidence.BindingAssembly),
                appBodies = v.Evidence.Inventory.BodiesFor(IifAppName),
                crossgenRooted = v.Evidence.Crossgen.Rooted,
                fixtures = v.Fixtures.Select(f => new
                {
                    name = f.Fixture.Name,
                    shape = f.Fixture.Shape,
                    expectation = f.Expectation.ToString(),
                    outcome = f.Receipt.Outcome.ToString(),
                    satisfied = f.Satisfied,
                    detail = f.Receipt.Detail,
                    observations = f.Receipt.Observations,
                    caller = ConsoleReceiptParser.Caller(f.Receipt) is { } c ? $"{c.Image}!{c.Symbol}" : null,
                    start = f.Receipt.Start,
                    readyToRunGap = v.Cell.ClaimedLane == ExecutionLane.ReadyToRun ? f.Fixture.ReadyToRunGap : null,
                }),
            }),
            seededMislabels = mislabels.Select(m => new
            {
                cell = m.Cell,
                claimed = m.Claim.ToString(),
                rejected = !m.Verdict.Accepted,
                reasons = m.Verdict.Problems,
            }),
        };
        File.WriteAllText(IifReceiptsDir / "interp-identity-report.json",
            JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));

        var sb = new StringBuilder();
        foreach (var v in verdicts)
        {
            sb.AppendLine($"{v.Cell.Id} {v.Cell.Label}: lane {(v.Lane.Accepted ? "PROVEN" : "NOT PROVEN")} " +
                $"(claimed {v.Cell.ClaimedLane}, derived {v.Lane.Derived?.ToString() ?? "none"})");
            foreach (var p in v.Lane.Problems) sb.AppendLine($"    lane: {p}");
            foreach (var f in v.Fixtures)
            {
                var caller = ConsoleReceiptParser.Caller(f.Receipt);
                sb.AppendLine($"  {(f.Satisfied ? "ok  " : "BAD ")} {f.Fixture.Name,-22} expected {f.Expectation,-9} got {f.Receipt.Outcome,-7} " +
                    $"caller {(caller == null ? "-" : $"{caller.Image}!{caller.Symbol}")}");
                if (f.Receipt.Detail.Length > 0) sb.AppendLine($"        {f.Receipt.Detail}");
                if (v.Cell.ClaimedLane == ExecutionLane.ReadyToRun && f.Fixture.ReadyToRunGap is { } gap)
                    sb.AppendLine($"        interpreted under ReadyToRun: {gap.Method} is not precompiled ({gap.Reason})");
                foreach (var o in f.Receipt.Observations) sb.AppendLine($"        observe: {o}");
            }
        }
        foreach (var (cell, claim, verdict) in mislabels)
            sb.AppendLine($"mislabel {cell} as {claim}: {(verdict.Accepted ? "ACCEPTED (harness defect)" : "rejected")}" +
                (verdict.Problems.Count > 0 ? $" — {verdict.Problems[0]}" : ""));
        foreach (var (platform, missing) in coverage)
            sb.AppendLine($"direct-lane coverage {platform}: {(missing.Count == 0 ? "every control is direct" : string.Join("; ", missing))}");
        File.WriteAllText(IifReceiptsDir / "interp-identity-summary.txt", sb.ToString());
        Log.Information("{Summary}", sb.ToString());
    }
}
