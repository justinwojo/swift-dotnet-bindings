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
    [InlineData("Sampling process 4242 for 1 second\nCall graph:\n\nTotal number in stack: 0\n", false)]
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
    public void CaptureWithHostCommands_NeverRunsTheDebuggerWhileTheSamplerHasABacktrace()
    {
        var runs = new List<string>();
        var stacks = HungProcessSampler.Capture("app", 4242, HungProcessSampler.HostCommands, TimeSpan.FromSeconds(5), TimeSpan.Zero,
            isAlive: _ => true,
            run: (command, _) =>
            {
                runs.Add(command.FileName);
                return command.FileName == "/usr/bin/sample"
                    ? new HungProcessSampler.CommandRun(false, 0, SampleCallGraph)
                    : throw new InvalidOperationException("the debugger must not attach while the sampler works");
            });

        Assert.Equal(new[] { "/usr/bin/sample", "/usr/bin/sample" }, runs);
        Assert.All(stacks.Samples, s => Assert.Equal(HungProcessSampler.OutcomeCaptured, s.Outcome));
        Assert.All(stacks.Samples, s => Assert.Equal("/usr/bin/sample", s.Tool));
    }

    [Fact]
    public void CaptureWithHostCommands_FallsBackToTheDebuggerWhenTheSamplerFails()
    {
        var runs = new List<string>();
        var stacks = HungProcessSampler.Capture("app", 4242, HungProcessSampler.HostCommands, TimeSpan.FromSeconds(5), TimeSpan.Zero,
            isAlive: _ => true,
            run: (command, _) =>
            {
                runs.Add(command.FileName);
                return command.FileName == "/usr/bin/sample"
                    ? new HungProcessSampler.CommandRun(false, 1, "sample[1]: Failed to examine process 4242")
                    : new HungProcessSampler.CommandRun(false, 0, LldbBacktrace);
            });

        Assert.Equal(new[] { "/usr/bin/sample", "xcrun", "/usr/bin/sample", "xcrun" }, runs);
        Assert.All(stacks.Samples, s => Assert.Equal("lldb", s.Tool));
        Assert.Contains("Failed to examine", stacks.Samples[0].Text);
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
        var run = HungProcessSampler.RunBounded(new HungProcessSampler.Command("/bin/sh", ["-c", "sleep 30 & echo $!"]),
            TimeSpan.FromSeconds(1));
        // The orphaned sleep outlives its exited parent's process tree; the test stops it itself.
        if (int.TryParse(run.Output.Trim(), out var orphan))
            try { Process.GetProcessById(orphan).Kill(); } catch (ArgumentException) { } catch (InvalidOperationException) { }

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

    // What the phone capture printed before the fix: the attach had not stopped the app, so
    // `thread backtrace all` refused, and the batch ended there.
    const string DeviceAttachStillRunning = """
        (lldb) device select 559479FD-3C60-51E4-8B2C-872D8CBA8B54
        (lldb) device process attach --pid 2403
        (lldb) thread backtrace all
        error: Command requires a process which is currently stopped.
        """;

    // `process status` prints the selected thread's top frame even while the attach is still
    // settling; only the backtrace command's own section is a backtrace.
    const string StatusFrameButNoBacktrace = """
        (lldb) device process attach --pid 2415
        (lldb) process status
        Process 2415 is running.
        * thread #1, stop reason = signal SIGSTOP
            frame #0: 0x00000002376825e8
        (lldb) thread backtrace all
        error: Command requires a process which is currently stopped.
        """;

    [Fact]
    public void HasBacktrace_CountsOnlyTheBacktraceCommandsOwnOutput() =>
        Assert.False(HungProcessSampler.HasBacktrace(StatusFrameButNoBacktrace));

    [Fact]
    public void StallProbeShortfall_AcceptsTwoCapturedBacktraces()
    {
        var stacks = HungProcessSampler.Capture("app", 4242, LldbThenSample, TimeSpan.FromSeconds(5), TimeSpan.Zero,
            run: (_, _) => new HungProcessSampler.CommandRun(false, 0, LldbBacktrace));

        Assert.Null(HungProcessSampler.StallProbeShortfall(stacks));
    }

    [Fact]
    public void StallProbeShortfall_RefusesACaptureThatTookNoBacktrace()
    {
        var stacks = HungProcessSampler.Capture("app on device", 2403, _ => [new HungProcessSampler.Command("xcrun", ["lldb"])],
            TimeSpan.FromSeconds(5), TimeSpan.Zero,
            run: (_, _) => new HungProcessSampler.CommandRun(false, 1, DeviceAttachStillRunning));

        var shortfall = HungProcessSampler.StallProbeShortfall(stacks);
        Assert.NotNull(shortfall);
        Assert.Contains("#1 no-backtrace", shortfall);
        Assert.Contains("#2 no-backtrace", shortfall);
    }

    [Fact]
    public void StallProbeShortfall_RefusesOneSampleShort()
    {
        var alive = true;
        var stacks = HungProcessSampler.Capture("app", 4242, LldbThenSample, TimeSpan.FromSeconds(5), TimeSpan.Zero,
            isAlive: _ => alive,
            run: (_, _) => { alive = false; return new HungProcessSampler.CommandRun(false, 0, LldbBacktrace); });

        Assert.Contains("#2 process-gone", HungProcessSampler.StallProbeShortfall(stacks));
    }

    [Fact]
    public void StallProbeShortfall_RefusesASampleLabelledCapturedWithoutThreadFrames()
    {
        var stacks = new HungProcessStacks("app", 4242,
        [
            new StackSample(1, DateTimeOffset.Now, TimeSpan.Zero, "lldb", HungProcessSampler.OutcomeCaptured, LldbBacktrace),
            new StackSample(2, DateTimeOffset.Now, TimeSpan.Zero, "lldb", HungProcessSampler.OutcomeCaptured, StatusFrameButNoBacktrace),
        ]);

        Assert.Contains("#2", HungProcessSampler.StallProbeShortfall(stacks));
    }

    [Fact]
    public void StallProbeShortfall_RefusesAStallWithNoCapture()
    {
        Assert.Contains("no hung-process stacks", HungProcessSampler.StallProbeShortfall(null));
        Assert.Contains("#1 no-pid", HungProcessSampler.StallProbeShortfall(
            HungProcessSampler.Capture("app", null, LldbThenSample, TimeSpan.FromSeconds(5), TimeSpan.Zero)));
    }

    static string SessionSample(int index, double start) => FormattableString.Invariant($"""
        === hung-stacks sample {index} start={start:0.0} end={start + 0.5:0.0} ===
        (lldb) thread backtrace all
        * thread #1, queue = 'com.apple.main-thread', stop reason = signal SIGSTOP
          * frame #0: 0x00000002376825e8 libsystem_kernel.dylib`__semwait_signal + 8
            frame #1: 0x00000001025c1234 RuntimeTestsApp`TestBase_HarnessStallForever + 40
        (lldb) image list -o -f
        [  0] 0x00000000025b0000 /private/var/containers/Bundle/Application/X/RuntimeTestsApp.app/RuntimeTestsApp
        """);

    const string SessionAttach = """
        (lldb) device select 559479FD-3C60-51E4-8B2C-872D8CBA8B54
        (lldb) device process attach --pid 2403
        [1.8s] process connected
        [115.5s] process stopped
        [115.5s] attached; process 2403 stopped
        """;

    static HungProcessStacks CaptureDeviceWith(HungProcessSampler.CommandRun result, List<HungProcessSampler.Command>? runs = null) =>
        HungProcessSampler.CaptureDevice("app on device", "/repo/build/scripts/device-hung-stacks.py", "U", 2403,
            ["/obj/RuntimeTestsApp"], (command, _) => { runs?.Add(command); return result; });

    [Fact]
    public void CaptureDevice_TakesBothSamplesInOneSession()
    {
        var runs = new List<HungProcessSampler.Command>();
        var stacks = CaptureDeviceWith(new HungProcessSampler.CommandRun(false, 0,
            SessionAttach + "\n" + SessionSample(1, 115.5) + "\n(lldb) process continue\n" + SessionSample(2, 121.9) +
            "\n(lldb) process detach\n[122.4s] process 2403 detached"), runs);

        var session = Assert.Single(runs);
        Assert.Equal("xcrun", session.FileName);
        Assert.Equal(new[] { "python3", "-I", "/repo/build/scripts/device-hung-stacks.py", "U", "2403", "2", "5", "300", "/obj/RuntimeTestsApp" },
            session.Arguments);
        Assert.Null(HungProcessSampler.StallProbeShortfall(stacks));
        Assert.Equal(TimeSpan.FromSeconds(6.4), stacks.Samples[1].StartedAt - stacks.Samples[0].StartedAt);
        // Each file stands alone: it says how the session attached and holds only its own backtrace.
        Assert.All(stacks.Samples, s => Assert.Contains("attached; process 2403 stopped", s.Text));
        Assert.Contains("start=115.5", stacks.Samples[0].Text);
        Assert.DoesNotContain("start=121.9", stacks.Samples[0].Text);
        Assert.Contains("start=121.9", stacks.Samples[1].Text);
    }

    [Fact]
    public void CaptureDevice_AnAttachThatNeverStopsTheApp_IsNoBacktrace()
    {
        var stacks = CaptureDeviceWith(new HungProcessSampler.CommandRun(false, 2,
            SessionAttach.Split("[115.5s]")[0] + "error: the attach did not stop process 2403 within 300 s (state connected)"));

        Assert.Equal(HungProcessSampler.OutcomeNoBacktrace, stacks.Samples[0].Outcome);
        Assert.Contains("did not stop process 2403", stacks.Samples[0].Text);
        Assert.Contains("[exit 2]", stacks.Samples[0].Text);
        Assert.Equal(HungProcessSampler.OutcomeSkipped, stacks.Samples[1].Outcome);
        Assert.NotNull(HungProcessSampler.StallProbeShortfall(stacks));
    }

    [Fact]
    public void CaptureDevice_ASessionKilledAfterItsFirstSample_KeepsThatSample()
    {
        var stacks = CaptureDeviceWith(new HungProcessSampler.CommandRun(true, null,
            SessionAttach + "\n" + SessionSample(1, 115.5) + "\n(lldb) process continue"));

        Assert.Equal(HungProcessSampler.OutcomeCaptured, stacks.Samples[0].Outcome);
        Assert.Equal(HungProcessSampler.OutcomeTimedOut, stacks.Samples[1].Outcome);
        Assert.Contains("[killed after 420 s]", stacks.Samples[1].Text);
        Assert.Contains("#2 debugger-timed-out", HungProcessSampler.StallProbeShortfall(stacks));
    }

    [Fact]
    public void CaptureDevice_ASampleWhoseBacktraceFailed_IsNoBacktrace()
    {
        var failed = "=== hung-stacks sample 2 start=121.9 end=122.0 ===\n(lldb) thread backtrace all\n" +
                     "error: Command requires a process which is currently stopped.";
        var stacks = CaptureDeviceWith(new HungProcessSampler.CommandRun(false, 0,
            SessionAttach + "\n" + SessionSample(1, 115.5) + "\n" + failed));

        Assert.Equal(HungProcessSampler.OutcomeCaptured, stacks.Samples[0].Outcome);
        Assert.Equal(HungProcessSampler.OutcomeNoBacktrace, stacks.Samples[1].Outcome);
    }

    [Fact]
    public void CaptureDevice_WithoutAPid_RunsNothing()
    {
        var stacks = HungProcessSampler.CaptureDevice("app", "script", "U", null,
            run: (_, _) => throw new InvalidOperationException("no debugger without a pid"));

        Assert.Equal(HungProcessSampler.OutcomeNoPid, Assert.Single(stacks.Samples).Outcome);
    }

    [Fact]
    public void ParseDwarfdumpUuids_ReadsEverySliceAndSkipsNonMachO()
    {
        var uuids = HungProcessSampler.ParseDwarfdumpUuids("""
            UUID: 1b2c3d4e-0000-1111-2222-333344445555 (arm64) /obj/ios-arm64/RuntimeTestsApp
            UUID: 6F7A8B9C-0000-1111-2222-333344445555 (arm64) /bin/RuntimeTestsApp.app.dSYM/Contents/Resources/DWARF/Runtime Tests
            UUID: 0A0B0C0D-0000-1111-2222-333344445555 (x86_64) /bin/RuntimeTestsApp.app.dSYM/Contents/Resources/DWARF/Runtime Tests
            error: /obj/notes.txt: The file was not recognized as a valid object file
            """);

        Assert.Equal(2, uuids.Count);
        Assert.Equal(new[] { "1B2C3D4E-0000-1111-2222-333344445555" }, uuids["/obj/ios-arm64/RuntimeTestsApp"]);
        Assert.Equal(2, uuids["/bin/RuntimeTestsApp.app.dSYM/Contents/Resources/DWARF/Runtime Tests"].Count);
    }

    [Fact]
    public void Describe_NamesWhyThereAreNoStacks()
    {
        var stacks = HungProcessSampler.Capture("app", 4242, LldbThenSample, TimeSpan.FromSeconds(1), TimeSpan.Zero,
            run: (_, _) => new HungProcessSampler.CommandRun(true, null, ""));

        Assert.Equal("hung-process stacks (app, pid 4242): #1 debugger-timed-out, #2 skipped", stacks.Describe());
    }
}
