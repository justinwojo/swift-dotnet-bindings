// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.
//
// Build.RuntimeTests.cs — simulator/device/macOS test execution
//
// DESIGN DECISION: Skip modes vs target dependencies
//
// Problem: --skip-regen means "don't rebuild bindings, just build app + run" (~17s).
// --skip-build means "don't even rebuild the .NET app, just install + run" (~5s).
// If RuntimeTestsSimulator unconditionally DependsOn(BindingTests), Nuke runs
// the full pipeline before the target body even executes — the skip flags can't work.
//
// Solution: The runtime test targets do NOT depend on BindingTests. Instead:
//   - Default behavior (no skip flags): the target body calls the binding pipeline
//     methods directly, then builds the app, then runs tests.
//   - --skip-regen: skips binding pipeline, just builds app + runs tests.
//   - --skip-build: skips everything, just installs + runs.
//   - Staleness detection: if --skip-regen but Swift sources are newer than bindings,
//     refuse to run (prevents confusing stale-binding failures).
//
// This matches run-runtime-tests.sh which is a self-contained script that
// conditionally calls build-and-test.sh, not a dependency chain.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Serilog;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    [Parameter("Skip all builds, just install + run")] readonly bool SkipBuild;
    [Parameter("Pre-booted simulator or device UDID")] readonly string? DeviceUdid;

    // --skip-build implies --skip-regen (matches run-runtime-tests.sh line 56-59).
    bool EffectiveSkipRegen => SkipRegen || SkipBuild;

    // macOS uses a separate output directory for its bindings
    AbsolutePath BtMacOSOutputDir => BindingTestsDir / "output-macos";

    const string RuntimeTestsBundleId = "com.swiftbindings.runtimetestsapp";

    // ============================================================
    // RuntimeTestsSimulator — NO DependsOn, manages pipeline internally
    // ============================================================

    Target RuntimeTestsSimulator => _ => _
        .After(Clean, BindingTestsStrict)
        .Executes(() =>
        {
            Log.Information("=========================================");
            Log.Information(" BindingTests Runtime Tests (Simulator)");
            Log.Information("=========================================");
            Log.Information("Skip regeneration: {SkipRegen}", EffectiveSkipRegen);
            Log.Information("Skip build: {SkipBuild}", SkipBuild);
            Log.Information("Timeout: {Timeout}s", Timeout);
            if (!string.IsNullOrEmpty(ClassFilter))
                Log.Information("Class filter: {ClassFilter}", ClassFilter);
            if (FlakeDetect)
                Log.Information("Flake detection: enabled");

            // Step 1: Conditionally run binding pipeline
            if (!EffectiveSkipRegen)
            {
                RunBuildXcframework();
                RunRegenerateBindings(strict: false);
                RunCompileCheck();
                RunBuildAsyncWrapper();
                RunBuildBridge();
            }
            else
            {
                AssertBindingsNotStale();
            }

            // Step 2: Build RuntimeTestsApp (unless --skip-build)
            if (!SkipBuild)
            {
                Log.Information("--- Building RuntimeTestsApp ---");
                DotNetBuild(s => s
                    .SetProjectFile(BindingTestsDir / "RuntimeTestsApp")
                    .SetConfiguration("Debug")
                    .SetVerbosity(DotNetVerbosity.quiet));

                var appFrameworks = BindingTestsDir / "RuntimeTestsApp" / "bin" / "Debug" /
                    $"{DotNetTfm}-ios" / "iossimulator-arm64" / "RuntimeTestsApp.app" / "Frameworks";

                if (!Directory.Exists(BindingTestsDir / "RuntimeTestsApp" / "bin" / "Debug" /
                    $"{DotNetTfm}-ios" / "iossimulator-arm64" / "RuntimeTestsApp.app"))
                    throw new Exception("Build failed - app bundle not found");

                Log.Information("Build successful.");

                // Inject all 4 native artifacts into app bundle Frameworks/
                InjectRuntimeDylib(appFrameworks);
                InjectAsyncWrapper(appFrameworks);
                InjectDependencyFramework(appFrameworks);
                InjectDependencyWrapper(appFrameworks);
            }
            else
            {
                Log.Information("--- Steps 1-2: Skipped (--skip-build) ---");
            }

            // Step 3: Install + run on simulator
            RunOnSimulator();
        });

    // ============================================================
    // RuntimeTestsDevice — NO DependsOn, manages pipeline internally
    // Device path has its OWN wrapper build step, separate from simulator.
    // ============================================================

    Target RuntimeTestsDevice => _ => _
        .After(Clean, RuntimeTestsSimulator)
        .Executes(() =>
        {
            Log.Information("=========================================");
            Log.Information(" BindingTests Runtime Tests (Device)");
            Log.Information("=========================================");

            // Step 0: Find connected device
            PhysicalDeviceInfo device;
            if (!string.IsNullOrEmpty(DeviceUdid))
            {
                device = new PhysicalDeviceInfo(DeviceUdid, "specified");
                Log.Information("Using specified device: {Udid}", DeviceUdid);
            }
            else
            {
                var found = DeviceCtl.ListDevices().FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "No connected iOS device found. Connect your iPhone and try again, or use --device-udid UDID.");
                device = new PhysicalDeviceInfo(found.Udid, found.Name);
                Log.Information("Device: {Name} ({Udid})", device.Name, device.Udid);
            }

            if (!EffectiveSkipRegen)
            {
                // Device path: build xcframework with device slice
                RunBuildXcframework(includeDeviceOverride: true);
                RunRegenerateBindings(strict: false);
                // Build device-specific wrappers
                RunBuildDeviceWrappers();
                RunBuildBridge(target: "device");
            }
            else
            {
                AssertBindingsNotStale();
                AssertDeviceSliceExists();
            }

            if (!SkipBuild)
            {
                // Publish NativeAOT (takes several minutes)
                // Uses the unified RuntimeTestsApp project with -r ios-arm64 (activates device conditionals)
                Log.Information("--- Publishing RuntimeTestsApp (NativeAOT, ios-arm64) ---");
                Log.Information("This may take several minutes (ILCompiler + code signing)...");
                DotNetPublish(s => s
                    .SetProject(BindingTestsDir / "RuntimeTestsApp")
                    .SetConfiguration("Release")
                    .SetRuntime("ios-arm64")
                    .SetVerbosity(DotNetVerbosity.quiet));
            }

            // Locate app bundle
            var appSearchDir = BindingTestsDir / "RuntimeTestsApp" / "bin";
            var appPath = Directory.GetDirectories(appSearchDir, "RuntimeTestsApp.app",
                    SearchOption.AllDirectories)
                .Where(d => d.Contains("Release") && d.Contains("ios-arm64"))
                .FirstOrDefault()
                ?? throw new Exception("App bundle not found after publish");
            Log.Information("App bundle: {Path}", appPath);

            // Install + run on device
            RunOnDevice(device, appPath);
        });

    // Simple record to avoid depending on DeviceCtl.PhysicalDevice in the target body
    record PhysicalDeviceInfo(string Udid, string Name);

    // ============================================================
    // RuntimeTestsMacOS — NO DependsOn
    // macOS has its own xcframework build and generates macOS-specific bindings.
    // ============================================================

    Target RuntimeTestsMacOS => _ => _
        .After(Clean, RuntimeTestsDevice, BindingTestsStrict)
        .Executes(() =>
        {
            Log.Information("=========================================");
            Log.Information(" BindingTests Runtime Tests (macOS)");
            Log.Information("=========================================");

            if (!EffectiveSkipRegen)
            {
                // Build xcframework for macOS
                RunBuildXcframework(platformOverride: ApplePlatform.MacOS);
                // Generate macOS-specific bindings
                RunRegenerateMacOSBindings();
                // Build async wrappers for macOS
                RunBuildAsyncWrapper(platformOverride: ApplePlatform.MacOS, outputDirOverride: BtMacOSOutputDir);
            }
            else
            {
                AssertBindingsNotStale(BtMacOSOutputDir);
            }

            if (!SkipBuild)
            {
                Log.Information("--- Building RuntimeTestsApp.Mac ---");
                DotNetBuild(s => s
                    .SetProjectFile(BindingTestsDir / "RuntimeTestsApp.Mac")
                    .SetConfiguration("Debug")
                    .SetVerbosity(DotNetVerbosity.quiet));

                var outputBin = BindingTestsDir / "RuntimeTestsApp.Mac" / "bin" / "Debug" /
                    DotNetTfm / "osx-arm64";
                if (!File.Exists(outputBin / "RuntimeTestsApp.Mac"))
                    throw new Exception("Build failed - macOS executable not found");

                Log.Information("Build successful.");

                InjectMacOSNativeLibraries(outputBin);
            }

            // Run natively on macOS (no simulator/device)
            RunOnMacOS();
        });

    // ============================================================
    // Shared Helpers: Simulator Execution
    // ============================================================

    void RunOnSimulator()
    {
        Log.Information("--- Running on iOS Simulator ---");

        var device = !string.IsNullOrEmpty(DeviceUdid)
            ? new SimCtl.SimDevice(DeviceUdid, "pre-booted", "Booted", true, "")
            : SimCtl.EnsureBootedDevice();
        Log.Information("Using simulator: {Name} ({Udid})", device.Name, device.Udid);

        var appPath = BindingTestsDir / "RuntimeTestsApp" / "bin" / "Debug" /
            $"{DotNetTfm}-ios" / "iossimulator-arm64" / "RuntimeTestsApp.app";

        // Load test inventory for crash recovery
        var inventoryPath = BindingTestsDir / "RuntimeTestsApp" / "TestClasses.g.txt";
        var inventory = TestClassInventory.Load(inventoryPath);

        // Compute eligible class set: full inventory or just the filtered class
        var eligibleClasses = inventory.ClassNames.ToHashSet();
        if (!string.IsNullOrEmpty(ClassFilter))
        {
            var match = eligibleClasses.FirstOrDefault(c =>
                c.Equals(ClassFilter, StringComparison.OrdinalIgnoreCase));
            eligibleClasses = match != null ? new HashSet<string> { match } : new HashSet<string>();
        }

        // Resume-on-crash orchestration loop
        const int maxRetries = 5;
        var excludeClasses = new HashSet<string>();
        var aggregated = new JsonlTestResults();
        LaunchResult? lastResult = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
                Log.Information("--- Resume-on-crash: attempt {Attempt}/{MaxRetries} (excluding {Count} classes) ---",
                    attempt + 1, maxRetries + 1, excludeClasses.Count);

            var crashLogsBefore = SimCtl.CountCrashLogs("RuntimeTestsApp");

            SimCtl.Install(device.Udid, appPath);

            var args = new List<string> { "--platform", "simulator" };
            if (FlakeDetect) args.AddRange(["--flake-detect"]);
            if (!string.IsNullOrEmpty(ClassFilter)) args.AddRange(["--class", ClassFilter]);
            if (excludeClasses.Count > 0)
                args.AddRange(["--exclude-classes", string.Join(",", excludeClasses)]);

            Log.Information("Launching app (timeout: {Timeout}s)...", Timeout);
            var result = SimCtl.Launch(
                device.Udid, RuntimeTestsBundleId,
                args.ToArray(), TimeSpan.FromSeconds(Timeout));
            lastResult = result;

            // Show output
            Log.Information("");
            Log.Information("=== APP OUTPUT ===");
            Log.Information(result.Output);

            // Crash diagnostics
            HandleCrashDiagnostics(result, device.Udid, crashLogsBefore);

            // Try to retrieve JSONL results from sandbox
            JsonlTestResults? runResults = null;
            var jsonlContent = SimCtl.CopyResultsFromSandbox(device.Udid, RuntimeTestsBundleId);
            if (jsonlContent != null)
            {
                runResults = JsonlTestResults.Parse(jsonlContent);
                Log.Information("JSONL results (run {Run}): {Summary}", attempt + 1, runResults.ToString());

                // Save this run's JSONL to host-side temp file
                var tempPath = $"/tmp/runtime-tests-run-{attempt}.jsonl";
                File.WriteAllText(tempPath, jsonlContent);
            }
            else
            {
                Log.Debug("JSONL retrieval failed for run {Run}", attempt + 1);
            }

            // If app completed normally (success or failure), we're done
            if (result.Result is TestResult.Success or TestResult.Failure)
            {
                if (runResults != null) aggregated.Merge(runResults);
                break;
            }

            // Crash/timeout: attempt recovery
            if (result.Result is TestResult.Crash or TestResult.Timeout or TestResult.LaunchFailure)
            {
                if (runResults == null || runResults.Tests.Count == 0)
                {
                    // JSONL recovery failed — fall back to console output parsing
                    var consoleClasses = JsonlTestResults.ParseClassesFromConsole(result.Output);
                    if (consoleClasses.Count > 0)
                    {
                        Log.Warning("JSONL recovery failed — falling back to console output ({Count} classes found).", consoleClasses.Count);
                        foreach (var cls in consoleClasses)
                            excludeClasses.Add(cls);

                        var remainingAfterConsole = eligibleClasses.Except(excludeClasses).ToList();
                        if (remainingAfterConsole.Count == 0)
                        {
                            Log.Information("All classes either completed or crashed — no more to run.");
                            break;
                        }

                        Log.Information("Remaining classes: {Count}", remainingAfterConsole.Count);

                        if (attempt == maxRetries)
                        {
                            Log.Error("Max retries ({Max}) reached.", maxRetries);
                            break;
                        }

                        continue;
                    }

                    // Neither JSONL nor console output available. Blind-skip the first
                    // remaining class to make progress through the crash-recovery loop.
                    var remainingBlind = eligibleClasses.Except(excludeClasses).OrderBy(c => c).ToList();
                    if (remainingBlind.Count > 0 && attempt < maxRetries)
                    {
                        var suspect = remainingBlind[0];
                        Log.Warning("Blind skip: excluding '{Class}' (first remaining — no output to identify crasher).", suspect);
                        excludeClasses.Add(suspect);
                        continue;
                    }

                    Log.Error("No JSONL results recovered from crashed run — cannot resume.");
                    break;
                }

                // Identify completed and crashing classes
                var crashingClass = runResults.FindCrashingClass();

                // Synthesize CRASHED entries for unfinished methods
                if (crashingClass != null)
                {
                    Log.Warning("Crash detected in class: {Class}", crashingClass);
                    runResults.SynthesizeCrashEntries(crashingClass, inventory);
                    excludeClasses.Add(crashingClass);
                }

                // Add all completed classes to exclude list
                foreach (var cls in runResults.CompletedClasses)
                    excludeClasses.Add(cls);

                aggregated.Merge(runResults);

                // Check if there are remaining classes to run (scoped to eligible set)
                var remaining = eligibleClasses.Except(excludeClasses).ToList();
                if (remaining.Count == 0)
                {
                    Log.Information("All classes either completed or crashed — no more to run.");
                    break;
                }

                Log.Information("Remaining classes: {Count} (completed: {Completed}, crashed: {Crashed})",
                    remaining.Count, runResults.CompletedClasses.Count,
                    crashingClass != null ? 1 : 0);

                if (attempt == maxRetries)
                {
                    Log.Error("Max retries ({Max}) reached. {Remaining} classes not executed.",
                        maxRetries, remaining.Count);
                    break;
                }

                continue;
            }

            // Unknown result — don't retry
            break;
        }

        // Report final aggregated result
        var finalJsonl = aggregated.Tests.Count > 0 ? aggregated : null;
        ReportRuntimeTestResult(lastResult!, "Simulator", finalJsonl);
    }

    // ============================================================
    // Shared Helpers: Device Execution
    // ============================================================

    void RunOnDevice(PhysicalDeviceInfo device, string appPath)
    {
        Log.Information("--- Running on physical device ---");

        // Load test inventory for crash recovery (unified project — same manifest for sim + device)
        var inventoryPath = BindingTestsDir / "RuntimeTestsApp" / "TestClasses.g.txt";
        var inventory = TestClassInventory.Load(inventoryPath);

        // Compute eligible class set: full inventory or just the filtered class
        var eligibleClasses = inventory.ClassNames.ToHashSet();
        if (!string.IsNullOrEmpty(ClassFilter))
        {
            var match = eligibleClasses.FirstOrDefault(c =>
                c.Equals(ClassFilter, StringComparison.OrdinalIgnoreCase));
            eligibleClasses = match != null ? new HashSet<string> { match } : new HashSet<string>();
        }

        // Resume-on-crash orchestration loop
        const int maxRetries = 5;
        var excludeClasses = new HashSet<string>();
        var aggregated = new JsonlTestResults();
        LaunchResult? lastResult = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            if (attempt > 0)
                Log.Information("--- Resume-on-crash (device): attempt {Attempt}/{MaxRetries} (excluding {Count} classes) ---",
                    attempt + 1, maxRetries + 1, excludeClasses.Count);

            DeviceCtl.Install(device.Udid, appPath);

            var args = new List<string> { "--platform", "device" };
            if (FlakeDetect) args.AddRange(["--flake-detect"]);
            if (!string.IsNullOrEmpty(ClassFilter)) args.AddRange(["--class", ClassFilter]);
            if (excludeClasses.Count > 0)
                args.AddRange(["--exclude-classes", string.Join(",", excludeClasses)]);

            Log.Information("Launching app on device (timeout: {Timeout}s)...", Timeout);
            var result = DeviceCtl.Launch(
                device.Udid, RuntimeTestsBundleId,
                args.ToArray(), TimeSpan.FromSeconds(Timeout));
            lastResult = result;

            Log.Information("");
            Log.Information("=== APP OUTPUT ===");
            Log.Information(result.Output);

            // Try to retrieve JSONL results from device sandbox
            JsonlTestResults? runResults = null;
            var jsonlContent = DeviceCtl.CopyResultsFromSandbox(device.Udid, RuntimeTestsBundleId);
            if (jsonlContent != null)
            {
                runResults = JsonlTestResults.Parse(jsonlContent);
                Log.Information("JSONL results (run {Run}): {Summary}", attempt + 1, runResults.ToString());

                var tempPath = $"/tmp/runtime-tests-device-run-{attempt}.jsonl";
                File.WriteAllText(tempPath, jsonlContent);
            }
            else
            {
                Log.Debug("JSONL retrieval from device failed for run {Run}", attempt + 1);
            }

            // If app completed normally, we're done
            if (result.Result is TestResult.Success or TestResult.Failure)
            {
                if (runResults != null) aggregated.Merge(runResults);
                break;
            }

            // Crash/timeout: attempt recovery
            if (result.Result is TestResult.Crash or TestResult.Timeout or TestResult.LaunchFailure)
            {
                if (runResults == null || runResults.Tests.Count == 0)
                {
                    // JSONL recovery failed — fall back to console output parsing.
                    // Extract class names from [PASS]/[FAIL]/[SKIP] lines to identify
                    // completed classes and the class that was running when the app crashed.
                    var consoleClasses = JsonlTestResults.ParseClassesFromConsole(result.Output);
                    if (consoleClasses.Count > 0)
                    {
                        Log.Warning("JSONL recovery failed — falling back to console output ({Count} classes found).", consoleClasses.Count);
                        foreach (var cls in consoleClasses)
                            excludeClasses.Add(cls);

                        var remainingAfterConsole = eligibleClasses.Except(excludeClasses).ToList();
                        if (remainingAfterConsole.Count == 0)
                        {
                            Log.Information("All classes either completed or crashed — no more to run.");
                            break;
                        }

                        Log.Information("Remaining classes: {Count}", remainingAfterConsole.Count);

                        if (attempt == maxRetries)
                        {
                            Log.Error("Max retries ({Max}) reached on device.", maxRetries);
                            break;
                        }

                        continue;
                    }

                    // Neither JSONL nor console gave us classes. The app likely crashed at
                    // startup before running any tests. Skip the first remaining class
                    // (alphabetically) to make progress — the crash-recovery loop will
                    // keep narrowing down until the crasher is isolated.
                    var remainingBlind = eligibleClasses.Except(excludeClasses).OrderBy(c => c).ToList();
                    if (remainingBlind.Count > 0 && attempt < maxRetries)
                    {
                        var suspect = remainingBlind[0];
                        Log.Warning("Blind skip: excluding '{Class}' (first remaining — no output to identify crasher).", suspect);
                        excludeClasses.Add(suspect);
                        continue;
                    }

                    Log.Error("No JSONL results recovered from crashed device run — cannot resume.");
                    break;
                }

                var crashingClass = runResults.FindCrashingClass();

                if (crashingClass != null)
                {
                    Log.Warning("Crash detected in class: {Class}", crashingClass);
                    runResults.SynthesizeCrashEntries(crashingClass, inventory);
                    excludeClasses.Add(crashingClass);
                }

                foreach (var cls in runResults.CompletedClasses)
                    excludeClasses.Add(cls);

                aggregated.Merge(runResults);

                // Check if there are remaining classes to run (scoped to eligible set)
                var remaining = eligibleClasses.Except(excludeClasses).ToList();
                if (remaining.Count == 0)
                {
                    Log.Information("All classes either completed or crashed — no more to run.");
                    break;
                }

                Log.Information("Remaining classes: {Count}", remaining.Count);

                if (attempt == maxRetries)
                {
                    Log.Error("Max retries ({Max}) reached on device.", maxRetries);
                    break;
                }

                continue;
            }

            break;
        }

        var finalJsonl = aggregated.Tests.Count > 0 ? aggregated : null;
        ReportRuntimeTestResult(lastResult!, "Device/NativeAOT", finalJsonl);
    }

    // ============================================================
    // Shared Helpers: macOS Execution
    // ============================================================

    void RunOnMacOS()
    {
        Log.Information("--- Running on macOS ---");

        // macOS uses --platform simulator (Mono JIT mode, same as simulator)
        var launchArgs = "--platform simulator";
        if (FlakeDetect) launchArgs += " --flake-detect";
        if (!string.IsNullOrEmpty(ClassFilter)) launchArgs += $" --class {ClassFilter}";

        Log.Information("Launching RuntimeTestsApp.Mac (timeout: {Timeout}s)...", Timeout);

        var output = new ConcurrentQueue<string>();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{BindingTestsDir / "RuntimeTestsApp.Mac"}\" --no-build -c Debug -- {launchArgs}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        var testResult = TestResult.Timeout;
        bool resultsFlushed = false;

        while (sw.Elapsed < TimeSpan.FromSeconds(Timeout))
        {
            if (process.HasExited)
            {
                Thread.Sleep(100);
                var text = string.Join("\n", output);
                resultsFlushed = text.Contains("RESULTS FLUSHED");
                if (text.Contains("TEST SUCCESS")) testResult = TestResult.Success;
                else if (text.Contains("TEST FAILURE")) testResult = TestResult.Failure;
                else testResult = TestResult.LaunchFailure;
                break;
            }

            var currentText = string.Join("\n", output);
            if (currentText.Contains("RESULTS FLUSHED"))
            {
                resultsFlushed = true;
                if (currentText.Contains("TEST SUCCESS")) { testResult = TestResult.Success; break; }
                if (currentText.Contains("TEST FAILURE")) { testResult = TestResult.Failure; break; }
            }

            Thread.Sleep(250);
        }

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }

        var finalOutput = string.Join("\n", output);
        int? exitCode = null;
        try { if (process.HasExited) exitCode = process.ExitCode; } catch { }

        var result = new LaunchResult(testResult, finalOutput, exitCode, null, resultsFlushed);

        Log.Information("");
        Log.Information("=== APP OUTPUT ===");
        Log.Information(result.Output);

        // macOS: try to read JSONL from working directory (dotnet run uses repo root as cwd)
        JsonlTestResults? jsonlResults = null;
        var macJsonlPath = RootDirectory / "test-results.jsonl";
        if (File.Exists(macJsonlPath))
        {
            jsonlResults = JsonlTestResults.ParseFile(macJsonlPath);
            Log.Information("JSONL results: {Summary}", jsonlResults.ToString());
        }

        ReportRuntimeTestResult(result, "macOS", jsonlResults);
    }

    // ============================================================
    // Crash Diagnostics
    // ============================================================

    void HandleCrashDiagnostics(LaunchResult result, string simulatorUdid, int crashLogsBefore)
    {
        if (result.Result is not (TestResult.Crash or TestResult.LaunchFailure or TestResult.Timeout))
            return;

        // Check crash log count delta
        var crashLogsAfter = SimCtl.CountCrashLogs("RuntimeTestsApp");
        if (crashLogsAfter > crashLogsBefore)
        {
            var crashLog = SimCtl.FindLatestCrashLog("RuntimeTestsApp");
            if (crashLog != null)
            {
                Log.Error("Crash log detected: {Path}", crashLog);
                try
                {
                    var crashContent = File.ReadAllLines(crashLog).Take(30);
                    foreach (var line in crashContent)
                        Log.Error("  {Line}", line);
                }
                catch { }
            }
        }

        // Read device log for crash evidence
        var deviceLog = SimCtl.ReadLog(simulatorUdid, TimeSpan.FromMinutes(3), "RuntimeTestsApp");
        if (!string.IsNullOrEmpty(deviceLog))
        {
            var isMonoJitCrash = SimCtl.IsMonoJitCrash(deviceLog) ||
                SimCtl.IsMonoJitCrash(result.Output);

            if (isMonoJitCrash)
            {
                Log.Error("");
                Log.Error("=== DEVICE LOG (crash evidence) ===");
                var crashLines = deviceLog.Split('\n')
                    .Where(l => l.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("assert", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("abort", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("exc_bad", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("jit-info", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("ReleaseHandle") ||
                                l.Contains("SIGABRT") ||
                                l.Contains("fatal", StringComparison.OrdinalIgnoreCase))
                    .TakeLast(10);
                foreach (var line in crashLines)
                    Log.Error("  {Line}", line);

                var passCount = result.Output.Split('\n')
                    .Count(l => l.Contains("[PASS]"));
                Log.Error("");
                Log.Error("Mono JIT crash detected on simulator ({PassCount} tests passed before crash).", passCount);
                Log.Error("This crash is a regression — diagnose the root cause (see CLAUDE.md).");
            }
            else if (deviceLog.Contains("EXC_BAD_ACCESS") || deviceLog.Contains("SIGABRT"))
            {
                Log.Warning("");
                Log.Warning("=== DEVICE LOG (last 3 min, RuntimeTestsApp) ===");
                var logLines = deviceLog.Split('\n').TakeLast(30);
                foreach (var line in logLines)
                    Log.Warning("  {Line}", line);
            }
        }

        // Extract partial results from output before crash
        var failCount = result.Output.Split('\n')
            .Count(l => l.Contains("[FAIL]") && l.Contains("ms)"));
        var passCountFinal = result.Output.Split('\n')
            .Count(l => l.Contains("[PASS]"));

        if (failCount > 0)
        {
            Log.Error("{FailCount} test(s) failed before crash ({PassCount} passed).", failCount, passCountFinal);
            var failingTests = result.Output.Split('\n')
                .Where(l => l.Contains("[FAIL]") && l.Contains("ms)"));
            foreach (var test in failingTests)
                Log.Error("  {Test}", test.Trim());
        }
    }

    // ============================================================
    // Result Reporting
    // ============================================================

    void ReportRuntimeTestResult(LaunchResult result, string platform, JsonlTestResults? jsonlResults = null)
    {
        // Log JSONL-derived counts if available
        if (jsonlResults != null)
        {
            Log.Information("");
            Log.Information("=== JSONL TEST COUNTS ({Platform}) ===", platform);
            Log.Information("  Pass:  {Pass}", jsonlResults.PassCount);
            Log.Information("  Fail:  {Fail}", jsonlResults.FailCount);
            Log.Information("  Skip:  {Skip}", jsonlResults.SkipCount);
            Log.Information("  Crash: {Crash}", jsonlResults.CrashCount);
            Log.Information("  Done:  {Done}", jsonlResults.Done);

            // Report crashed classes explicitly
            var crashedClasses = jsonlResults.CrashedClasses;
            if (crashedClasses.Count > 0)
            {
                Log.Warning("  Crashed classes ({Count}):", crashedClasses.Count);
                foreach (var cls in crashedClasses)
                {
                    var crashCount = jsonlResults.Tests.Count(t => t.ClassName == cls && t.Status == "crash");
                    Log.Warning("    - {Class} ({Count} methods crashed)", cls, crashCount);
                }
            }
        }

        // If we have aggregated results from crash recovery, adjust the final verdict.
        // A crash that was recovered (all remaining classes ran) is reported as Success/Failure
        // based on actual test results, not the crash status of the last launch.
        var effectiveResult = result.Result;
        if (jsonlResults != null && jsonlResults.CrashCount > 0 &&
            result.Result is TestResult.Crash or TestResult.Timeout or TestResult.LaunchFailure)
        {
            // Crash recovery completed — report based on aggregated fail count
            if (jsonlResults.FailCount > 0)
                effectiveResult = TestResult.Failure;
            else
                effectiveResult = TestResult.Success;
            Log.Information("Crash recovery completed — reporting based on aggregated results.");
        }

        Log.Information("");
        Log.Information("=========================================");
        switch (effectiveResult)
        {
            case TestResult.Success:
                Log.Information(" RUNTIME TESTS PASSED ({Platform})", platform);
                Log.Information("=========================================");
                if (jsonlResults != null)
                    CompareRuntimeBaseline(platform, jsonlResults);
                break;

            case TestResult.Failure:
                Log.Information(" RUNTIME TESTS FAILED ({Platform})", platform);
                Log.Information("=========================================");
                if (jsonlResults != null)
                    CompareRuntimeBaseline(platform, jsonlResults);
                throw new Exception($"Runtime tests failed ({platform})");

            case TestResult.Crash:
                Log.Information(" RUNTIME TESTS CRASHED ({Platform})", platform);
                Log.Information("=========================================");
                throw new Exception($"Runtime tests crashed ({platform})");

            case TestResult.LaunchFailure:
                Log.Information(" RUNTIME TESTS LAUNCH FAILURE ({Platform})", platform);
                Log.Information("=========================================");
                throw new Exception($"Runtime tests launch failure ({platform})");

            case TestResult.Timeout:
                Log.Information(" RUNTIME TESTS TIMEOUT ({Platform})", platform);
                Log.Information("=========================================");
                throw new Exception($"Runtime tests timed out ({platform})");
        }
    }

    /// <summary>
    /// Compares JSONL results against the runtime tests baseline.
    /// Fails if pass count drops, warns + auto-updates if pass count increases.
    /// </summary>
    void CompareRuntimeBaseline(string platform, JsonlTestResults jsonlResults)
    {
        var baseline = ValidationBaseline.Load(BaselinePath);
        var runtimeBaseline = baseline.RuntimeTests;

        // Determine which platform baseline to compare
        var platformKey = platform.ToLowerInvariant() switch
        {
            "simulator" => "simulator",
            "device/nativeaot" or "device" => "device",
            _ => null // macOS — no baseline yet
        };

        if (platformKey == null || runtimeBaseline == null)
        {
            Log.Information("No runtime test baseline for {Platform} — skipping comparison", platform);
            return;
        }

        var baselineCounts = platformKey == "simulator" ? runtimeBaseline.Simulator : runtimeBaseline.Device;
        if (baselineCounts == null)
        {
            Log.Information("No runtime test baseline for {Platform} — skipping comparison", platform);
            return;
        }

        var currentPass = jsonlResults.PassCount;
        var baselinePass = baselineCounts.Pass;

        Log.Information("");
        Log.Information("=== RUNTIME BASELINE COMPARISON ({Platform}) ===", platform);
        Log.Information("  Baseline pass: {Baseline}", baselinePass);
        Log.Information("  Current pass:  {Current}", currentPass);

        if (currentPass < baselinePass)
        {
            var delta = baselinePass - currentPass;
            Log.Error("REGRESSION: {Platform} pass count dropped by {Delta} (baseline={Baseline}, current={Current})",
                platform, delta, baselinePass, currentPass);
            throw new Exception(
                $"Runtime test regression on {platform}: pass count dropped from {baselinePass} to {currentPass} (-{delta})");
        }

        if (currentPass > baselinePass)
        {
            var delta = currentPass - baselinePass;
            Log.Warning("IMPROVEMENT: {Platform} pass count increased by {Delta} (baseline={Baseline}, current={Current})",
                platform, delta, baselinePass, currentPass);

            // Auto-update baseline on unfiltered, no-crash successful runs
            if (string.IsNullOrEmpty(ClassFilter) && jsonlResults.CrashCount == 0)
            {
                var newCounts = new ValidationBaseline.RuntimeTestsPlatformCounts
                {
                    Pass = currentPass,
                    Fail = jsonlResults.FailCount,
                    Skip = jsonlResults.SkipCount,
                    Crash = 0
                };

                var newRuntimeBaseline = platformKey == "simulator"
                    ? runtimeBaseline with { Simulator = newCounts }
                    : runtimeBaseline with { Device = newCounts };

                var newBaseline = baseline with { RuntimeTests = newRuntimeBaseline };
                newBaseline.Save(BaselinePath);
                Log.Information("Baseline auto-updated for {Platform}: pass={Pass}", platform, currentPass);
            }
        }
        else
        {
            Log.Information("Baseline matches: {Platform} pass count = {Pass}", platform, currentPass);
        }
    }


    // ============================================================
    // Staleness Detection
    // ============================================================

    void AssertBindingsNotStale(AbsolutePath? outputDirOverride = null)
    {
        var outputDir = outputDirOverride ?? BtOutputDir;
        var bindingsFile = outputDir / $"{ModuleName}.cs";

        if (!File.Exists(bindingsFile))
            throw new InvalidOperationException(
                $"Bindings not found at {bindingsFile}. Run without --skip-regen first.");

        var bindingsTime = File.GetLastWriteTimeUtc(bindingsFile);
        var swiftSourceDir = BindingTestsDir / "Sources" / "SwiftBindingsTestLib";

        if (!Directory.Exists(swiftSourceDir)) return;

        var newerSource = Directory.GetFiles(swiftSourceDir, "*.swift", SearchOption.AllDirectories)
            .FirstOrDefault(f => File.GetLastWriteTimeUtc(f) > bindingsTime);

        if (newerSource != null)
            throw new InvalidOperationException(
                $"Bindings are stale. Swift source newer than bindings: {newerSource}. " +
                "Run without --skip-regen to regenerate.");

        Log.Information("Staleness check passed: bindings are up to date.");
    }

    void AssertDeviceSliceExists()
    {
        var deviceSliceDir = BtXcframeworkDir / "ios-arm64";
        if (!Directory.Exists(deviceSliceDir))
            throw new InvalidOperationException(
                "Device slice missing from SwiftBindingsTestLib.xcframework. " +
                "Run without --skip-regen first.");
    }

    // ============================================================
    // Native Artifact Injection (Simulator)
    // ============================================================

    /// <summary>
    /// Injects libSwiftBindingsRuntime.dylib into the app bundle Frameworks/ directory.
    /// </summary>
    void InjectRuntimeDylib(AbsolutePath appFrameworks)
    {
        var runtimeDylib = RootDirectory / "src" / "Swift.Runtime" / "native" / "iossimulator" /
            "libSwiftBindingsRuntime.dylib";

        appFrameworks.CreateDirectory();
        if (File.Exists(runtimeDylib))
        {
            File.Copy(runtimeDylib, appFrameworks / "libSwiftBindingsRuntime.dylib", overwrite: true);
            Log.Information("Injected libSwiftBindingsRuntime.dylib into app bundle.");
        }
        else
        {
            Log.Warning("libSwiftBindingsRuntime.dylib not found at {Path}", runtimeDylib);
            Log.Warning("Existential metadata tests will fail.");
        }
    }

    /// <summary>
    /// Injects the SwiftBindings async wrapper framework into the app bundle.
    /// The resolver uses @rpath/SwiftBindings.framework/SwiftBindings.
    /// </summary>
    void InjectAsyncWrapper(AbsolutePath appFrameworks)
    {
        var platform = ResolvedPlatform;
        var wrapperSlice = BtOutputDir / $"{WrapperModule}.xcframework" /
            platform.SimulatorSliceId / $"{WrapperModule}.framework" / WrapperModule;

        if (File.Exists(wrapperSlice))
        {
            var targetDir = appFrameworks / $"{WrapperModule}.framework";
            targetDir.CreateDirectory();
            File.Copy(wrapperSlice, targetDir / WrapperModule, overwrite: true);
            Log.Information("Injected {Module} wrapper dylib into app bundle.", WrapperModule);
        }
        else
        {
            Log.Information("Note: {Module} wrapper dylib not found — wrapper-dependent tests will be skipped.",
                WrapperModule);
        }
    }

    /// <summary>
    /// Injects the SwiftBindingsTestLibDependency framework into the app bundle.
    /// </summary>
    void InjectDependencyFramework(AbsolutePath appFrameworks)
    {
        var platform = ResolvedPlatform;
        var depFwDir = BtDepXcframeworkDir / platform.SimulatorSliceId /
            $"{DepModuleName}.framework";

        if (Directory.Exists(depFwDir))
        {
            var targetDir = appFrameworks / $"{DepModuleName}.framework";
            targetDir.CreateDirectory();
            File.Copy(depFwDir / DepModuleName, targetDir / DepModuleName, overwrite: true);

            // Copy or generate Info.plist
            var plistSource = depFwDir / "Info.plist";
            if (File.Exists(plistSource))
                File.Copy(plistSource, targetDir / "Info.plist", overwrite: true);
            else
                PlistGenerator.WriteFrameworkPlist(
                    targetDir / "Info.plist",
                    $"com.test.{DepModuleName}", DepModuleName, DepModuleName,
                    platform.MinOsVersion, platform.SimulatorPlistPlatform);

            Log.Information("Injected {Module} framework into app bundle.", DepModuleName);
        }
        else
        {
            Log.Information("Note: {Module} framework not found — cross-module tests may fail.", DepModuleName);
        }
    }

    /// <summary>
    /// Injects the dependency wrapper framework into the app bundle.
    /// </summary>
    void InjectDependencyWrapper(AbsolutePath appFrameworks)
    {
        var platform = ResolvedPlatform;
        var depWrapperName = $"{DepModuleName}SwiftBindings";
        var depWrapperDir = BtOutputDir / $"{depWrapperName}.xcframework" /
            platform.SimulatorSliceId / $"{depWrapperName}.framework";

        if (Directory.Exists(depWrapperDir))
        {
            var targetDir = appFrameworks / $"{depWrapperName}.framework";
            targetDir.CreateDirectory();
            File.Copy(depWrapperDir / depWrapperName, targetDir / depWrapperName, overwrite: true);

            // Copy or generate Info.plist
            var plistSource = depWrapperDir / "Info.plist";
            if (File.Exists(plistSource))
                File.Copy(plistSource, targetDir / "Info.plist", overwrite: true);
            else
                PlistGenerator.WriteFrameworkPlist(
                    targetDir / "Info.plist",
                    $"com.test.{depWrapperName}", depWrapperName, depWrapperName,
                    platform.MinOsVersion, platform.SimulatorPlistPlatform);

            Log.Information("Injected {Module} wrapper into app bundle.", depWrapperName);
        }
        else
        {
            Log.Information("Note: {Module} wrapper not found — dependency wrapper tests may fail.", depWrapperName);
        }
    }

    // ============================================================
    // Native Artifact Injection (macOS)
    // ============================================================

    /// <summary>
    /// Injects native libraries into the macOS output directory as flat dylibs.
    /// macOS doesn't use framework bundles — just copies dylibs directly.
    /// </summary>
    void InjectMacOSNativeLibraries(AbsolutePath outputBin)
    {
        var macosPlatform = ApplePlatform.MacOS;

        // 1. SwiftBindingsTestLib dylib from xcframework
        var xcfwSlice = BtXcframeworkDir / macosPlatform.SimulatorSliceId /
            $"{ModuleName}.framework" / ModuleName;
        if (File.Exists(xcfwSlice))
        {
            File.Copy(xcfwSlice, outputBin / $"lib{ModuleName}.dylib", overwrite: true);
            Log.Information("Injected {Module} dylib.", ModuleName);
        }
        else
        {
            Log.Warning("{Module} dylib not found at {Path}", ModuleName, xcfwSlice);
        }

        // 2. SwiftBindings async wrapper dylib
        var asyncSlice = BtMacOSOutputDir / $"{WrapperModule}.xcframework" /
            macosPlatform.SimulatorSliceId / $"{WrapperModule}.framework" / WrapperModule;
        if (File.Exists(asyncSlice))
        {
            File.Copy(asyncSlice, outputBin / $"lib{WrapperModule}.dylib", overwrite: true);
            Log.Information("Injected {Module} async wrapper dylib.", WrapperModule);
        }

        // 3. Runtime dylib
        var runtimeDylib = RootDirectory / "src" / "Swift.Runtime" / "native" / "macos" /
            "libSwiftBindingsRuntime.dylib";
        if (File.Exists(runtimeDylib))
        {
            File.Copy(runtimeDylib, outputBin / "libSwiftBindingsRuntime.dylib", overwrite: true);
            Log.Information("Injected libSwiftBindingsRuntime.dylib.");
        }
        else
        {
            Log.Warning("libSwiftBindingsRuntime.dylib not found at {Path}", runtimeDylib);
        }
    }

    // ============================================================
    // macOS Binding Generation
    // ============================================================

    /// <summary>
    /// Generates macOS-specific bindings. Simpler than RunRegenerateBindings:
    /// no dependency bindings, no strict mode, uses --platform macos.
    /// </summary>
    void RunRegenerateMacOSBindings()
    {
        Log.Information("=== Generating macOS bindings for {Module} ===", ModuleName);

        EnsureGeneratorBuilt();

        if (Directory.Exists(BtMacOSOutputDir))
            BtMacOSOutputDir.DeleteDirectory();
        BtMacOSOutputDir.CreateDirectory();

        var genArgs = new List<string>
        {
            $"\"{GeneratorDll}\"",
            $"--xcframework \"{BtXcframeworkDir}\"",
            "--platform macos",
            $"-o \"{BtMacOSOutputDir}\"",
        };

        var genProcess = ProcessTasks.StartProcess(
            "dotnet", string.Join(" ", genArgs),
            workingDirectory: BindingTestsDir,
            logOutput: false);
        genProcess.WaitForExit();

        if (genProcess.ExitCode != 0)
            Log.Warning("macOS binding generation exited with code {ExitCode} (non-fatal)", genProcess.ExitCode);

        var csCount = Directory.GetFiles(BtMacOSOutputDir, "*.cs", SearchOption.AllDirectories).Length;
        var swiftCount = Directory.GetFiles(BtMacOSOutputDir, "*.swift", SearchOption.AllDirectories).Length;
        Log.Information("Generated (macOS): {CsCount} C# files, {SwiftCount} Swift wrapper files", csCount, swiftCount);
    }

    // ============================================================
    // Device Wrapper Build
    // ============================================================

    /// <summary>
    /// Builds the Swift wrapper and SwiftBindingsRuntime for device (ios-arm64).
    /// Ports build-wrapper-device.sh: strips broken code, compiles for device target,
    /// creates framework structure, also builds SwiftBindingsRuntime device xcframework.
    /// </summary>
    void RunBuildDeviceWrappers()
    {
        var platform = ResolvedPlatform;
        if (!platform.HasDeviceSlice)
        {
            Log.Information("No device slice for {Platform} — skipping device wrappers.", platform.Name);
            return;
        }

        var deviceTarget = platform.DeviceTarget!;
        var deviceSdkName = platform.DeviceSdkName!;
        var deviceSliceId = platform.DeviceSliceId!;
        var devicePlistPlatform = platform.DevicePlistPlatform!;

        var xcfwSliceDir = BtXcframeworkDir / deviceSliceId;
        var depXcfwSliceDir = BtDepXcframeworkDir / deviceSliceId;

        // Verify device slice exists
        if (!Directory.Exists(xcfwSliceDir))
            throw new Exception($"Device slice missing: {xcfwSliceDir}. Run build-xcframework with --include-device.");

        Log.Information("=== Building {Module} wrapper (device) ===", WrapperModule);

        // Collect Swift wrapper files
        var swiftFiles = Directory.GetFiles(BtOutputDir, "*.swift")
            .Where(f => !f.EndsWith(".SwiftUIBridge.swift"))
            .ToList();

        if (swiftFiles.Count == 0)
        {
            Log.Information("No Swift wrapper files found — skipping device wrapper build.");
            return;
        }

        // Post-process: strip known-broken sections
        var cleanedDir = BtOutputDir / ".wrapper-build-device";
        if (Directory.Exists(cleanedDir))
            ((AbsolutePath)cleanedDir).DeleteDirectory();
        cleanedDir.CreateDirectory();

        int totalStripped = 0;
        foreach (var swiftFile in swiftFiles)
        {
            var basename = Path.GetFileName(swiftFile);
            var result = SwiftSourceStripper.StripFile(swiftFile, cleanedDir / basename);
            totalStripped += result.StrippedCount;
        }

        var cleanedFiles = Directory.GetFiles(cleanedDir, "*.swift").ToList();
        if (cleanedFiles.Count == 0)
        {
            Log.Information("No cleaned Swift files to compile for device.");
            return;
        }

        // Compile native ARM64 thunk assembly files (if any)
        var thunkObjects = new List<string>();
        foreach (var asmFile in Directory.GetFiles(BtOutputDir, "*.arm64.s"))
        {
            var objFile = Path.ChangeExtension(asmFile, ".device.o");
            XcRunTool($"clang -c {asmFile} -o {objFile} -target {deviceTarget}");
            thunkObjects.Add(objFile);
        }

        // Create device framework output
        var wrapperXcfDir = BtOutputDir / $"{WrapperModule}.xcframework";
        var outputFwDir = wrapperXcfDir / deviceSliceId / $"{WrapperModule}.framework";
        outputFwDir.CreateDirectory();

        var sdkPath = XcRun.GetSdkPath(deviceSdkName);

        // Compile with error-based retry (same pattern as RunBuildAsyncWrapper)
        const int maxRetries = 3;
        int attempt = 0;

        while (attempt < maxRetries)
        {
            attempt++;
            var allSourceFiles = cleanedFiles.Concat(thunkObjects).ToList();

            var settings = new SwiftCompilerSettings()
                .SetEmitLibrary()
                .SetTarget(deviceTarget)
                .SetSdk(sdkPath)
                .AddFrameworkSearchPath(xcfwSliceDir + "/")
                .SetModuleName(WrapperModule)
                .SetStrictConcurrency("minimal")
                .SetInstallName($"@rpath/{WrapperModule}.framework/{WrapperModule}")
                .SetOutputPath(outputFwDir / WrapperModule)
                .AddSourceFiles(allSourceFiles);

            if (Directory.Exists(depXcfwSliceDir))
                settings.AddFrameworkSearchPath(depXcfwSliceDir + "/");

            var process = SwiftCompiler.Run(settings);
            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                Log.Information("Device wrapper compilation succeeded (after {Attempt} attempt(s)).", attempt);
                break;
            }

            var compileLog = string.Join("\n", process.Output.Select(o => o.Text));

            if (attempt == maxRetries)
            {
                Log.Warning("Device wrapper compilation failed after {Retries} attempts. Continuing without.", maxRetries);
                CleanupWrapperBuild(cleanedDir);
                return;
            }

            Log.Information("Device compilation attempt {Attempt} failed — stripping broken functions...", attempt);
            var errors = string.Join("\n", compileLog.Split('\n').Where(l => l.Contains("error:")).Take(80));
            int strippedN = SwiftSourceStripper.StripErrorFunctions(cleanedDir, errors);

            if (strippedN == 0)
            {
                Log.Warning("No strippable functions found. Device build error may be structural.");
                CleanupWrapperBuild(cleanedDir);
                return;
            }

            totalStripped += strippedN;
            cleanedFiles = Directory.GetFiles(cleanedDir, "*.swift").ToList();
            Log.Information("Retrying device compilation...");
        }

        CleanupWrapperBuild(cleanedDir);

        // Create framework Info.plist
        PlistGenerator.WriteFrameworkPlist(
            outputFwDir / "Info.plist",
            $"com.swiftbindings.{WrapperModule}", WrapperModule, WrapperModule,
            platform.MinOsVersion, devicePlistPlatform);

        // Update xcframework Info.plist to include both simulator and device slices
        WriteDeviceXcframeworkPlist(wrapperXcfDir / "Info.plist", WrapperModule, platform);

        Log.Information("{Module} device wrapper framework built successfully.", WrapperModule);

        // --- Part 2: Build dependency wrapper device xcframework ---
        BuildDependencyWrapperDevice(platform, deviceTarget, deviceSdkName, deviceSliceId, devicePlistPlatform);

        // --- Part 3: Build SwiftBindingsRuntime device xcframework ---
        BuildRuntimeDeviceXcframework(platform);
    }

    /// <summary>
    /// Builds the dependency module's Swift wrapper for device (ios-arm64).
    /// Uses preserved Swift sources from RunRegenerateBindings (dep-swift/ directory).
    /// </summary>
    void BuildDependencyWrapperDevice(ApplePlatform platform, string deviceTarget, string deviceSdkName,
        string deviceSliceId, string devicePlistPlatform)
    {
        var depWrapperName = $"{DepModuleName}SwiftBindings";
        var depSwiftDir = BtOutputDir / "dep-swift";

        if (!Directory.Exists(depSwiftDir))
        {
            Log.Information("No dependency wrapper Swift sources — skipping device dependency wrapper build.");
            return;
        }

        var swiftFiles = Directory.GetFiles(depSwiftDir, "*.swift").ToList();
        if (swiftFiles.Count == 0)
        {
            Log.Information("No dependency wrapper Swift files found.");
            return;
        }

        Log.Information("=== Building {Module} (device) ===", depWrapperName);

        var xcfwSliceDir = BtXcframeworkDir / deviceSliceId;
        var depXcfwSliceDir = BtDepXcframeworkDir / deviceSliceId;
        var sdkPath = XcRun.GetSdkPath(deviceSdkName);

        var depWrapperXcf = BtOutputDir / $"{depWrapperName}.xcframework";
        var outputFwDir = depWrapperXcf / deviceSliceId / $"{depWrapperName}.framework";
        outputFwDir.CreateDirectory();

        // Strip known-broken sections (same pattern as main wrapper)
        var cleanedDir = BtOutputDir / ".dep-wrapper-build-device";
        if (Directory.Exists(cleanedDir))
            ((AbsolutePath)cleanedDir).DeleteDirectory();
        cleanedDir.CreateDirectory();

        foreach (var sf in swiftFiles)
            SwiftSourceStripper.StripFile(sf, cleanedDir / Path.GetFileName(sf));

        var cleanedFiles = Directory.GetFiles(cleanedDir, "*.swift").ToList();
        if (cleanedFiles.Count == 0)
        {
            Log.Information("No cleaned dependency wrapper Swift files to compile.");
            CleanupWrapperBuild(cleanedDir);
            return;
        }

        var settings = new SwiftCompilerSettings()
            .SetEmitLibrary()
            .SetTarget(deviceTarget)
            .SetSdk(sdkPath)
            .AddFrameworkSearchPath(depXcfwSliceDir + "/")
            .SetModuleName(depWrapperName)
            .SetStrictConcurrency("minimal")
            .SetInstallName($"@rpath/{depWrapperName}.framework/{depWrapperName}")
            .SetOutputPath(outputFwDir / depWrapperName)
            .AddSourceFiles(cleanedFiles);

        // Also need main library search path for cross-module references
        if (Directory.Exists(xcfwSliceDir))
            settings.AddFrameworkSearchPath(xcfwSliceDir + "/");

        var process = SwiftCompiler.Run(settings);
        process.WaitForExit();

        CleanupWrapperBuild(cleanedDir);

        if (process.ExitCode != 0)
        {
            Log.Warning("Dependency wrapper device compilation failed. Cross-module tests will be skipped on device.");
            return;
        }

        PlistGenerator.WriteFrameworkPlist(
            outputFwDir / "Info.plist",
            $"com.swiftbindings.{depWrapperName}", depWrapperName, depWrapperName,
            platform.MinOsVersion, devicePlistPlatform);

        WriteDeviceXcframeworkPlist(depWrapperXcf / "Info.plist", depWrapperName, platform);

        Log.Information("{Module} device wrapper built successfully.", depWrapperName);
    }

    void BuildRuntimeDeviceXcframework(ApplePlatform platform)
    {
        Log.Information("=== Building SwiftBindingsRuntime xcframework (device) ===");

        var runtimeDylib = RootDirectory / "src" / "Swift.Runtime" / "native" / "ios" /
            "libSwiftBindingsRuntime.dylib";

        if (!File.Exists(runtimeDylib))
        {
            Log.Warning("Device runtime dylib not found at {Path}. Skipping.", runtimeDylib);
            return;
        }

        var runtimeXcfw = BtBuildDir / "SwiftBindingsRuntime.xcframework";
        var runtimeFwDir = runtimeXcfw / "ios-arm64" / "SwiftBindingsRuntime.framework";
        runtimeFwDir.CreateDirectory();

        File.Copy(runtimeDylib, runtimeFwDir / "SwiftBindingsRuntime", overwrite: true);

        // Fix install_name to use @rpath
        try
        {
            XcRunTool($"install_name_tool -id @rpath/SwiftBindingsRuntime.framework/SwiftBindingsRuntime " +
                $"{runtimeFwDir / "SwiftBindingsRuntime"}");
        }
        catch (Exception ex)
        {
            Log.Warning("install_name_tool failed: {Message}", ex.Message);
        }

        // Code sign
        try
        {
            XcRunTool($"codesign --force --sign - \"{runtimeFwDir / "SwiftBindingsRuntime"}\"");
        }
        catch { /* Best-effort signing */ }

        PlistGenerator.WriteFrameworkPlist(
            runtimeFwDir / "Info.plist",
            "com.swiftbindings.SwiftBindingsRuntime", "SwiftBindingsRuntime", "SwiftBindingsRuntime",
            platform.MinOsVersion, platform.DevicePlistPlatform!);

        // Create xcframework Info.plist with device slice (preserve simulator if exists)
        WriteDeviceXcframeworkPlist(runtimeXcfw / "Info.plist", "SwiftBindingsRuntime", platform);

        Log.Information("SwiftBindingsRuntime device xcframework built successfully.");
    }

    /// <summary>
    /// Writes an xcframework Info.plist that includes both simulator and device slices
    /// when both exist. Used for device wrapper and runtime xcframeworks.
    /// </summary>
    void WriteDeviceXcframeworkPlist(string outputPath, string moduleName, ApplePlatform platform)
    {
        var xcfwDir = Path.GetDirectoryName(outputPath)!;
        var libraries = new List<string>();

        // Add simulator slice if it exists
        if (Directory.Exists(Path.Combine(xcfwDir, platform.SimulatorSliceId)))
        {
            var variantXml = platform.SimulatorPlistVariant != null
                ? $@"
            <key>SupportedPlatformVariant</key>
            <string>{platform.SimulatorPlistVariant}</string>"
                : "";

            libraries.Add($"""
                    <dict>
                        <key>LibraryIdentifier</key>
                        <string>{platform.SimulatorSliceId}</string>
                        <key>LibraryPath</key>
                        <string>{moduleName}.framework</string>
                        <key>SupportedArchitectures</key>
                        <array>
                            <string>arm64</string>
                        </array>
                        <key>SupportedPlatform</key>
                        <string>{platform.SupportedPlatform}</string>{variantXml}
                    </dict>
            """);
        }

        // Add device slice if it exists
        if (platform.HasDeviceSlice && Directory.Exists(Path.Combine(xcfwDir, platform.DeviceSliceId!)))
        {
            libraries.Add($"""
                    <dict>
                        <key>LibraryIdentifier</key>
                        <string>{platform.DeviceSliceId}</string>
                        <key>LibraryPath</key>
                        <string>{moduleName}.framework</string>
                        <key>SupportedArchitectures</key>
                        <array>
                            <string>arm64</string>
                        </array>
                        <key>SupportedPlatform</key>
                        <string>{platform.SupportedPlatform}</string>
                    </dict>
            """);
        }

        var content = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>AvailableLibraries</key>
                <array>
            {string.Join("\n", libraries)}
                </array>
                <key>CFBundlePackageType</key>
                <string>XFWK</string>
                <key>XCFrameworkFormatVersion</key>
                <string>1.0</string>
            </dict>
            </plist>
            """;
        File.WriteAllText(outputPath, content);
    }
}
