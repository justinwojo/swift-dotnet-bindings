// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Self-contained nullable context — see the note in TestResults.cs. This file is link-compiled into
// the unit-test project (Nullable=disable + warnings-as-errors), where `string? RunToken` would
// otherwise raise CS8632.
#nullable enable

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Process-wide flags shared by all four runtime-test hosts (iOS / tvOS /
/// macOS / Mac Catalyst). The hosts populate these from CLI args before the
/// reflection-discovered test classes start running; tests in shared folders
/// (e.g. <c>RuntimeTestsApp/Lifetime/**</c>) read them through this single
/// type so the same code compiles cleanly into every host project.
/// </summary>
/// <remarks>
/// We deliberately do NOT route flags through host-local statics like
/// <c>Application.FlakeDetect</c> or <c>Program.FlakeDetect</c>: those types
/// have different names and namespaces per host (iOS uses
/// <c>RuntimeTestsApp.Application</c>, tvOS uses <c>RuntimeTestsApp.TvOS.Application</c>,
/// macOS / Mac Catalyst use <c>RuntimeTestsApp.Mac.Program</c> /
/// <c>RuntimeTestsApp.MacCatalyst.Program</c>), so any direct reference would
/// only resolve in one host. New cross-host flags should land here.
/// </remarks>
public static class TestRunFlags
{
    /// <summary>
    /// When true, the long-running / GC-pressure assertion blocks inside the
    /// shared <c>RuntimeTestsApp/Lifetime/</c> test classes are enabled.
    /// Skipped by default for inner-loop simulator runs; the integration-branch
    /// serial gate sets this unconditionally. Wired via the <c>--lifetime</c>
    /// CLI arg from <c>nuke binding-tests --lifetime</c>. Bundle work in
    /// 0.10.0 (Bundles 1 and 3) populates the gated assertions.
    /// </summary>
    public static bool Lifetime { get; set; }

    /// <summary>
    /// Opaque per-launch identity token minted by the Nuke harness and passed in
    /// via the <c>--run-token</c> CLI arg. <see cref="TestResults.InitializeJsonl"/>
    /// stamps it as the first line of the JSONL results file so the harness can
    /// prove the file it recovers was written by <em>this</em> launch.
    /// </summary>
    /// <remarks>
    /// This exists because the results file lives in the app's <b>persistent</b>
    /// data container on both simulator and device, and that container survives
    /// reinstall. When a launch fails outright (e.g. devicectl CoreDeviceError
    /// 10002 / EINVAL), the harness would otherwise pull the <em>previous</em>
    /// run's file out of the sandbox and score it as this run's result — a fully
    /// green report for a run that never executed a single test.
    ///
    /// Null when the app is launched by hand (no <c>--run-token</c>); in that case
    /// no token line is written and the harness — which always passes one — refuses
    /// the file. That refusal is deliberate: see the fail-closed note on
    /// <c>JsonlTestResults.HasMatchingRunToken</c>.
    /// </remarks>
    public static string? RunToken { get; set; }
}
