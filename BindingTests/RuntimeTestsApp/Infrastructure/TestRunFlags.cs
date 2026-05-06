// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

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
}
