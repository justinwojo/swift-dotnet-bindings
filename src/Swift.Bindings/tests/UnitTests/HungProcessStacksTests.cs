// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The launchers' hung-app capture: the bounded debugger runs that take a stalled app's stacks
/// before the stall kill, and the attempt recorder that keeps them beside the console.
/// </summary>
public sealed class HungProcessStacksTests : IDisposable
{
    const string Token = "0f5e2c8a9b7d4e1f8a6c3b2d1e0f9a8b";
    const string Bundle = "com.swiftbindings.runtimetestsapp";
    readonly string directory = Path.Combine(Path.GetTempPath(), "hung-stacks-tests-" + Guid.NewGuid().ToString("N"));

    const string LldbBacktrace = """
        (lldb) process attach --pid 4242
        Process 4242 stopped
        (lldb) thread backtrace all
        * thread #1, queue = 'com.apple.main-thread', stop reason = signal SIGSTOP
          * frame #0: 0x00000001818dc308 libsystem_kernel.dylib`__semwait_signal + 8
            frame #1: 0x00000001817b7cc0 libsystem_c.dylib`nanosleep + 220
          thread #2
            frame #0: 0x00000001818d9c34 libsystem_kernel.dylib`__workq_kernreturn + 8
        (lldb) process detach
        """;

    const string LldbAttachRefused = """
        (lldb) process attach --pid 4242
        error: attach failed: attach failed (Not allowed to attach to process.)
        (lldb) thread backtrace all
        error: invalid process
        """;

    const string SampleCallGraph = """
        Sampling process 4242 for 1 second with 1 millisecond of run time between samples
        Call graph:
            789 Thread_56054445   DispatchQueue_1: com.apple.main-thread  (serial)
              789 start  (in dyld) + 6992  [0x1815514e4]
        """;

    public void Dispose()
    {
        try { Directory.Delete(directory, recursive: true); } catch { }
    }

    static IReadOnlyList<HungProcessSampler.Command> LldbThenSample(int pid) =>
        [HungProcessSampler.HostLldb(pid), HungProcessSampler.HostSample(pid)];

    [Theory]
    [InlineData("com.swiftbindings.runtimetestsapp: 51234\n[INFO] Starting tests", 51234)]
    [InlineData("noise\r\ncom.swiftbindings.runtimetestsapp: 7\r\n", 7)]
    [InlineData("com.other.app: 51234\n", null)]
    [InlineData("[INFO] com.swiftbindings.runtimetestsapp: 12 objects", null)]
    [InlineData("", null)]
    public void ParseSimctlLaunchPid_ReadsOnlyTheLaunchedBundlesLine(string output, int? expected) =>
        Assert.Equal(expected, HungProcessSampler.ParseSimctlLaunchPid(output, Bundle));

    [Theory]
    [InlineData(LldbBacktrace, true)]
    [InlineData(SampleCallGraph, true)]
    [InlineData(LldbAttachRefused, false)]
    [InlineData("* thread #1, stop reason = signal SIGSTOP\n", false)]
    public void HasBacktrace_RequiresFramesOrACallGraph(string text, bool expected) =>
        Assert.Equal(expected, HungProcessSampler.HasBacktrace(text));

    [Fact]
    public void Capture_TakesTwoSamples_FallingBackWhenTheDebuggerCannotAttach()
    {
        var runs = new List<string>();
        var stacks = HungProcessSampler.Capture("app", 4242, LldbThenSample, TimeSpan.FromSeconds(5), TimeSpan.Zero,
            isAlive: _ => true,
            run: (command, _) =>
            {
                runs.Add(command.FileName);
                return command.FileName == "xcrun"
                    ? new HungProcessSampler.CommandRun(false, 1, LldbAttachRefused)
                    : new HungProcessSampler.CommandRun(false, 0, SampleCallGraph);
            });

        Assert.Equal(new[] { "xcrun", "/usr/bin/sample", "xcrun", "/usr/bin/sample" }, runs);
        Assert.Equal(2, stacks.Samples.Count);
        Assert.All(stacks.Samples, s => Assert.Equal(HungProcessSampler.OutcomeCaptured, s.Outcome));
        Assert.All(stacks.Samples, s => Assert.Equal("/usr/bin/sample", s.Tool));
        // The refused attach stays in the sample, so the reason lldb failed is not lost.
        Assert.Contains("Not allowed to attach", stacks.Samples[0].Text);
    }

    [Fact]
    public void Capture_UsesTheFirstCommandWhenItHasABacktrace()
    {
        var stacks = HungProcessSampler.Capture("app", 4242, LldbThenSample, TimeSpan.FromSeconds(5), TimeSpan.Zero,
            run: (command, _) => command.FileName == "xcrun"
                ? new HungProcessSampler.CommandRun(false, 0, LldbBacktrace)
                : throw new InvalidOperationException("the fallback must not run"));

        Assert.All(stacks.Samples, s => Assert.Equal("lldb", s.Tool));
        Assert.Contains("__semwait_signal", stacks.Samples[1].Text);
    }

    [Fact]
    public void Capture_StopsAfterADebuggerOverrunsItsBound()
    {
        var runs = 0;
        var stacks = HungProcessSampler.Capture("app", 4242, LldbThenSample, TimeSpan.FromSeconds(1), TimeSpan.Zero,
            run: (_, _) => { runs++; return new HungProcessSampler.CommandRun(true, null, "(lldb) process attach --pid 4242"); });

        Assert.Equal(1, runs);
        Assert.Equal(HungProcessSampler.OutcomeTimedOut, stacks.Samples[0].Outcome);
        Assert.Equal(HungProcessSampler.OutcomeSkipped, stacks.Samples[1].Outcome);
    }

    [Fact]
    public void Capture_StopsWhenTheProcessIsGone()
    {
        var alive = true;
        var stacks = HungProcessSampler.Capture("app", 4242, LldbThenSample, TimeSpan.FromSeconds(5), TimeSpan.Zero,
            isAlive: _ => alive,
            run: (_, _) => { alive = false; return new HungProcessSampler.CommandRun(false, 0, LldbBacktrace); });

        Assert.Equal(HungProcessSampler.OutcomeCaptured, stacks.Samples[0].Outcome);
        Assert.Equal(HungProcessSampler.OutcomeProcessGone, stacks.Samples[1].Outcome);
    }

    [Fact]
    public void Capture_WithoutAPid_RunsNothing()
    {
        var stacks = HungProcessSampler.Capture("app", null, LldbThenSample, TimeSpan.FromSeconds(5), TimeSpan.Zero,
            run: (_, _) => throw new InvalidOperationException("no debugger without a pid"));

        var sample = Assert.Single(stacks.Samples);
        Assert.Equal(HungProcessSampler.OutcomeNoPid, sample.Outcome);
        Assert.Null(stacks.Pid);
    }

    [Fact]
    public void RunBounded_KillsACommandThatOverrunsItsBound()
    {
        var clock = Stopwatch.StartNew();
        var run = HungProcessSampler.RunBounded(new HungProcessSampler.Command("/bin/sleep", ["30"]), TimeSpan.FromSeconds(1));

        Assert.True(run.TimedOut);
        Assert.Null(run.ExitCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }

    [Fact]
    public void RunBounded_DoesNotWaitOnADescendantThatHoldsTheOutputOpen()
    {
        var clock = Stopwatch.StartNew();
        var run = HungProcessSampler.RunBounded(new HungProcessSampler.Command("/bin/sh", ["-c", "sleep 30 & echo started"]),
            TimeSpan.FromSeconds(1));

        Assert.True(run.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }

    [Fact]
    public void RunBounded_ReturnsOutputAndExitCode()
    {
        var run = HungProcessSampler.RunBounded(new HungProcessSampler.Command("/bin/sh", ["-c", "echo out; echo err >&2; exit 3"]),
            TimeSpan.FromSeconds(30));

        Assert.False(run.TimedOut);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("out", run.Output);
        Assert.Contains("err", run.Output);
    }

    [Fact]
    public void RecordLaunch_WritesTheStacksBesideTheConsole_AndKeepsTheTimeout()
    {
        var stacks = new HungProcessStacks("RuntimeTestsApp on simulator X", 4242,
        [
            new StackSample(1, DateTimeOffset.Now, TimeSpan.FromSeconds(1), "lldb", HungProcessSampler.OutcomeCaptured, LldbBacktrace),
            new StackSample(2, DateTimeOffset.Now, TimeSpan.FromSeconds(1), "lldb", HungProcessSampler.OutcomeCaptured, LldbBacktrace),
        ]);
        var hung = new LaunchResult(TestResult.Timeout, "[TEST] --- A.B ---", 137, null, false, stacks);
        var attempts = new RuntimeTestAttempts(directory);

        var attempt = attempts.RecordLaunch(0, Token, hung);

        var files = attempt.Launch.HungStacks!.Files;
        Assert.Equal(new[] { "hung-stacks-1.txt", "hung-stacks-2.txt" }, files.Select(Path.GetFileName));
        Assert.All(files, f => Assert.Equal(attempt.Directory, Path.GetDirectoryName(f)));
        Assert.True(File.Exists(Path.Combine(attempt.Directory, "console.log")));
        Assert.Contains("__semwait_signal", File.ReadAllText(files[0]));
        Assert.StartsWith("# RuntimeTestsApp on simulator X, pid 4242: sample 1 of 2, lldb, captured", File.ReadAllText(files[0]));

        // The capture never turns the stall into anything else.
        Assert.Equal(TestResult.Timeout, attempt.Launch.Result);
        Assert.Equal(137, attempt.Launch.ExitCode);
        Assert.Same(attempt.Launch, attempts.FinalResult(attempt.Launch));
        Assert.Equal(files, attempts.FirstProductFailure!.HungStacks!.Files);
        Assert.All(files, f => Assert.Contains(f, attempts.FirstProductFailure.HungStacks!.Describe()));

        using var launch = JsonDocument.Parse(File.ReadAllText(Path.Combine(attempt.Directory, "launch.json")));
        Assert.Equal("Timeout", launch.RootElement.GetProperty("result").GetString());
        Assert.Equal(137, launch.RootElement.GetProperty("exit_code").GetInt32());
        var recorded = launch.RootElement.GetProperty("hung_process_stacks");
        Assert.Equal(4242, recorded.GetProperty("pid").GetInt32());
        Assert.Equal(new[] { "hung-stacks-1.txt", "hung-stacks-2.txt" },
            recorded.GetProperty("samples").EnumerateArray().Select(s => s.GetProperty("file").GetString()));
    }

    [Fact]
    public void RecordLaunch_KeepsTheStallVerdictWhenTheStacksCannotBeWritten()
    {
        var stacks = new HungProcessStacks("app", 4242,
            [new StackSample(1, DateTimeOffset.Now, TimeSpan.Zero, "lldb", HungProcessSampler.OutcomeCaptured, LldbBacktrace)]);
        var attempts = new RuntimeTestAttempts(directory);
        // A directory where the stack file goes makes its write fail.
        Directory.CreateDirectory(Path.Combine(directory, $"00-{Token}", RuntimeTestAttempts.HungStacksFileName(1)));

        Assert.ThrowsAny<IOException>(() =>
            attempts.RecordLaunch(0, Token, new LaunchResult(TestResult.Timeout, "[TEST] --- A.B ---", 137, null, false, stacks)));

        Assert.Equal(TestResult.Timeout, attempts.FirstProductFailure!.Result);
        Assert.Equal(137, attempts.FirstProductFailure.ExitCode);
        Assert.Same(stacks, attempts.FirstProductFailure.HungStacks);
    }

    [Fact]
    public void RecordLaunch_WithoutStacks_WritesNoStackFiles()
    {
        var attempts = new RuntimeTestAttempts(directory);
        var attempt = attempts.RecordLaunch(0, Token, new LaunchResult(TestResult.Success, "TEST SUCCESS", 0, null, true));

        Assert.Null(attempt.Launch.HungStacks);
        Assert.Empty(Directory.GetFiles(attempt.Directory, "hung-stacks-*"));
        using var launch = JsonDocument.Parse(File.ReadAllText(Path.Combine(attempt.Directory, "launch.json")));
        Assert.False(launch.RootElement.TryGetProperty("hung_process_stacks", out _));
    }

    [Fact]
    public void Describe_NamesWhyThereAreNoStacks()
    {
        var stacks = HungProcessSampler.Capture("app", 4242, LldbThenSample, TimeSpan.FromSeconds(1), TimeSpan.Zero,
            run: (_, _) => new HungProcessSampler.CommandRun(true, null, ""));

        Assert.Equal("hung-process stacks (app, pid 4242): #1 debugger-timed-out, #2 skipped", stacks.Describe());
    }
}
