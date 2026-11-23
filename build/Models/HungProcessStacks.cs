// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Explicit, because this file is also link-compiled into Swift.Bindings.Unit.Tests, which builds
// with Nullable=disable.
#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

/// <summary>One debugger run against a hung app: what it printed and how it ended.</summary>
/// <param name="Index">1-based sample number.</param>
/// <param name="Tool">The command that produced <paramref name="Text"/> (<c>lldb</c> or <c>sample</c>).</param>
/// <param name="Outcome">One of the <see cref="HungProcessSampler"/> outcome constants.</param>
/// <param name="Text">Everything the tool printed, or why it was not run.</param>
public sealed record StackSample(int Index, DateTimeOffset StartedAt, TimeSpan Duration, string Tool, string Outcome, string Text);

/// <summary>
/// The stacks of an app the launcher found still running when its launch budget ran out, taken
/// before the launcher killed it. <see cref="Files"/> is empty until the attempt recorder has
/// written the samples beside the attempt's <c>console.log</c>.
/// </summary>
public sealed record HungProcessStacks(string Target, int? Pid, IReadOnlyList<StackSample> Samples)
{
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>One line for a failure message: where the stacks are, or why there are none.</summary>
    public string Describe()
    {
        var outcomes = string.Join(", ", Samples.Select(s => $"#{s.Index} {s.Outcome}"));
        if (Files.Count > 0)
            return $"hung-process stacks ({Target}, pid {Pid?.ToString() ?? "?"}; {outcomes}): {string.Join(", ", Files)}";
        return $"hung-process stacks ({Target}, pid {Pid?.ToString() ?? "?"}): {outcomes}";
    }
}

/// <summary>
/// Takes every thread's backtrace of a hung app before the launcher's stall kill: twice, a few
/// seconds apart, so a thread that is blocked shows the same frames in both and a thread that is
/// spinning shows different ones. lldb symbolicates from the binaries it can read (an on-disk
/// simulator or host app, and the device's own copy for a phone); each sample also lists the
/// loaded images with their slides, so a stripped frame can be resolved later against the
/// build's unstripped copy. Every debugger run is bounded and its process tree is killed when it
/// overruns, so a debugger that hangs or cannot attach costs a bounded delay and never stops
/// the launcher from killing the app. The capture never touches the launch's result.
/// </summary>
public static class HungProcessSampler
{
    public const string OutcomeCaptured = "captured";
    public const string OutcomeNoBacktrace = "no-backtrace";
    public const string OutcomeTimedOut = "debugger-timed-out";
    public const string OutcomeProcessGone = "process-gone";
    public const string OutcomeNoPid = "no-pid";
    public const string OutcomeSkipped = "skipped";

    public const int SampleCount = 2;
    public static readonly TimeSpan HostDebuggerBound = TimeSpan.FromSeconds(60);
    // A phone attach can first copy symbols off the device; the bound still caps it.
    public static readonly TimeSpan DeviceDebuggerBound = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan SampleGap = TimeSpan.FromSeconds(5);

    /// <summary>A command line: the executable and its arguments.</summary>
    public sealed record Command(string FileName, IReadOnlyList<string> Arguments);

    /// <summary>The bounded result of one command.</summary>
    public sealed record CommandRun(bool TimedOut, int? ExitCode, string Output);

    /// <summary>lldb against a process on this Mac (a simulator app, a macOS or Mac Catalyst app).</summary>
    public static Command HostLldb(int pid) => new("xcrun",
    [
        "lldb", "--batch", "--no-lldbinit",
        "-o", $"process attach --pid {pid}",
        "-o", "thread backtrace all",
        "-o", "image list -o -f",
        "-o", "process detach",
    ]);

    /// <summary>The <c>sample</c> fallback for a host process lldb could not attach to.</summary>
    public static Command HostSample(int pid) => new("/usr/bin/sample", [pid.ToString(), "1", "-mayDie"]);

    /// <summary>lldb against a process on a connected phone, through CoreDevice.</summary>
    public static Command DeviceLldb(string udid, int pid) => new("xcrun",
    [
        "lldb", "--batch", "--no-lldbinit",
        "-o", $"device select {udid}",
        "-o", $"device process attach --pid {pid}",
        "-o", "thread backtrace all",
        "-o", "image list -o -f",
        "-o", "process detach",
    ]);

    /// <summary>
    /// True when <paramref name="text"/> holds a thread backtrace: lldb's <c>thread #N</c> rows with
    /// at least one frame, or <c>sample</c>'s call graph.
    /// </summary>
    public static bool HasBacktrace(string text) =>
        (Regex.IsMatch(text, @"^\s*\*?\s*thread #\d+", RegexOptions.Multiline) &&
         Regex.IsMatch(text, @"^\s*\*?\s*frame #\d+", RegexOptions.Multiline)) ||
        Regex.IsMatch(text, @"^Call graph:\s*$", RegexOptions.Multiline);

    static readonly Regex SimctlPid = new(@"^(?<bundle>[A-Za-z0-9.\-]+): (?<pid>[1-9][0-9]*)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// The PID <c>simctl launch</c> prints for the app it started (<c>bundle.id: 12345</c>), or
    /// null. The last such line wins: <c>--terminate-running-process</c> launches once.
    /// </summary>
    public static int? ParseSimctlLaunchPid(string output, string bundleId)
    {
        int? pid = null;
        foreach (Match m in SimctlPid.Matches(output))
            if (m.Groups["bundle"].Value == bundleId && int.TryParse(m.Groups["pid"].Value, out var p))
                pid = p;
        return pid;
    }

    /// <summary>
    /// Takes <see cref="SampleCount"/> samples of <paramref name="pid"/>, <paramref name="gap"/>
    /// apart. Each command in <paramref name="commands"/> is a fallback for the one before it,
    /// tried only when the earlier one finished without a backtrace. A run that overruns
    /// <paramref name="bound"/> is killed and ends the capture: a debugger that hung once is not
    /// given a second chance to hang. The total time is bounded by
    /// <c>SampleCount * commands * bound + gap</c>.
    /// </summary>
    public static HungProcessStacks Capture(string target, int? pid, Func<int, IReadOnlyList<Command>> commands,
        TimeSpan bound, TimeSpan gap, Func<int, bool>? isAlive = null,
        Func<Command, TimeSpan, CommandRun>? run = null)
    {
        run ??= RunBounded;
        var samples = new List<StackSample>();
        if (pid is not int id)
        {
            samples.Add(new StackSample(1, DateTimeOffset.Now, TimeSpan.Zero, "-", OutcomeNoPid,
                "The launcher could not resolve the hung app's process ID, so no debugger was attached."));
            return new HungProcessStacks(target, null, samples);
        }

        var stop = false;
        for (var index = 1; index <= SampleCount; index++)
        {
            if (stop)
            {
                samples.Add(new StackSample(index, DateTimeOffset.Now, TimeSpan.Zero, "-", OutcomeSkipped,
                    "Not taken: an earlier debugger run overran its bound or the process is gone."));
                continue;
            }
            if (index > 1)
                Thread.Sleep(gap);
            if (isAlive != null && !isAlive(id))
            {
                samples.Add(new StackSample(index, DateTimeOffset.Now, TimeSpan.Zero, "-", OutcomeProcessGone,
                    $"Process {id} is no longer running."));
                stop = true;
                continue;
            }

            var started = DateTimeOffset.Now;
            var clock = Stopwatch.StartNew();
            var text = new StringBuilder();
            string tool = "-", outcome = OutcomeNoBacktrace;
            foreach (var command in commands(id))
            {
                tool = command.Arguments.Count > 0 && command.FileName == "xcrun" ? command.Arguments[0] : command.FileName;
                text.AppendLine($"$ {command.FileName} {string.Join(" ", command.Arguments)}");
                CommandRun result;
                try { result = run(command, bound); }
                catch (Exception ex) { result = new CommandRun(false, null, $"could not start: {ex.Message}"); }
                text.AppendLine(result.Output);
                if (result.TimedOut)
                {
                    text.AppendLine($"[killed after {bound.TotalSeconds:0} s]");
                    outcome = OutcomeTimedOut;
                    stop = true;
                    break;
                }
                text.AppendLine($"[exit {result.ExitCode?.ToString() ?? "?"}]");
                if (HasBacktrace(result.Output)) { outcome = OutcomeCaptured; break; }
                outcome = OutcomeNoBacktrace;
            }
            samples.Add(new StackSample(index, started, clock.Elapsed, tool, outcome, text.ToString()));
        }
        return new HungProcessStacks(target, id, samples);
    }

    /// <summary>Captures a process on this Mac: lldb, then <c>sample</c>.</summary>
    public static HungProcessStacks CaptureHost(string target, int? pid) =>
        Capture(target, pid, id => [HostLldb(id), HostSample(id)], HostDebuggerBound, SampleGap, IsHostProcessAlive);

    /// <summary>Captures a process on a connected phone through lldb's CoreDevice support.</summary>
    public static HungProcessStacks CaptureDevice(string target, string udid, int? pid) =>
        Capture(target, pid, id => [DeviceLldb(udid, id)], DeviceDebuggerBound, SampleGap);

    static bool IsHostProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch { return false; }
    }

    /// <summary>
    /// Runs <paramref name="command"/> with stdin closed, collects its output, and kills its whole
    /// process tree if it is still running after <paramref name="bound"/>. The output is drained
    /// against the same deadline (plus a short grace), so a descendant that keeps the pipes open
    /// after the command exits cannot hold the caller either.
    /// </summary>
    public static CommandRun RunBounded(Command command, TimeSpan bound)
    {
        var output = new StringBuilder();
        var gate = new object();
        using var stdoutClosed = new ManualResetEventSlim();
        using var stderrClosed = new ManualResetEventSlim();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) stdoutClosed.Set();
            else lock (gate) output.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) stderrClosed.Set();
            else lock (gate) output.AppendLine(e.Data);
        };
        var clock = Stopwatch.StartNew();
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exited = process.WaitForExit(bound);
        if (exited)
        {
            // EOF on both pipes by the deadline (at least a short grace after the exit): a descendant
            // that outlives the command can keep them open, and that counts as an overrun.
            var deadline = Max(bound, clock.Elapsed + TimeSpan.FromSeconds(2));
            if (!stdoutClosed.Wait(Max(TimeSpan.Zero, deadline - clock.Elapsed)) ||
                !stderrClosed.Wait(Max(TimeSpan.Zero, deadline - clock.Elapsed)))
                exited = false;
        }
        int? exitCode = null;
        if (exited)
        {
            try { exitCode = process.ExitCode; } catch { }
        }
        else
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(TimeSpan.FromSeconds(5)); } catch { }
        }
        lock (gate)
            return new CommandRun(!exited, exitCode, output.ToString().TrimEnd());
    }

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
