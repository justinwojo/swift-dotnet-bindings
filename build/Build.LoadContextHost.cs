// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.LoadContextHost.cs — the load-context ownership leg of the macOS lanes (`--macos`,
// `--macos-x64`), run after the suite.
//
// A binding's reverse-dispatch vtables and SwiftBindings.Runtime's closure-destroy hook are
// process-global native registrations, so each has exactly one managed owner per process: the first
// load context to initialize the module (or the runtime) owns it, and any other copy is refused with
// SwiftLoadContextConflictException before it registers anything native
// (src/Swift.Runtime/src/Swift/Runtime/NativeRegistrationOwnership.cs).
//
// This leg proves that on real generated bindings in separate processes. It builds the BindingTests
// binding as its own assembly (BindingTests/LoadContextHost/Binding), a probe that drives it from
// inside a load context (Probe), and a macOS host that references neither (Host). The binding, the
// probe, SwiftBindings.Runtime and SwiftBindings.Apple are staged under the host's
// Contents/MonoBundle/lc/, so the default context loads them only when a scenario asks it to. Each
// scenario runs in its own process, because the failure the claim prevents fail-fasts the process.
// A scenario reads Swift's installed vtables back before and after a refused attempt and requires
// them unchanged, and requires the first owner's conformers and closures to keep working.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    static readonly string[] LoadContextStagedAssemblies =
        ["Swift.Runtime", "SwiftBindings.Apple", "SwiftBindingsTestLib", "LoadContextProbe"];

    /// <summary>
    /// The scenarios the gate requires. The host lists its own (<c>--list</c>); the two must agree, so
    /// a scenario dropped from the host fails the gate instead of silently not running.
    /// </summary>
    static readonly string[] LoadContextRequiredScenarios =
    [
        "default-then-noncollectible",
        "default-then-collectible",
        "noncollectible-then-default",
        "collectible-then-default",
        "collectible-then-collectible",
        "private-runtime-refused",
        "private-runtime-first",
        "plugin-shared-binding",
        "measure-keepalive-binding-init",
        "measure-keepalive-binding-attached",
        "measure-keepalive-private-runtime",
    ];

    AbsolutePath LoadContextHostDir => BindingTestsDir / "LoadContextHost";

    void RunLoadContextOwnershipLeg(ApplePlatform platform)
    {
        var label = platform.RunUnderRosetta ? "macOS x64 (Rosetta)" : "macOS";
        Log.Information("=========================================");
        Log.Information(" Load-context ownership ({Label})", label);
        Log.Information("=========================================");

        var appBundle = LoadContextHostDir / "Host" / RuntimeLane.AppBinDirectory(false) / BtConfiguration /
            $"{BtFramework}-macos" / platform.Rid! / "LoadContextHost.app";
        var exe = appBundle / "Contents" / "MacOS" / "LoadContextHost";

        if (SkipBuild)
        {
            if (!File.Exists(exe))
                throw new Exception($"--skip-build: the load-context host was never built at {appBundle}. Drop --skip-build.");
        }
        else
        {
            BuildLoadContextHost(platform, appBundle);
        }

        var listed = RunLoadContextHost(exe, ["--list"], platform.RunUnderRosetta, TimeSpan.FromSeconds(60));
        var scenarios = listed.Output.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var missing = LoadContextRequiredScenarios.Except(scenarios).ToList();
        var extra = scenarios.Except(LoadContextRequiredScenarios).ToList();
        if (listed.ExitCode != 0 || missing.Count > 0 || extra.Count > 0)
            throw new Exception(
                $"Load-context host scenario list does not match the gate (exit {listed.ExitCode}; " +
                $"missing: {string.Join(", ", missing)}; unexpected: {string.Join(", ", extra)}).");

        var logDir = (AbsolutePath)Path.GetTempPath() / $"swift-bindings-loadcontext-{platform.Rid}{RuntimeEvidenceSuffix()}";
        if (Directory.Exists(logDir)) logDir.DeleteDirectory();
        Directory.CreateDirectory(logDir);

        var failures = new List<string>();
        var measures = new List<string>();
        foreach (var scenario in LoadContextRequiredScenarios)
        {
            var run = RunLoadContextHost(exe, ["--scenario", scenario], platform.RunUnderRosetta, TimeSpan.FromSeconds(180));
            File.WriteAllLines(logDir / $"{scenario}.log", run.Output);
            var passed = run.ExitCode == 0 && run.Output.Contains($"LC-RESULT {scenario} PASS");
            Log.Information("  {Verdict} {Scenario} (exit {Exit})", passed ? "PASS" : "FAIL", scenario,
                run.TimedOut ? "timeout" : run.ExitCode?.ToString() ?? "?");
            foreach (var line in run.Output.Where(l => l.StartsWith("LC-REFUSED", StringComparison.Ordinal)
                                                     || l.StartsWith("LC-MEASURE", StringComparison.Ordinal)
                                                     || l.StartsWith("LC-FAIL", StringComparison.Ordinal)
                                                     || l.StartsWith("LC-RED", StringComparison.Ordinal)))
                Log.Information("      {Line}", line);
            measures.AddRange(run.Output.Where(l => l.StartsWith("LC-MEASURE", StringComparison.Ordinal))
                .Select(l => $"{scenario}: {l}"));
            if (!passed)
            {
                failures.Add(scenario);
                Log.Error("--- {Scenario} output ---\n{Output}", scenario, string.Join("\n", run.Output));
            }
        }

        Log.Information("Load-context ownership logs: {Dir}", logDir);
        if (failures.Count > 0)
            throw new Exception($"Load-context ownership ({label}): {failures.Count} scenario(s) failed: {string.Join(", ", failures)}. Logs: {logDir}");
        Log.Information("Load-context ownership ({Label}): all {Count} scenarios passed.", label, LoadContextRequiredScenarios.Length);
    }

    void BuildLoadContextHost(ApplePlatform platform, AbsolutePath appBundle)
    {
        Log.Information("--- Building the load-context probe and binding ---");
        DotNetBuild(s => s
            .SetProjectFile(LoadContextHostDir / "Probe" / "LoadContextProbe.csproj")
            .SetConfiguration(BtConfiguration)
            .SetProperty("BtTargetFramework", BtFramework)
            .SetVerbosity(DotNetVerbosity.quiet));
        var probeOut = LoadContextHostDir / "Probe" / "bin" / BtConfiguration / $"{BtFramework}-macos";

        Log.Information("--- Building LoadContextHost ---");
        if (Directory.Exists(appBundle)) appBundle.DeleteDirectory();
        DotNetBuild(s => s
            .SetProjectFile(LoadContextHostDir / "Host" / "LoadContextHost.csproj")
            .SetConfiguration(BtConfiguration)
            .SetProperty("BtTargetFramework", BtFramework)
            .SetProperty("BtRuntimeEngine", "coreclr")
            .SetProperty("BtRuntimeIdentifier", platform.Rid!)
            .SetProperty("BtNativeWrapperSlice", platform.SimulatorSliceId)
            .SetVerbosity(DotNetVerbosity.quiet));
        if (!Directory.Exists(appBundle))
            throw new Exception($"Build failed - load-context host bundle not found at {appBundle}");

        var monoBundle = appBundle / "Contents" / "MonoBundle";
        // The host must not carry any staged assembly itself: the default context would then load it
        // from the app, and a scenario could not choose where each copy lives.
        var leaked = LoadContextStagedAssemblies.Where(a => File.Exists(monoBundle / $"{a}.dll")).ToList();
        if (leaked.Count > 0)
            throw new Exception($"LoadContextHost bundles {string.Join(", ", leaked)} directly; it must reference none of them.");

        var lc = monoBundle / "lc";
        lc.CreateDirectory();
        foreach (var assembly in LoadContextStagedAssemblies)
        {
            var source = probeOut / $"{assembly}.dll";
            if (!File.Exists(source))
                throw new Exception($"Probe build did not produce {source}.");
            File.Copy(source, lc / $"{assembly}.dll", overwrite: true);
        }

        InjectMacOSNativeLibraries(monoBundle, platform);
        CodesignMacOSApp(appBundle, "LoadContextHost");
    }

    sealed record LoadContextHostRun(List<string> Output, int? ExitCode, bool TimedOut);

    static LoadContextHostRun RunLoadContextHost(AbsolutePath exe, List<string> args, bool underRosetta, TimeSpan timeout)
    {
        var output = new ConcurrentQueue<string>();
        using var process = new Process { StartInfo = BuildHostLaunchStartInfo(exe, args, underRosetta) };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = !process.WaitForExit((int)timeout.TotalMilliseconds);
        if (timedOut)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* already gone */ }
        }
        process.WaitForExit();
        return new LoadContextHostRun(output.ToList(), timedOut ? null : process.ExitCode, timedOut);
    }
}
