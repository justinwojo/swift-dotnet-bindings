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

    static readonly string[] PreferredDevices = ["iPhone 16", "iPhone 16 Pro", "iPhone 15 Pro", "iPhone 15"];

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
    /// Preferred device list matches the bash script: iPhone 16, 15 Pro, 15.
    /// </summary>
    public static SimDevice EnsureBootedDevice()
    {
        // Check for already-booted
        var booted = ListDevices("iOS")
            .FirstOrDefault(d => d.State == "Booted");
        if (booted != null)
        {
            Log.Information("Using already-booted simulator: {Name} ({Udid})", booted.Name, booted.Udid);
            return booted;
        }

        // Find preferred device to boot
        var available = ListDevices("iOS")
            .Where(d => d.IsAvailable && d.Name.Contains("iPhone"))
            .ToList();

        var device = PreferredDevices
            .Select(pref => available.FirstOrDefault(d => d.Name == pref))
            .FirstOrDefault(d => d != null)
            ?? available.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "No available iPhone simulator found. Install one via Xcode.");

        Log.Information("Booting simulator: {Name} ({Udid})", device.Name, device.Udid);
        Boot(device.Udid);
        return device;
    }

    // --- Lifecycle ---

    public static void Boot(string udid)
    {
        ProcessTasks.StartProcess("xcrun", $"simctl boot {udid}")
            .AssertWaitForExit();
        Log.Information("Waiting for simulator to finish booting...");
        ProcessTasks.StartProcess("xcrun", $"simctl bootstatus {udid} -b")
            .AssertWaitForExit();
        Log.Information("Simulator booted.");
    }

    public static void Install(string udid, string appPath)
    {
        Log.Information("Installing app...");
        ProcessTasks.StartProcess("xcrun", $"simctl install {udid} \"{appPath}\"")
            .AssertWaitForExit();
    }

    /// <summary>
    /// Launches app on simulator, captures console output, and detects test completion or crash.
    /// Uses 0.25s polling interval matching the shell script for fast response.
    /// Replaces the 80-line poll-sleep-kill-grep pattern in run-runtime-tests.sh.
    /// </summary>
    public static LaunchResult Launch(string udid, string bundleId, string[] args, TimeSpan timeout)
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

        while (sw.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                // Let buffered output flush
                Thread.Sleep(100);
                var text = string.Join("\n", output);

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
            // NOTE: Do NOT check for crash signals during active polling.
            // Mono's malloc assertion fires during background cleanup but the app
            // continues running and produces the test summary.
            var currentText = string.Join("\n", output);
            if (currentText.Contains("TEST SUCCESS")) { result = TestResult.Success; break; }
            if (currentText.Contains("TEST FAILURE")) { result = TestResult.Failure; break; }

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

        var finalOutput = string.Join("\n", output);
        int? exitCode = null;
        try { if (process.HasExited) exitCode = process.ExitCode; } catch { }

        // Check crash logs if no clear test result
        string? crashLog = null;
        if (result is TestResult.Crash or TestResult.LaunchFailure or TestResult.Timeout)
            crashLog = FindLatestCrashLog("RuntimeTestsApp");

        return new LaunchResult(result, finalOutput, exitCode, crashLog);
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
        try
        {
            var process = ProcessTasks.StartProcess(
                "xcrun",
                $"simctl spawn {udid} log show --last {minutes}m " +
                $"--predicate 'process == \"{processName}\" OR (process == \"ReportCrash\" AND eventMessage CONTAINS \"{processName}\")' " +
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
        return Directory.GetFiles(CrashLogDir, $"{appName}*.ips").Length;
    }

    /// <summary>
    /// Returns the path to the most recent crash log for an app, or null.
    /// </summary>
    public static string? FindLatestCrashLog(string appName)
    {
        if (!Directory.Exists(CrashLogDir)) return null;
        return Directory.GetFiles(CrashLogDir, $"{appName}*.ips")
            .OrderByDescending(File.GetLastWriteTime)
            .FirstOrDefault();
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
