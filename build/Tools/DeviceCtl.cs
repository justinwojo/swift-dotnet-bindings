// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            .AssertWaitForExit()
            .AssertZeroExitCode();
    }

    /// <summary>
    /// Launches app on physical device, captures console output, detects test completion.
    /// Waits for RESULTS FLUSHED marker before checking TEST SUCCESS/TEST FAILURE to ensure
    /// JSONL results are fully written before the process is killed.
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
        bool resultsFlushed = false;

        while (sw.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                Thread.Sleep(100);
                var text = string.Join("\n", output);
                resultsFlushed = text.Contains("RESULTS FLUSHED");

                if (text.Contains("TEST SUCCESS"))
                    result = TestResult.Success;
                else if (text.Contains("TEST FAILURE"))
                    result = TestResult.Failure;
                else
                    result = TestResult.LaunchFailure;
                break;
            }

            // Wait for RESULTS FLUSHED before acting on TEST SUCCESS/FAILURE
            var currentText = string.Join("\n", output);
            if (currentText.Contains("RESULTS FLUSHED"))
            {
                resultsFlushed = true;
                if (currentText.Contains("TEST SUCCESS")) { result = TestResult.Success; break; }
                if (currentText.Contains("TEST FAILURE")) { result = TestResult.Failure; break; }
            }

            Thread.Sleep(1000); // 1s polling for device (slower than simulator)
        }

        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }

        Terminate(udid, bundleId);

        // Drain in-flight async output before snapshotting — the poll-break path kills the process
        // immediately, so buffered OutputDataReceived/ErrorDataReceived callbacks may still be
        // delivering queued lines (e.g. an ObjC duplicate-registration warning the mixed device leg
        // greps for). Use the parameterless WaitForExit(), documented to block until the redirected
        // async readers reach EOF (all callbacks fired), unlike the timeout overload. Bound the
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

        return new LaunchResult(result, finalOutput, exitCode, null, resultsFlushed);
    }

    /// <summary>
    /// Copies JSONL test results from the app's sandbox Documents directory on a physical device.
    /// Uses xcrun devicectl to copy the file to a temp location, then reads it.
    /// Returns the file contents, or null if retrieval failed.
    /// </summary>
    /// <param name="expectedRunToken">
    /// The token this launch passed to the app via <c>--run-token</c>. The recovered file must carry
    /// a matching <c>run_token</c> line or it is discarded (returns null, exactly as a failed copy
    /// does). Required because the app's data container is PERSISTENT and survives reinstall: when a
    /// launch fails outright (CoreDeviceError 10002 / EINVAL — the process never starts) the copy
    /// still succeeds and yields the previous run's results, which would otherwise be scored as this
    /// run's and report green for a run that executed nothing.
    /// </param>
    public static string? CopyResultsFromSandbox(string udid, string bundleId, string expectedRunToken)
    {
        try
        {
            var tempDest = Path.Combine(Path.GetTempPath(), $"device-test-results-{Guid.NewGuid():N}.jsonl");

            // devicectl device copy from: copies a file from the device app's data container
            var process = ProcessTasks.StartProcess(
                "xcrun",
                $"devicectl device copy from --device {udid} --domain-type appDataContainer " +
                $"--domain-identifier {bundleId} --source Documents/test-results.jsonl " +
                $"--destination \"{tempDest}\"",
                logOutput: false, timeout: 15000);
            process.WaitForExit();

            if (process.ExitCode == 0 && File.Exists(tempDest))
            {
                Log.Debug("Reading JSONL from device sandbox: {Path}", tempDest);
                var content = File.ReadAllText(tempDest);
                try { File.Delete(tempDest); } catch { }

                // Fail closed: no token, or a token from an earlier launch, means the file cannot be
                // attributed to THIS launch. Return null so the caller's existing "JSONL retrieval
                // failed" path runs — an honest "no results recovered" instead of a silent false green.
                if (!JsonlTestResults.HasMatchingRunToken(content, expectedRunToken))
                {
                    Log.Warning(
                        "Discarding device JSONL: run-token mismatch (expected {Expected}, file carries {Actual}). " +
                        "The app's data container is persistent, so this is a stale file from an earlier run — " +
                        "treating it as no results recovered.",
                        expectedRunToken, JsonlTestResults.ExtractRunToken(content) ?? "<none>");
                    return null;
                }

                return content;
            }

            Log.Debug("devicectl copy failed (exit code {ExitCode})", process.ExitCode);
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug("Failed to copy JSONL from device sandbox: {Message}", ex.Message);
            return null;
        }
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
