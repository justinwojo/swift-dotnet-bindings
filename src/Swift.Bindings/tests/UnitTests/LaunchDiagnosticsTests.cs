// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="LaunchDiagnostics.LauncherNeverStartedApp(TestResult, string)"/> — the
/// predicate the one-shot mixed consumer gates (<c>--mixed-pack</c>, <c>--mixed-direct</c>) use to
/// tell "devicectl/simctl gave up before the app ever ran" apart from "the app ran and the binding
/// misbehaved". Only the former is retried and only the former is reported as a deploy failure, so
/// the load-bearing invariant is the predicate's CONSERVATISM: anything that could be a product
/// result must classify as false.
///
/// The real-world case it exists for: a <c>--mixed-pack --device</c> run went red claiming the ObjC
/// type "was not usable through the single Swift-binding PackageReference" when the CoreDevice trace
/// showed devicectl aborting with EINVAL right after creating its console sockets and BEFORE it ever
/// sent the launch request — the same bundle then ran clean on every retry.
/// </summary>
public class LaunchDiagnosticsTests
{
    // The verbatim devicectl output from that failure.
    const string DeviceCtlAbortedOutput = """
        Acquired tunnel connection to device.
        Enabling developer disk image services.
        Acquired usage assertion.
        ERROR: The application failed to launch. (com.apple.dt.CoreDeviceError error 10002 (0x2712))
               ----------------------------------------
                   The operation couldn't be completed. Invalid argument (NSPOSIXErrorDomain error 22 (0x16))
        """;

    // The verbatim devicectl output from the same bundle launching successfully.
    const string DeviceCtlRanOutput = """
        Acquired tunnel connection to device.
        Acquired usage assertion.
        Launched application with com.swiftbindings.mixedpack bundle identifier.
        Waiting for the application to terminate…
        MixedPackApp[4550:2741915] OBJC_GREETING:objc-mixed-ok
        MixedPackApp[4550:2741915] RESULTS FLUSHED
        MixedPackApp[4550:2741915] TEST SUCCESS
        """;

    [Fact]
    public void DeviceCtlAbortBeforeLaunch_IsALauncherFailure()
    {
        Assert.True(LaunchDiagnostics.LauncherNeverStartedApp(
            TestResult.LaunchFailure, DeviceCtlAbortedOutput));
    }

    [Theory]
    // simctl's equivalents — the device shut down under us, or SpringBoard refused the open.
    [InlineData("An error was encountered processing the command (domain=NSPOSIXErrorDomain, code=2):\nUnable to lookup in current state: Shutdown")]
    [InlineData("The request to open \"com.swiftbindings.mixeddirect\" failed.\ndomain=FBSOpenApplicationServiceErrorDomain, code=1")]
    public void SimCtlLauncherAborts_AreLauncherFailures(string output)
    {
        Assert.True(LaunchDiagnostics.LauncherNeverStartedApp(TestResult.LaunchFailure, output));
    }

    [Fact]
    public void LauncherConfirmedStart_IsNotALauncherFailure()
    {
        // devicectl said it launched the process, then the app died without printing anything.
        // That is the app's failure — ours — and must never be retried away as tooling noise.
        var output = """
            Launched application with com.swiftbindings.mixedpack bundle identifier.
            Waiting for the application to terminate…
            dyld[4550]: Library not loaded: @rpath/SbMixedPackSwiftBindings.framework/SbMixedPackSwiftBindings
            """;
        Assert.False(LaunchDiagnostics.LauncherNeverStartedApp(TestResult.LaunchFailure, output));
    }

    [Fact]
    public void AppProducedOutput_IsNotALauncherFailure_EvenWhenLauncherAlsoErrored()
    {
        // The app got far enough to print its own markers, so there IS a product signal here. A
        // trailing launcher error (e.g. the harness killing the console) must not mask it.
        var output = """
            OBJC_GREETING:objc-mixed-ok
            RESULTS FLUSHED
            TEST FAILURE: System.EntryPointNotFoundException
            ERROR: The application failed to launch. (com.apple.dt.CoreDeviceError error 10002)
            """;
        Assert.False(LaunchDiagnostics.LauncherNeverStartedApp(TestResult.LaunchFailure, output));
    }

    [Theory]
    [InlineData(TestResult.Success)]
    [InlineData(TestResult.Failure)]
    [InlineData(TestResult.Crash)]
    [InlineData(TestResult.Timeout)]
    public void NonLaunchFailureOutcomes_AreNeverLauncherFailures(TestResult result)
    {
        // A crash or a timeout is the app's behaviour. Only LaunchFailure — "the launch produced no
        // recognizable app outcome at all" — is even a candidate for the launcher-abort reading.
        Assert.False(LaunchDiagnostics.LauncherNeverStartedApp(result, DeviceCtlAbortedOutput));
    }

    [Fact]
    public void SuccessfulRun_IsNotALauncherFailure()
    {
        Assert.False(LaunchDiagnostics.LauncherNeverStartedApp(TestResult.Success, DeviceCtlRanOutput));
    }

    [Theory]
    [InlineData("")]
    [InlineData("some unrecognized launcher chatter with no known abort signature")]
    public void UnrecognizedOrEmptyOutput_IsNotALauncherFailure(string output)
    {
        // Fail closed: without a launcher abort signature we cannot claim the app never started, so
        // the result stays a product verdict and the gate reports it as one.
        Assert.False(LaunchDiagnostics.LauncherNeverStartedApp(TestResult.LaunchFailure, output));
    }

    [Fact]
    public void LaunchResultOverload_AgreesWithTheStringOverload()
    {
        var result = new LaunchResult(TestResult.LaunchFailure, DeviceCtlAbortedOutput, null, null);
        Assert.True(LaunchDiagnostics.LauncherNeverStartedApp(result));
    }

    [Fact]
    public void CapturedDeviceNativeAbort_IsCrashAndNeverLauncherRetry()
    {
        using var stream = typeof(LaunchDiagnosticsTests).Assembly.GetManifestResourceStream(
            "LaunchDiagnostics.device-sigabrt.txt")!;
        using var reader = new System.IO.StreamReader(stream);
        var output = reader.ReadToEnd();
        Assert.Contains("ConsumeDirectAndThrow", output);
        Assert.Contains("App terminated due to signal 6.", output);
        Assert.Equal(TestResult.Crash, LaunchDiagnostics.ClassifyFinalOutput(TestResult.LaunchFailure, output));
        Assert.False(LaunchDiagnostics.LauncherNeverStartedApp(TestResult.LaunchFailure, output));
    }

    [Theory]
    [InlineData("[TEST] --- ExampleTests.TestValue ---")]
    [InlineData("Runtime flavor: IsMonoRuntime=True, IsMonoAot=True")]
    [InlineData("[WARN] SKIP: ExampleTests.TestValue: not enabled")]
    [InlineData("dyld[55]: Library not loaded: @rpath/Example.framework/Example")]
    [InlineData("Unhandled exception. System.EntryPointNotFoundException")]
    public void ProductEvidenceBeforeGenericLauncherError_IsNotRetryable(string productOutput)
    {
        var output = productOutput + "\n" + DeviceCtlAbortedOutput;
        Assert.False(LaunchDiagnostics.LauncherNeverStartedApp(TestResult.LaunchFailure, output));
        Assert.Equal(TestResult.Failure, LaunchDiagnostics.ClassifyFinalOutput(TestResult.LaunchFailure, output));
    }

    [Fact]
    public void FatalOutputBeforeGenericLauncherError_IsCrash()
    {
        var output = "App terminated due to signal 6.\n" + DeviceCtlAbortedOutput;
        Assert.Equal(TestResult.Crash, LaunchDiagnostics.ClassifyFinalOutput(TestResult.LaunchFailure, output));
        Assert.False(LaunchDiagnostics.LauncherNeverStartedApp(TestResult.LaunchFailure, output));
    }

    [Fact]
    public void CapturedPrestartLauncherFailure_RemainsNoProductVerdict()
    {
        var result = LaunchDiagnostics.ClassifyFinalOutput(TestResult.LaunchFailure, DeviceCtlAbortedOutput);
        Assert.Equal(TestResult.LaunchFailure, result);
        Assert.True(LaunchDiagnostics.LauncherNeverStartedApp(result, DeviceCtlAbortedOutput));
    }

    [Theory]
    [InlineData(TestResult.Timeout, "RESULTS FLUSHED\nTEST SUCCESS", TestResult.Timeout)]
    [InlineData(TestResult.Timeout, "App terminated due to signal 6.", TestResult.Crash)]
    [InlineData(TestResult.Success, "RESULTS FLUSHED\nTEST SUCCESS\nApp terminated due to signal 6.", TestResult.Crash)]
    [InlineData(TestResult.Success, "RESULTS FLUSHED\nTEST SUCCESS\nApp terminated due to signal 9.", TestResult.Success)]
    [InlineData(TestResult.Success, "RESULTS FLUSHED\nTEST SUCCESS\nApp terminated due to signal 15.", TestResult.Success)]
    [InlineData(TestResult.LaunchFailure, "RESULTS FLUSHED\nTEST SUCCESS", TestResult.Success)]
    [InlineData(TestResult.Success, "TEST SUCCESS\nTEST FAILURE", TestResult.Failure)]
    [InlineData(TestResult.Failure, "TEST SUCCESS", TestResult.Failure)]
    [InlineData(TestResult.Success, "Assertion was not met\nRESULTS FLUSHED\nTEST SUCCESS", TestResult.Success)]
    [InlineData(TestResult.LaunchFailure, "SIGSEGV", TestResult.Crash)]
    [InlineData(TestResult.LaunchFailure, "", TestResult.LaunchFailure)]
    public void FinalDrainedOutput_PreservesStopReasonAndFatalEvidence(
        TestResult observed, string output, TestResult expected)
        => Assert.Equal(expected, LaunchDiagnostics.ClassifyFinalOutput(observed, output));

    [Theory]
    [InlineData(TestResult.Failure, "Unhandled exception. System.InvalidOperationException", true)]
    [InlineData(TestResult.Failure, "[TEST] --- ExampleTests.TestValue ---", true)]
    [InlineData(TestResult.Failure, "TEST FAILURE", false)]
    [InlineData(TestResult.Failure, "RESULTS FLUSHED", false)]
    [InlineData(TestResult.Success, "TEST SUCCESS", false)]
    [InlineData(TestResult.Crash, "App terminated due to signal 6.", true)]
    [InlineData(TestResult.Timeout, "", true)]
    [InlineData(TestResult.LaunchFailure, "com.apple.dt.CoreDeviceError", true)]
    public void TerminationDiagnostics_IncludeIncompleteProductFailureWithoutInventingCrash(
        TestResult result, string output, bool expected)
        => Assert.Equal(expected, LaunchDiagnostics.ShouldInspectTermination(result, output));

    // ===================================================================
    //  Launcher-abort retry budget and settle curve
    //
    //  The RuntimeTests resume-on-crash loops used to handle LaunchFailure in the SAME branch as
    //  Crash and Timeout. A launcher abort produced no JSONL and no console markers, so recovery
    //  fell through to its blind-skip fallback and excluded an arbitrary test class per attempt —
    //  "recovering" from a failure no test caused, then reporting a test-level verdict for a run in
    //  which nothing executed. Observed for real: six devicectl aborts in sixteen seconds, five real
    //  test classes excluded. These pin the separate budget and the settle pacing that replaced it.
    // ===================================================================

    [Fact]
    public void AbortBudget_IsNotExhaustedBeforeAnyAbort()
    {
        Assert.False(LaunchDiagnostics.LauncherAbortBudgetExhausted(0));
    }

    [Fact]
    public void AbortBudget_ToleratesRetriesThenGivesUp()
    {
        // Two aborts are retried; the third spends the budget. A caller that keeps going past this
        // point is re-issuing an identical launch forever, which is what "six attempts in sixteen
        // seconds" looked like from the outside.
        Assert.False(LaunchDiagnostics.LauncherAbortBudgetExhausted(1));
        Assert.False(LaunchDiagnostics.LauncherAbortBudgetExhausted(2));
        Assert.True(LaunchDiagnostics.LauncherAbortBudgetExhausted(LaunchDiagnostics.MaxLauncherAbortAttempts));
        Assert.True(LaunchDiagnostics.LauncherAbortBudgetExhausted(LaunchDiagnostics.MaxLauncherAbortAttempts + 1));
    }

    [Fact]
    public void SettleDelay_BacksOffBetweenAttempts()
    {
        // Strictly increasing: the point of the settle is that a retry is not a carbon copy of the
        // attempt that just aborted. A flat (or zero) delay is the behaviour being replaced.
        var first = LaunchDiagnostics.SettleDelayAfterAbort(1);
        var second = LaunchDiagnostics.SettleDelayAfterAbort(2);

        Assert.True(first > System.TimeSpan.Zero);
        Assert.True(second > first);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SettleDelay_IsNeverZeroOrNegative(int abortCount)
    {
        // Defensive: a caller that passes a pre-increment count must still wait, never busy-loop.
        Assert.True(LaunchDiagnostics.SettleDelayAfterAbort(abortCount) > System.TimeSpan.Zero);
    }
}
