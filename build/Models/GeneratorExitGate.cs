// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;

/// <summary>
/// The policy for what a NON-ZERO generator exit means to the runtime-test leg that requested the
/// regeneration.
///
/// <para><b>The defect this closes.</b> Every regeneration path used to log a non-zero generator exit
/// as a warning ("this is expected if the test library includes features beyond current generator
/// support") and then go on to build the app against bindings that were never emitted. The macOS /
/// Mac Catalyst paths were routed through this gate first; the iOS-family paths (simulator, device,
/// tvOS, and the standalone regen targets that share their entry point) followed. A generator that exits 1 having written zero C# does not produce a degraded run; it
/// produces roughly two thousand <c>CS0246</c>s in the app build, every one of them naming a binding
/// type rather than the single generator error that actually happened. The reader is handed a wall of
/// noise that accuses the bindings, and the one honest line scrolled past minutes earlier.</para>
///
/// <para><b>The policy.</b> Hard-fail by default; <c>--permissive</c> demotes it to a warning and
/// proceeds. <c>--strict</c> stays fail-closed even under <c>--permissive</c>, so the fail-closed
/// predicate is <c>strict || !permissive</c> — the identical shape the compile gate's other
/// fail-closed steps already use. It is deliberately NOT "warn by default": warn-by-default is what
/// the harness used to do, and that behaviour is the bug.</para>
///
/// <para>Note this is a policy about the generator's <i>exit code</i>, not about how much of the
/// library it managed to bind. Partial binding — unsupported members emitted as documented skips — is
/// a supported, exit-zero outcome and is unaffected.</para>
/// </summary>
public static class GeneratorExitGate
{
    /// <summary>
    /// Whether a generator outcome must abort the leg. Fail-closed: an explicit
    /// <paramref name="permissive"/> opt-out is the only thing that downgrades a failure, and
    /// <paramref name="strict"/> overrides even that.
    /// </summary>
    public static bool ShouldFail(int exitCode, bool strict, bool permissive) =>
        exitCode != 0 && (strict || !permissive);

    /// <summary>
    /// Whether a generator outcome should be reported as a warning and execution continued. Only ever
    /// true under <c>--permissive</c> without <c>--strict</c>; an exit-zero generator reports nothing.
    /// </summary>
    public static bool ShouldWarn(int exitCode, bool strict, bool permissive) =>
        exitCode != 0 && !strict && permissive;

    /// <summary>
    /// Names the leg for the diagnostics below. Every regeneration path except macOS / Mac Catalyst
    /// leaves the platform override unset and lets the generator apply its own iOS default, so a null
    /// or blank <paramref name="platformName"/> must read as <c>ios</c> rather than leaving a hole in
    /// the sentence — an unnamed leg is exactly the ambiguity these messages exist to remove.
    /// </summary>
    public static string LegLabel(string? platformName) =>
        $"the {(string.IsNullOrWhiteSpace(platformName) ? "ios" : platformName!.Trim())} binding-tests leg";

    /// <summary>
    /// The diagnosis a reader gets in place of a wall of unexplained compile errors. Names the leg and
    /// the module whose generation failed, the exit code, the consequence of continuing, and the
    /// opt-out.
    /// </summary>
    public static string FailureMessage(string legLabel, string moduleLabel, int exitCode) =>
        $"{moduleLabel} binding generation FAILED for {legLabel} (generator exit code {exitCode}). " +
        "This is a generator failure, not a test result: a generator that exits non-zero may have " +
        "written no C# at all, and continuing would build the app against bindings that do not exist " +
        "— surfacing one real error as hundreds of unrelated CS0246 'type or namespace not found' " +
        "errors that accuse the bindings instead of the generator. Fix the generator failure (its " +
        "output is replayed above); pass --permissive without --strict to downgrade this to a " +
        "warning and proceed anyway (--strict keeps it fatal regardless).";

    /// <summary>The same diagnosis, worded for the <c>--permissive</c> path that proceeds regardless.</summary>
    public static string WarningMessage(string legLabel, string moduleLabel, int exitCode) =>
        $"{moduleLabel} binding generation FAILED for {legLabel} (generator exit code {exitCode}); " +
        "continuing because --permissive was passed. Results from this run are NOT trustworthy — if " +
        "the generator wrote no C#, the app build will fail with hundreds of unrelated CS0246 errors " +
        "naming binding types that were never emitted.";
}
