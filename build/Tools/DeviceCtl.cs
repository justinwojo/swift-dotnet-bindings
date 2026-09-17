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
            // `--console` forwards our stdin to the app. An inherited stdin the launcher cannot
            // forward — e.g. a peerless unix socket, which is what an agent- or CI-spawned nuke
            // inherits — fails EVERY launch with CoreDeviceError 10002 / NSPOSIXErrorDomain 22
            // (EINVAL) before the process starts. Always hand it a pipe we own, and keep that pipe
            // OPEN for the whole launch: a closed pipe (immediate EOF) launches the app but drops
            // its forwarded console, so the RESULTS FLUSHED marker never arrives.
            RedirectStandardInput = true,
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

                // Classify after redirected readers have drained, not from this partial snapshot.
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
        resultsFlushed |= finalOutput.Contains("RESULTS FLUSHED", StringComparison.Ordinal);
        result = LaunchDiagnostics.ClassifyFinalOutput(result, finalOutput);
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

    /// <summary>
    /// Terminates every running instance of <paramref name="bundleId"/>. `devicectl device process
    /// terminate` only accepts <c>--pid</c>, so the PID is resolved first: the app's installed bundle
    /// URL, then the running processes whose executable lives inside it. Killing the `--console`
    /// launcher does not reliably stop the app, so without this a timed-out or abandoned run can
    /// leave the previous instance running into the next launch.
    /// </summary>
    public static void Terminate(string udid, string bundleId)
    {
        try
        {
            using var apps = RunDeviceCtlJson($"device info apps --device {udid} --bundle-id {bundleId}");
            var bundleUrl = apps?.RootElement.GetProperty("result").GetProperty("apps")
                .EnumerateArray().Select(a => a.GetProperty("url").GetString()).FirstOrDefault(u => u != null);
            if (bundleUrl == null)
                return;

            using var processes = RunDeviceCtlJson($"device info processes --device {udid}");
            if (processes == null)
                return;

            foreach (var p in processes.RootElement.GetProperty("result").GetProperty("runningProcesses").EnumerateArray())
            {
                if (p.TryGetProperty("executable", out var exe)
                    && exe.GetString()?.StartsWith(bundleUrl, StringComparison.Ordinal) == true
                    && p.TryGetProperty("processIdentifier", out var pid))
                {
                    ProcessTasks.StartProcess(
                            "xcrun", $"devicectl device process terminate --device {udid} --pid {pid.GetInt32()} -q",
                            logOutput: false, timeout: 5000)
                        .WaitForExit();
                }
            }
        }
        catch { /* Best-effort termination */ }
    }

    /// <summary>
    /// Runs a devicectl subcommand and parses its <c>--json-output</c> file — the only output
    /// devicectl documents as a stable interface for programs. Returns null on any failure.
    /// </summary>
    static System.Text.Json.JsonDocument? RunDeviceCtlJson(string subcommand)
    {
        var jsonPath = Path.Combine(Path.GetTempPath(), $"devicectl-{Guid.NewGuid():N}.json");
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "xcrun",
                Arguments = $"devicectl {subcommand} -q --json-output \"{jsonPath}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Log.Debug("devicectl {Subcommand} timed out", subcommand);
                return null;
            }
            if (process.ExitCode != 0 || !File.Exists(jsonPath))
            {
                Log.Debug("devicectl {Subcommand} failed (exit {ExitCode}): {Output}",
                    subcommand, process.ExitCode, (stdout.Result + stderr.Result).Trim());
                return null;
            }
            return System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
        }
        catch (Exception ex)
        {
            Log.Debug("devicectl {Subcommand} failed: {Message}", subcommand, ex.Message);
            return null;
        }
        finally
        {
            try { File.Delete(jsonPath); } catch { }
        }
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
