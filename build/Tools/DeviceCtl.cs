// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Nuke.Common.Tooling;
using Serilog;

/// <summary>
/// Manages physical iOS device lifecycle: discovery, app install/launch, and termination.
/// Replaces the devicectl bash patterns in run-runtime-tests.sh.
/// </summary>
public static class DeviceCtl
{
    public record PhysicalDevice(string Udid, string Name);

    /// <summary>
    /// Finds connected iOS devices by parsing xcrun devicectl output.
    /// Falls back to xcrun xctrace if devicectl fails.
    /// Replaces: xcrun devicectl list devices | grep -i "iphone|ipad" | grep -oE UDID_PATTERN
    /// </summary>
    public static IReadOnlyList<PhysicalDevice> ListDevices()
    {
        var devices = new List<PhysicalDevice>();

        // Try devicectl first
        try
        {
            var output = ProcessTasks.StartProcess(
                    "xcrun", "devicectl list devices",
                    logOutput: false)
                .AssertWaitForExit()
                .Output.StdToText();

            devices = ParseDeviceCtlOutput(output);
            if (devices.Count > 0) return devices;
        }
        catch { /* Fall through to xctrace */ }

        // Fallback: xctrace list devices
        try
        {
            var output = ProcessTasks.StartProcess(
                    "xcrun", "xctrace list devices",
                    logOutput: false)
                .AssertWaitForExit()
                .Output.StdToText();

            devices = ParseXctraceOutput(output);
        }
        catch { /* No devices found */ }

        return devices;
    }

    public static void Install(string udid, string appPath)
    {
        Log.Information("Installing app on device {Udid}...", udid);
        ProcessTasks.StartProcess(
                "xcrun", $"devicectl device install app --device {udid} \"{appPath}\"")
            .AssertWaitForExit();
    }

    /// <summary>
    /// Launches app on physical device, captures console output, detects test completion.
    /// Same completion detection pattern as SimCtl.Launch.
    /// </summary>
    public static LaunchResult Launch(string udid, string bundleId, string[] args, TimeSpan timeout)
    {
        var launchArgs = string.Join(" ", args);
        var output = new ConcurrentQueue<string>();

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "xcrun",
            Arguments = $"devicectl device process launch --device {udid} --console {bundleId} {launchArgs}",
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
                Thread.Sleep(100);
                var text = string.Join("\n", output);

                if (text.Contains("TEST SUCCESS"))
                    result = TestResult.Success;
                else if (text.Contains("TEST FAILURE"))
                    result = TestResult.Failure;
                else
                    result = TestResult.LaunchFailure;
                break;
            }

            var currentText = string.Join("\n", output);
            if (currentText.Contains("TEST SUCCESS")) { result = TestResult.Success; break; }
            if (currentText.Contains("TEST FAILURE")) { result = TestResult.Failure; break; }

            Thread.Sleep(1000); // 1s polling for device (slower than simulator)
        }

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }

        Terminate(udid, bundleId);

        var finalOutput = string.Join("\n", output);
        int? exitCode = null;
        try { if (process.HasExited) exitCode = process.ExitCode; } catch { }

        return new LaunchResult(result, finalOutput, exitCode, null);
    }

    public static void Terminate(string udid, string bundleId)
    {
        try
        {
            ProcessTasks.StartProcess(
                    "xcrun", $"devicectl device process terminate --device {udid} {bundleId}",
                    logOutput: false, timeout: 5000)
                .WaitForExit();
        }
        catch { /* Best-effort termination */ }
    }

    // --- Output Parsers ---

    static readonly Regex UdidPattern = new(@"[0-9A-Fa-f]{8,}-[0-9A-Fa-f-]{4,}[0-9A-Fa-f]",
        RegexOptions.Compiled);

    static List<PhysicalDevice> ParseDeviceCtlOutput(string output)
    {
        var devices = new List<PhysicalDevice>();
        var lines = output.Split('\n');

        foreach (var line in lines)
        {
            if (!line.Contains("iPhone", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("iPad", StringComparison.OrdinalIgnoreCase))
                continue;

            var udidMatch = UdidPattern.Match(line);
            if (!udidMatch.Success) continue;

            // Extract device name (text before the UDID, trimmed)
            var nameEnd = line.IndexOf(udidMatch.Value, StringComparison.Ordinal);
            var name = nameEnd > 0 ? line[..nameEnd].Trim().TrimEnd('-', ' ') : "Unknown";

            devices.Add(new PhysicalDevice(udidMatch.Value, name));
        }

        return devices;
    }

    static List<PhysicalDevice> ParseXctraceOutput(string output)
    {
        var devices = new List<PhysicalDevice>();
        var lines = output.Split('\n');

        foreach (var line in lines)
        {
            // Skip simulator lines
            if (line.Contains("Simulator", StringComparison.OrdinalIgnoreCase)) continue;

            // Format: "Device Name (UDID)"
            var parenMatch = Regex.Match(line, @"\(([^)]+)\)\s*$");
            if (!parenMatch.Success) continue;

            var udid = parenMatch.Groups[1].Value;
            if (!UdidPattern.IsMatch(udid)) continue;

            var name = line[..line.LastIndexOf('(')].Trim();
            devices.Add(new PhysicalDevice(udid, name));
        }

        return devices;
    }
}
