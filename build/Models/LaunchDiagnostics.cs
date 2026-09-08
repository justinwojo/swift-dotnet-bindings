// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

/// <summary>
/// Tells apart the two very different things a failed launch can mean:
///
///   (a) the app RAN and produced the wrong answer — a real product regression, and
///   (b) the LAUNCHER (devicectl / simctl) gave up before the app's process ever started —
///       no product signal was produced at all, so the run carries no verdict either way.
///
/// The distinction matters because a gate that reports (b) using (a)'s wording sends the reader
/// hunting for a binding, marshalling, or ObjC-registration defect that the evidence does not
/// support. It has also actually happened: a `--mixed-pack --device` run went red with "the ObjC
/// type was not usable through the single Swift-binding PackageReference" when the CoreDevice
/// trace showed devicectl aborting with EINVAL after creating its console sockets and BEFORE it
/// ever sent the launch request to the phone — the same bundle then ran clean a dozen times.
/// </summary>
public static class LaunchDiagnostics
{
    // The launcher's own confirmation that it handed the process off to the OS. Past this point
    // any failure is the app's (and therefore ours): a dyld error, a crash, a wrong greeting.
    static readonly string[] LauncherStartedApp =
    {
        "Launched application with",  // devicectl
        "Waiting for the application to terminate",  // devicectl --console
    };

    // The launcher reporting that IT could not proceed. These are tooling/transport conditions,
    // not app behaviour — the app image is never entered.
    static readonly string[] LauncherAborted =
    {
        "The application failed to launch",           // devicectl
        "com.apple.dt.CoreDeviceError",               // devicectl (transport/tunnel/socket)
        "Unable to lookup in current state",          // simctl (device shut down mid-run)
        "FBSOpenApplicationServiceErrorDomain",       // simctl (SpringBoard refused the open)
        "The request to open",                        // simctl
        "An error was encountered processing the command",  // simctl
    };

    // Markers the test app itself prints. If any of these appear the app demonstrably ran, so the
    // failure is ours no matter what the launcher said afterwards.
    static readonly string[] AppProducedOutput =
    {
        "RESULTS FLUSHED",
        "TEST SUCCESS",
        "TEST FAILURE",
        "=== RUNTIME TESTS ===",
        "Runtime flavor:",
        "[TEST]", "[PASS]", "[FAIL]", "[SKIP]", "[WARN] SKIP:",
        "OBJC_GREETING:", "FOREIGN_CATEGORY:",
        "dyld[", "Library not loaded:", "Symbol not found:",
        "DllNotFoundException", "EntryPointNotFoundException",
        "Unhandled exception",
        "Native Crash Reporting", "App terminated due to signal ",
    };

    /// <summary>
    /// True when the evidence says the launcher never got the app running: the launch is a
    /// <see cref="TestResult.LaunchFailure"/>, the launcher printed one of its own abort messages,
    /// it never confirmed a start, and the app produced no output of its own.
    ///
    /// Deliberately conservative — every clause must hold. A launch with recognized app, test, loader or crash output,
    /// or that the launcher confirmed it started, is treated as a product result and reported as
    /// one. Recognized product evidence is kept out of the launcher-only retry path.
    /// </summary>
    public static bool LauncherNeverStartedApp(TestResult result, string output)
    {
        if (result != TestResult.LaunchFailure) return false;
        if (string.IsNullOrEmpty(output)) return false;
        if (HasProductEvidence(output)) return false;
        if (ContainsAny(output, LauncherStartedApp)) return false;
        return ContainsAny(output, LauncherAborted);
    }

    /// <summary>
    /// Classifies the final drained console using the reason polling ended. A timed-out run
    /// cannot become a late success. Confirmed fatal output wins even over a completed summary;
    /// weaker diagnostic words retain the simulator's existing completed-summary precedence.
    /// </summary>
    public static TestResult ClassifyFinalOutput(TestResult observed, string output)
    {
        if (HasFatalTermination(output)) return TestResult.Crash;
        if (observed is TestResult.Crash or TestResult.Failure) return observed;
        if (observed == TestResult.Timeout) return TestResult.Timeout;
        if (output.Contains("TEST FAILURE", StringComparison.Ordinal)) return TestResult.Failure;
        if (output.Contains("TEST SUCCESS", StringComparison.Ordinal) || observed == TestResult.Success)
            return TestResult.Success;
        if (HasCrashOutput(output)) return TestResult.Crash;
        // Started but exited without a summary is a product failure, not evidence of a crash.
        if (HasProductEvidence(output) || ContainsAny(output, LauncherStartedApp))
            return TestResult.Failure;
        return TestResult.LaunchFailure;
    }

    /// <summary>
    /// A started app's failure without a terminal app marker warrants termination diagnostics.
    /// Recovery additionally requires an unfinished inventory-validated invocation.
    /// </summary>
    public static bool IsIncompleteProductFailure(TestResult result, string output) =>
        result == TestResult.Failure &&
        !output.Contains("RESULTS FLUSHED", StringComparison.Ordinal) &&
        !output.Contains("TEST SUCCESS", StringComparison.Ordinal) &&
        !output.Contains("TEST FAILURE", StringComparison.Ordinal);

    public static bool ShouldInspectTermination(TestResult result, string output) =>
        result is TestResult.Crash or TestResult.Timeout or TestResult.LaunchFailure ||
        IsIncompleteProductFailure(result, output);

    static bool HasProductEvidence(string output) =>
        ContainsAny(output, AppProducedOutput) || HasCrashOutput(output);

    static bool HasFatalTermination(string output)
    {
        // Mono's fatal report is stronger than an incidental diagnostic containing "Assertion".
        if (output.Contains("Native Crash Reporting", StringComparison.Ordinal) &&
            output.Contains("Got a SIG", StringComparison.Ordinal)) return true;
        if (output.Contains("Abort trap: 6", StringComparison.Ordinal) ||
            output.Contains("Segmentation fault: 11", StringComparison.Ordinal)) return true;
        const string prefix = "App terminated due to signal ";
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith(prefix, StringComparison.Ordinal) && line.EndsWith(".", StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan(prefix.Length, line.Length - prefix.Length - 1), out var signal) &&
                signal > 0 && signal is not (9 or 15))
                return true;
        }
        // The harness deliberately kills/terminates after completion or timeout. SIGKILL/SIGTERM
        // alone therefore cannot turn a successful run into a crash or fabricate timeout cause.
        return false;
    }

    static bool HasCrashOutput(string text) =>
        text.Contains("SIGABRT", StringComparison.Ordinal) ||
        text.Contains("SIGSEGV", StringComparison.Ordinal) ||
        text.Contains("SIGBUS", StringComparison.Ordinal) ||
        text.Contains("Fatal error", StringComparison.Ordinal) ||
        text.Contains("CRASH", StringComparison.Ordinal) ||
        text.Contains("EXC_BAD_ACCESS", StringComparison.Ordinal) ||
        (text.Contains("Assertion", StringComparison.Ordinal) && text.Contains("not met", StringComparison.Ordinal));

    /// <inheritdoc cref="LauncherNeverStartedApp(TestResult, string)"/>
    public static bool LauncherNeverStartedApp(LaunchResult result) =>
        LauncherNeverStartedApp(result.Result, result.Output);

    /// <summary>
    /// How many times a launch is re-attempted when the LAUNCHER aborted before the app started.
    /// One budget for every caller: the one-shot consumer gates and the RuntimeTests resume loops.
    /// </summary>
    public const int MaxLauncherAbortAttempts = 3;

    /// <summary>
    /// True once <paramref name="abortCount"/> launcher aborts have been observed and the budget is
    /// spent. The caller must then fail with a launch-specific diagnosis — never a test verdict.
    /// </summary>
    public static bool LauncherAbortBudgetExhausted(int abortCount) => abortCount >= MaxLauncherAbortAttempts;

    /// <summary>
    /// How long to settle before re-attempting a launch the launcher aborted: linear backoff
    /// (5s, 10s, …) keyed on how many aborts have been seen so far.
    ///
    /// The RuntimeTests resume loops previously had no settle at all — a real device run burned all
    /// six of its attempts in sixteen seconds, re-issuing the identical install→launch pattern each
    /// time. This is retry pacing only; it is NOT an attempt to make the launch itself succeed, and
    /// no delay is inserted ahead of a first attempt.
    /// </summary>
    public static TimeSpan SettleDelayAfterAbort(int abortCount) =>
        TimeSpan.FromSeconds(5 * Math.Max(1, abortCount));

    static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
