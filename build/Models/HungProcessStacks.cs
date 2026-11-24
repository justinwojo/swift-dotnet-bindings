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
/// spinning shows different ones. A process on this Mac is read with <c>sample</c>, which runs no
/// code in the app (lldb is its fallback; see <see cref="HostCommands"/>); a phone's is read in one
/// lldb session through CoreDevice (see <see cref="CaptureDevice"/>). Both symbolicate from the binaries they can read, and each sample also
/// lists the loaded images with their load addresses, so a stripped frame can be resolved later
/// against the build's unstripped copy. Every debugger run is bounded and its process tree is killed when it
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
    // A phone attach reads every loaded image's headers off the phone when this Mac holds no copy
    // of its shared cache: about two minutes over a network link at the minimal load level the
    // session uses. The attach bound covers that; the session bound caps the whole capture.
    public static readonly TimeSpan DeviceAttachBound = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan DeviceSessionBound = TimeSpan.FromSeconds(420);
    public static readonly TimeSpan SampleGap = TimeSpan.FromSeconds(5);

    /// <summary>A command line: the executable and its arguments.</summary>
    public sealed record Command(string FileName, IReadOnlyList<string> Arguments);

    /// <summary>The bounded result of one command.</summary>
    public sealed record CommandRun(bool TimedOut, int? ExitCode, string Output);

    /// <summary>lldb against a process on this Mac (a simulator app, a macOS or Mac Catalyst app): the fallback when <c>sample</c> gets no backtrace.</summary>
    public static Command HostLldb(int pid) => new("xcrun",
    [
        "lldb", "--batch", "--no-lldbinit",
        "-o", $"process attach --pid {pid}",
        "-o", "thread backtrace all",
        "-o", "image list -o -f",
        "-o", "process detach",
    ]);

    /// <summary><c>sample</c> against a process on this Mac: the host capture's first choice, since it runs nothing in the app.</summary>
    public static Command HostSample(int pid) => new("/usr/bin/sample", [pid.ToString(), "1", "-mayDie"]);

    /// <summary>
    /// One lldb session against a process on a connected phone, through CoreDevice
    /// (<c>build/scripts/device-hung-stacks.py</c>): it waits for the attach to stop the app, takes
    /// <see cref="SampleCount"/> backtraces <see cref="SampleGap"/> apart, and detaches.
    /// <paramref name="symbolFiles"/> are the build's unstripped copies of the app's images, which
    /// the session adds by UUID so the app's frames are named.
    /// </summary>
    public static Command DeviceSession(string script, string udid, int pid, IReadOnlyList<string>? symbolFiles = null) => new("xcrun",
    [
        "python3", "-I", script, udid, pid.ToString(), SampleCount.ToString(),
        SampleGap.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
        DeviceAttachBound.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
        .. symbolFiles ?? Array.Empty<string>(),
    ]);

    /// <summary>
    /// True when <paramref name="text"/> holds a thread backtrace: <c>sample</c>'s call graph with a
    /// thread and a frame under it, or a
    /// <c>thread #N</c> row followed by at least one <c>frame #N</c> row. When the text echoes lldb
    /// commands (<c>(lldb) …</c>), only the output of <c>thread backtrace all</c> counts: lldb's
    /// <c>process status</c> also prints the selected thread's top frame, including while an
    /// attach has not yet stopped the process, and that is not a backtrace.
    /// </summary>
    public static bool HasBacktrace(string text)
    {
        if (SampleThreadWithFrame.IsMatch(text))
            return true;
        var lines = text.Split('\n');
        var echoes = lines.Any(l => l.TrimStart().StartsWith("(lldb) ", StringComparison.Ordinal));
        var inBacktrace = !echoes;
        var thread = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimStart().StartsWith("(lldb) ", StringComparison.Ordinal))
            {
                inBacktrace = line.TrimStart()["(lldb) ".Length..].Trim() == "thread backtrace all";
                thread = false;
                continue;
            }
            if (!inBacktrace)
                continue;
            if (ThreadRow.IsMatch(line)) thread = true;
            else if (thread && FrameRow.IsMatch(line)) return true;
        }
        return false;
    }

    // sample's call graph: a thread row, then a frame row indented under it (a second thread row
    // sits at the first one's indent, so it is not a frame).
    static readonly Regex SampleThreadWithFrame = new(@"^Call graph:[ \t]*\r?\n(?<indent>[ \t]+)\d+ Thread_\S+[^\n]*\n\k<indent>[ \t+!:|]+\d+ \S",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly Regex ThreadRow = new(@"^\s*\*?\s*thread #\d+", RegexOptions.CultureInvariant);
    static readonly Regex FrameRow = new(@"^\s*\*?\s*frame #\d+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Why <paramref name="stacks"/> does not prove that a stalled app's stacks were taken, or null
    /// when it does: <see cref="SampleCount"/> samples, each holding a thread backtrace. The stall
    /// probe passes only on that proof, so a capture that ran but read no threads (an attach that
    /// never stopped the app, a debugger that overran, a process that went away) fails it.
    /// </summary>
    public static string? StallProbeShortfall(HungProcessStacks? stacks)
    {
        if (stacks is null)
            return "no hung-process stacks were taken";
        var missing = new List<string>();
        for (var index = 1; index <= SampleCount; index++)
        {
            var sample = stacks.Samples.FirstOrDefault(s => s.Index == index);
            if (sample is null)
                missing.Add($"#{index} not taken");
            else if (sample.Outcome != OutcomeCaptured)
                missing.Add($"#{index} {sample.Outcome}");
            else if (!HasBacktrace(sample.Text))
                missing.Add($"#{index} captured but holds no thread frames");
        }
        return missing.Count == 0
            ? null
            : $"{SampleCount} samples with a thread backtrace are required, got {string.Join(", ", missing)}; {stacks.Describe()}";
    }

    static readonly Regex DwarfdumpUuid = new(@"^UUID: (?<uuid>[0-9A-Fa-f-]{36}) \([^)]*\) (?<path>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// The UUIDs <c>dwarfdump --uuid</c> printed, by file (a fat file has one per slice). Lines
    /// that are not UUID rows (a file that is not Mach-O) are ignored.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseDwarfdumpUuids(string output) =>
        DwarfdumpUuid.Matches(output).Cast<Match>()
            .GroupBy(m => m.Groups["path"].Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(m => m.Groups["uuid"].Value.ToUpperInvariant()).Distinct().ToList(),
                StringComparer.Ordinal);

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

    /// <summary>
    /// The commands a host capture tries, in order: <c>sample</c>, then lldb. <c>sample</c> only
    /// reads the threads. lldb's attach can run code inside the app (its Objective-C runtime
    /// support calls utility functions to describe frame arguments), and on the .NET 11 CoreCLR
    /// interpreter that left the main thread stopped at lldb's return trap, where debugserver
    /// killed the app before the second sample. So lldb is only the fallback for an app
    /// <c>sample</c> cannot read.
    /// </summary>
    public static IReadOnlyList<Command> HostCommands(int pid) => [HostSample(pid), HostLldb(pid)];

    /// <summary>Captures a process on this Mac with <see cref="HostCommands"/>.</summary>
    public static HungProcessStacks CaptureHost(string target, int? pid) =>
        Capture(target, pid, HostCommands, HostDebuggerBound, SampleGap, IsHostProcessAlive);

    /// <summary>
    /// Captures a process on a connected phone with one <see cref="DeviceSession"/>. lldb's
    /// <c>device process attach</c> returns before the attach has stopped the app, and the app
    /// stays running until lldb has read every image off the phone, so a batch of commands reaches
    /// <c>thread backtrace all</c> while the process still runs; the batch then ends on that error,
    /// before its detach. One session waits for the stop and takes both samples, which also reads
    /// the images once rather than once per sample.
    /// </summary>
    public static HungProcessStacks CaptureDevice(string target, string script, string udid, int? pid,
        IReadOnlyList<string>? symbolFiles = null, Func<Command, TimeSpan, CommandRun>? run = null)
    {
        if (pid is not int id)
            return Capture(target, null, _ => [], DeviceSessionBound, SampleGap);
        run ??= RunBounded;
        var command = DeviceSession(script, udid, id, symbolFiles);
        var started = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        CommandRun result;
        try { result = run(command, DeviceSessionBound); }
        catch (Exception ex) { result = new CommandRun(false, null, $"could not start: {ex.Message}"); }
        return SplitSession(target, id, command, started, clock.Elapsed, DeviceSessionBound, result);
    }

    static readonly Regex SessionMarker = new(
        @"^=== hung-stacks sample (?<index>\d+) start=(?<start>[0-9.]+) end=(?<end>[0-9.]+) ===\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits one session's output into its samples at the session's
    /// <c>=== hung-stacks sample N … ===</c> markers. Each sample keeps the command line and the
    /// attach transcript before the first marker, so a file read alone says how the session
    /// attached; a sample whose section holds no backtrace is <see cref="OutcomeNoBacktrace"/>.
    /// A sample the session never reached carries the whole transcript and the reason (the
    /// session overran its bound, or ended without it), and the ones after it are skipped.
    /// </summary>
    public static HungProcessStacks SplitSession(string target, int pid, Command command, DateTimeOffset started,
        TimeSpan elapsed, TimeSpan bound, CommandRun result)
    {
        var output = result.Output;
        var commandLine = $"$ {command.FileName} {string.Join(" ", command.Arguments)}";
        var ending = result.TimedOut
            ? $"[killed after {bound.TotalSeconds:0} s]"
            : $"[exit {result.ExitCode?.ToString() ?? "?"}]";
        var markers = SessionMarker.Matches(output).Cast<Match>().ToList();
        var preamble = markers.Count > 0 ? output[..markers[0].Index].TrimEnd() : output.TrimEnd();
        var samples = new List<StackSample>();
        var reached = true;
        for (var index = 1; index <= SampleCount; index++)
        {
            var at = markers.FindIndex(m => m.Groups["index"].Value == index.ToString());
            if (at < 0)
            {
                if (!reached)
                {
                    samples.Add(new StackSample(index, started, TimeSpan.Zero, "-", OutcomeSkipped,
                        "Not taken: the session ended before an earlier sample."));
                    continue;
                }
                reached = false;
                samples.Add(new StackSample(index, started, elapsed, "lldb",
                    result.TimedOut ? OutcomeTimedOut : OutcomeNoBacktrace,
                    $"{commandLine}\n{output.TrimEnd()}\n{ending}\n"));
                continue;
            }
            var marker = markers[at];
            var end = at + 1 < markers.Count ? markers[at + 1].Index : output.Length;
            var section = output[marker.Index..end].TrimEnd();
            var startSeconds = double.Parse(marker.Groups["start"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var endSeconds = double.Parse(marker.Groups["end"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var body = section[(section.IndexOf('\n') is var nl && nl >= 0 ? nl + 1 : section.Length)..];
            samples.Add(new StackSample(index, started + TimeSpan.FromSeconds(startSeconds),
                TimeSpan.FromSeconds(Math.Max(0, endSeconds - startSeconds)), "lldb",
                HasBacktrace(body) ? OutcomeCaptured : OutcomeNoBacktrace,
                $"{commandLine}\n{preamble}\n{section}\n{ending}\n"));
        }
        return new HungProcessStacks(target, pid, samples);
    }

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
