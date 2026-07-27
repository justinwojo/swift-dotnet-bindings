// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Nuke.Common.Tooling;
using Serilog;

/// <summary>
/// Manages iOS Simulator lifecycle: device discovery, boot, app install/launch,
/// and crash diagnostics. Replaces the simctl bash patterns in run-runtime-tests.sh.
/// </summary>
public static class SimCtl
{
    public record SimDevice(string Udid, string Name, string State, bool IsAvailable, string Runtime);

    /// <summary>
    /// Describes a family of Apple simulators (iOS iPhone, tvOS Apple TV, ...) for device discovery.
    /// Keeping the selection criteria in one record avoids sprawling per-family EnsureBooted overloads
    /// and makes it obvious why a given booted device was picked.
    /// </summary>
    public record SimDeviceFamily(
        string DisplayName,
        string RuntimeFilter,
        string NameContains,
        IReadOnlyList<string> PreferredDevices);

    public static readonly SimDeviceFamily IOSiPhoneFamily = new(
        DisplayName: "iPhone",
        RuntimeFilter: "iOS",
        NameContains: "iPhone",
        PreferredDevices: ["iPhone 16", "iPhone 16 Pro", "iPhone 15 Pro", "iPhone 15"]);

    public static readonly SimDeviceFamily TvOSAppleTVFamily = new(
        DisplayName: "Apple TV",
        RuntimeFilter: "tvOS",
        NameContains: "Apple TV",
        PreferredDevices: ["Apple TV 4K (3rd generation)", "Apple TV 4K", "Apple TV"]);

    static readonly string CrashLogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library/Logs/DiagnosticReports");

    // --- Device Discovery ---

    /// <summary>
    /// Lists all available simulator devices, optionally filtered by runtime keyword (e.g., "iOS").
    /// Replaces the inline Python JSON parsing in run-runtime-tests.sh.
    /// </summary>
    public static IReadOnlyList<SimDevice> ListDevices(string? runtimeFilter = null)
    {
        var json = ProcessTasks.StartProcess(
                "xcrun", "simctl list devices available -j",
                logOutput: false)
            .AssertWaitForExit()
            .Output.StdToText();

        var doc = JsonDocument.Parse(json);
        var devices = new List<SimDevice>();

        foreach (var runtime in doc.RootElement.GetProperty("devices").EnumerateObject())
        {
            if (runtimeFilter != null &&
                !runtime.Name.Contains(runtimeFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var device in runtime.Value.EnumerateArray())
            {
                devices.Add(new SimDevice(
                    Udid: device.GetProperty("udid").GetString()!,
                    Name: device.GetProperty("name").GetString()!,
                    State: device.GetProperty("state").GetString()!,
                    IsAvailable: device.GetProperty("isAvailable").GetBoolean(),
                    Runtime: runtime.Name));
            }
        }

        return devices;
    }

    /// <summary>
    /// Returns an already-booted simulator, or finds a preferred one and boots it.
    /// Defaults to the iPhone family for backwards compatibility with the iOS runner.
    /// </summary>
    public static SimDevice EnsureBootedDevice(SimDeviceFamily? family = null)
    {
        family ??= IOSiPhoneFamily;

        // Check for already-booted
        var booted = ListDevices(family.RuntimeFilter)
            .FirstOrDefault(d => d.State == "Booted"
                && d.Name.Contains(family.NameContains, StringComparison.OrdinalIgnoreCase));
        if (booted != null)
        {
            Log.Information("Using already-booted {Family} simulator: {Name} ({Udid})",
                family.DisplayName, booted.Name, booted.Udid);
            return booted;
        }

        var available = ListDevices(family.RuntimeFilter)
            .Where(d => d.IsAvailable
                && d.Name.Contains(family.NameContains, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var device = family.PreferredDevices
            .Select(pref => available.FirstOrDefault(d => d.Name == pref))
            .FirstOrDefault(d => d != null)
            ?? available.FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"No available {family.DisplayName} simulator found. Install one via Xcode.");

        Log.Information("Booting {Family} simulator: {Name} ({Udid})",
            family.DisplayName, device.Name, device.Udid);
        Boot(device.Udid);
        return device;
    }

    // --- Lifecycle ---

    public static void Boot(string udid)
    {
        ProcessTasks.StartProcess("xcrun", $"simctl boot {udid}",
                timeout: 60_000)
            .AssertWaitForExit();
        Log.Information("Waiting for simulator to finish booting...");
        ProcessTasks.StartProcess("xcrun", $"simctl bootstatus {udid} -b",
                timeout: 120_000)
            .AssertWaitForExit();
        Log.Information("Simulator booted.");
    }

    public static void Install(string udid, string appPath)
    {
        Log.Information("Installing app...");
        ProcessTasks.StartProcess("xcrun", $"simctl install {udid} \"{appPath}\"")
            .AssertWaitForExit()
            .AssertZeroExitCode();
    }

    /// <summary>
    /// Launches app on simulator, captures console output, and detects test completion or crash.
    /// Waits for RESULTS FLUSHED marker before checking TEST SUCCESS/TEST FAILURE to ensure
    /// JSONL results are fully written before the process is killed.
    /// <paramref name="appName"/> identifies which crash logs to search for on the non-happy-path
    /// (prefix-matched against <c>{appName}*.ips</c>); callers MUST pass the exact basename of
    /// the app they launched — bundleId is shared across iOS and tvOS so we can't derive it.
    /// </summary>
    public static LaunchResult Launch(string udid, string bundleId, string[] args, TimeSpan timeout, string appName)
    {
        var launchArgs = string.Join(" ", args);
        var output = new ConcurrentQueue<string>();

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "xcrun",
            Arguments = $"simctl launch --console --terminate-running-process {udid} {bundleId} {launchArgs}",
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
        var result = TestResult.Timeout;
        bool resultsFlushed = false;

        while (sw.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                // Let buffered output flush
                Thread.Sleep(100);
                var text = string.Join("\n", output);
                resultsFlushed = text.Contains("RESULTS FLUSHED");

                if (text.Contains("TEST SUCCESS"))
                    result = TestResult.Success;
                else if (text.Contains("TEST FAILURE"))
                    result = TestResult.Failure;
                else if (IsCrashOutput(text))
                    result = TestResult.Crash;
                else
                    result = TestResult.LaunchFailure;
                break;
            }

            // Check for completion markers while still running.
            // Wait for RESULTS FLUSHED before acting on TEST SUCCESS/FAILURE to ensure
            // JSONL file is fully written before we kill the process.
            // NOTE: Do NOT check for crash signals during active polling.
            // Mono's malloc assertion fires during background cleanup but the app
            // continues running and produces the test summary.
            var currentText = string.Join("\n", output);
            if (currentText.Contains("RESULTS FLUSHED"))
            {
                resultsFlushed = true;
                if (currentText.Contains("TEST SUCCESS")) { result = TestResult.Success; break; }
                if (currentText.Contains("TEST FAILURE")) { result = TestResult.Failure; break; }
            }

            Thread.Sleep(250); // 0.25s polling interval matching shell script
        }

        // Kill process if still running
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* Best-effort */ }
        }

        // Terminate the app via simctl (with its own timeout to avoid hangs)
        Terminate(udid, bundleId);

        // Drain in-flight async output before snapshotting. The poll-break path (RESULTS FLUSHED +
        // TEST SUCCESS) kills the process immediately, so buffered OutputDataReceived/ErrorDataReceived
        // callbacks may still be delivering queued lines. Without this, a late line — e.g. an ObjC
        // "Class X is implemented in both ..." duplicate-registration warning the mixed legs grep for —
        // could be dropped from finalOutput and read as a false green.
        //
        // Use the parameterless WaitForExit() rather than a fixed sleep: it is documented to block
        // until the redirected async readers reach EOF (the OutputDataReceived/ErrorDataReceived
        // callbacks have all fired), whereas the timeout overload does NOT wait for them. We bound the
        // process-exit wait first (the process was just Killed, so it terminates promptly), then the
        // parameterless call deterministically flushes the readers — no guessed interval to outrun.
        try
        {
            if (process.WaitForExit(5000))
                process.WaitForExit();
        }
        catch { /* Best-effort drain; snapshot whatever was captured. */ }

        var finalOutput = string.Join("\n", output);
        int? exitCode = null;
        try { if (process.HasExited) exitCode = process.ExitCode; } catch { }

        // Check crash logs if no clear test result
        string? crashLog = null;
        if (result is TestResult.Crash or TestResult.LaunchFailure or TestResult.Timeout)
            crashLog = FindLatestCrashLog(appName);

        return new LaunchResult(result, finalOutput, exitCode, crashLog, resultsFlushed);
    }

    /// <summary>
    /// Copies JSONL test results from the app's sandbox Documents directory on the simulator.
    /// Returns the file contents, or null if retrieval failed.
    /// </summary>
    /// <param name="expectedRunToken">
    /// The token this launch passed to the app via <c>--run-token</c>. The recovered file must carry
    /// a matching <c>run_token</c> line or it is discarded (returns null, exactly as a missing file
    /// does). `simctl install` over an existing app PRESERVES the data container, so a launch that
    /// never started the process (or a resume attempt that died before writing) would otherwise
    /// recover the previous run's results and have them scored as this run's.
    /// </param>
    public static string? CopyResultsFromSandbox(string udid, string bundleId, string expectedRunToken)
    {
        try
        {
            // Get the app's data container path
            var containerProcess = ProcessTasks.StartProcess(
                "xcrun", $"simctl get_app_container {udid} {bundleId} data",
                logOutput: false, timeout: 10000);
            containerProcess.AssertWaitForExit();
            var containerPath = containerProcess.Output.StdToText().Trim();

            if (string.IsNullOrEmpty(containerPath))
                return null;

            // The runners write to Environment.SpecialFolder.MyDocuments, which does NOT
            // resolve to <container>/Documents on every Apple platform. tvOS has no
            // persistent per-app Documents directory, so the runtime maps MyDocuments to
            // <container>/Library/Caches/Documents — the iOS-shaped probe alone misses a
            // tvOS run's artifact entirely, and the caller then (correctly) refuses to
            // certify a green run that had no results file. Probe both shapes rather than
            // branching on platform: the names are distinct, so the first hit carrying THIS
            // launch's token is unambiguous, and an iOS container is unaffected by the
            // extra check.
            string[] candidates =
            [
                Path.Combine(containerPath, "Documents", "test-results.jsonl"),
                Path.Combine(containerPath, "Library", "Caches", "Documents", "test-results.jsonl"),
            ];

            // Fail closed per candidate: no token, or a token from an earlier launch, means THAT file
            // cannot be attributed to this launch — but it says nothing about the other shape, which
            // may hold the real results. A stale Documents/ file left by an earlier iOS run must not
            // hide a live tvOS result at the Library/Caches/ path. If nothing carries a matching
            // token the probe yields null, so the caller's existing "JSONL retrieval failed" path
            // runs — an honest "no results recovered" rather than a silent false green built out of a
            // stale container artifact.
            var probe = SandboxResultProbe.Probe(
                candidates, expectedRunToken,
                path => File.Exists(path) ? File.ReadAllText(path) : null);

            // States only what a single rejected candidate proves — that THIS file is stale — because
            // a later candidate may still be accepted. The outcome of the probe is reported by the
            // accepted/none-found lines below; a per-candidate "treating it as no results recovered"
            // would read as false to an operator whose results were in fact recovered from the other
            // container shape.
            foreach (var stale in probe.Rejected)
            {
                Log.Warning(
                    "Skipping simulator JSONL at {Path}: run-token mismatch (expected {Expected}, " +
                    "file carries {Actual}). The data container survives install, so this file is left " +
                    "over from an earlier run and cannot be attributed to this launch.",
                    stale.Path, expectedRunToken, stale.ActualToken);
            }

            if (probe.Content != null)
            {
                Log.Debug("Reading JSONL from sandbox: {Path}", probe.AcceptedPath);
                return probe.Content;
            }

            // Whether nothing was found or only stale files were, the outcome is the same — this
            // launch recovered no results — but a container holding a stale file is worth saying out
            // loud, since that is the state a false green would have been built out of. That
            // statement lives here, once, where it is actually true.
            if (probe.Rejected.Count > 0)
            {
                Log.Warning(
                    "No JSONL carrying this launch's run token in the sandbox — {Stale} stale file(s) " +
                    "skipped, nothing recovered for this launch. Probed: {Paths}",
                    probe.Rejected.Count, string.Join(", ", candidates));
            }
            else
            {
                Log.Debug("No JSONL attributable to this launch in sandbox; probed: {Paths}",
                    string.Join(", ", candidates));
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to copy JSONL from sandbox: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Terminates the app with its own timeout. simctl terminate can hang on GHA runners,
    /// so we run it in a background process and kill after 5 seconds.
    /// </summary>
    public static void Terminate(string udid, string bundleId)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "xcrun",
                Arguments = $"simctl terminate {udid} {bundleId}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { /* Best-effort */ }
            }
        }
        catch { /* Best-effort termination */ }
    }

    /// <summary>
    /// Reads the simulator device log for crash diagnostics.
    /// Replaces: xcrun simctl spawn {udid} log show --last 3m --predicate '...'
    /// </summary>
    public static string ReadLog(string udid, TimeSpan interval, string processName)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(interval.TotalMinutes));
        // The eventMessage CONTAINS fallback is a substring match and would
        // otherwise let an iOS `RuntimeTestsApp` query sweep up any ReportCrash
        // line that mentions `RuntimeTestsApp.tvOS`. Exclude the tvOS sibling
        // explicitly when querying for the plain iOS app name.
        var crashMessageClause = $"eventMessage CONTAINS \"{processName}\"";
        if (!processName.Contains(".tvOS"))
            crashMessageClause += $" AND NOT eventMessage CONTAINS \"{processName}.tvOS\"";
        try
        {
            var process = ProcessTasks.StartProcess(
                "xcrun",
                $"simctl spawn {udid} log show --last {minutes}m " +
                $"--predicate 'process == \"{processName}\" OR (process == \"ReportCrash\" AND {crashMessageClause})' " +
                "--style compact",
                logOutput: false,
                timeout: 15000);
            process.WaitForExit();
            return process.Output.StdToText();
        }
        catch
        {
            return string.Empty;
        }
    }

    // --- Crash Log Helpers ---

    /// <summary>
    /// Counts existing crash logs for an app. Used for before/after delta detection.
    /// </summary>
    public static int CountCrashLogs(string appName)
    {
        if (!Directory.Exists(CrashLogDir)) return 0;
        return EnumerateCrashLogsForApp(appName).Count();
    }

    /// <summary>
    /// Returns the path to the most recent crash log for an app, or null.
    /// </summary>
    public static string? FindLatestCrashLog(string appName)
    {
        if (!Directory.Exists(CrashLogDir)) return null;
        return EnumerateCrashLogsForApp(appName)
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
    }

    // Crash report filenames follow `<appName>-<timestamp>-<host>.ips` or
    // `<appName>_<timestamp>.ips`. A plain glob of `{appName}*.ips` also
    // matches sibling bundles that start with the same prefix, e.g. querying
    // `RuntimeTestsApp` would sweep up `RuntimeTestsApp.tvOS-*.ips`. Require
    // that whatever follows the prefix is a real separator so we don't
    // cross-contaminate diagnostics across platforms.
    static IEnumerable<string> EnumerateCrashLogsForApp(string appName)
    {
        return Directory.GetFiles(CrashLogDir, $"{appName}*.ips")
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name.Length == appName.Length) return true;
                if (!name.StartsWith(appName)) return false;
                var sep = name[appName.Length];
                return sep == '-' || sep == '_';
            });
    }

    /// <summary>
    /// Detects Mono JIT crash signatures in crash log content or device log.
    /// </summary>
    public static bool IsMonoJitCrash(string text)
    {
        return text.Contains("jit-info") ||
               text.Contains("mono_jit") ||
               text.Contains("ReleaseHandle") ||
               text.Contains("jit-info.c:918");
    }

    static bool IsCrashOutput(string text)
    {
        return text.Contains("SIGABRT") ||
               text.Contains("SIGSEGV") ||
               text.Contains("SIGBUS") ||
               text.Contains("Fatal error") ||
               text.Contains("CRASH") ||
               text.Contains("EXC_BAD_ACCESS") ||
               text.Contains("Assertion") && text.Contains("not met");
    }
}
