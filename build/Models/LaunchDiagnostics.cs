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
    };

    /// <summary>
    /// True when the evidence says the launcher never got the app running: the launch is a
    /// <see cref="TestResult.LaunchFailure"/>, the launcher printed one of its own abort messages,
    /// it never confirmed a start, and the app produced no output of its own.
    ///
    /// Deliberately conservative — every clause must hold. A launch that produced ANY app output,
    /// or that the launcher confirmed it started, is treated as a product result and reported as
    /// one, so this can never turn a genuine binding regression into a retry.
    /// </summary>
    public static bool LauncherNeverStartedApp(TestResult result, string output)
    {
        if (result != TestResult.LaunchFailure) return false;
        if (string.IsNullOrEmpty(output)) return false;
        if (ContainsAny(output, AppProducedOutput)) return false;
        if (ContainsAny(output, LauncherStartedApp)) return false;
        return ContainsAny(output, LauncherAborted);
    }

    /// <inheritdoc cref="LauncherNeverStartedApp(TestResult, string)"/>
    public static bool LauncherNeverStartedApp(LaunchResult result) =>
        LauncherNeverStartedApp(result.Result, result.Output);

    static bool ContainsAny(string haystack, string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
