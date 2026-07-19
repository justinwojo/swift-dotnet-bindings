// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// Finding 50: which input-edge category a resolution decision belongs to.
/// </summary>
public enum InputResolutionCategory
{
    /// <summary>Choosing the xcframework platform slice (device vs simulator).</summary>
    SliceSelection,

    /// <summary>Choosing the CPU architecture within a slice.</summary>
    Architecture,

    /// <summary>Locating the <c>.swiftinterface</c> used for internal-member detection.</summary>
    SwiftInterface,

    /// <summary>Locating or generating the ABI JSON.</summary>
    AbiJson,

    /// <summary>Locating, generating, or synthesizing the TBD.</summary>
    Tbd,

    /// <summary>Loading a dependency module's types.</summary>
    Dependency,

    /// <summary>
    /// Finding 58: the active host toolchain (Xcode major) vs. the tested support envelope. A version
    /// outside <see cref="SupportedToolchain"/>'s range is a degradation; an unreadable one is not.
    /// </summary>
    Toolchain,
}

/// <summary>
/// Finding 50: whether a resolution decision used the requested input or quietly substituted a
/// different one. Only <see cref="Degradation"/> trips the fail-closed gate.
/// </summary>
public enum InputResolutionSeverity
{
    /// <summary>The requested input was found and used as-is.</summary>
    Info,

    /// <summary>A fallback substituted a different input than requested (the graceful-to-a-fault path).</summary>
    Degradation,
}

/// <summary>
/// Finding 50: one input-resolution decision (a slice/arch/artifact choice or a dependency load),
/// recorded in chronological order.
/// </summary>
public sealed record InputResolutionDecision(
    InputResolutionCategory Category,
    InputResolutionSeverity Severity,
    string Detail);

/// <summary>
/// Finding 50: per-generation input-resolution report. The input edge of the pipeline
/// (<see cref="XCFrameworkResolver"/>, dependency parsing) historically degraded silently —
/// a requested device slice fell back to the simulator slice at <c>LogWarning</c>, an
/// arch-specific artifact fell back to "any" at <c>LogInformation</c>, an auto-detected
/// dependency that failed to parse shrank the API surface at warning level. None of these
/// were observable after the fact, and none could fail a CI gate.
/// </summary>
/// <remarks>
/// <para>This ambient collector accumulates every slice decision, fallback, and degraded
/// dependency across one generation, so they can be (a) surfaced on the artifact manifest and
/// (b) turned into a fatal error under <c>--strict-inputs</c> (the CI compile gate). It mirrors
/// the existing ambient collectors (<see cref="ReportCollector"/>,
/// <see cref="AppleSupplementReferences"/>): <c>[ThreadStatic]</c> so parallel test fixtures
/// stay isolated, flushed via <see cref="Reset"/> at the start of a generation. Production
/// invocation is single-threaded and resolution runs on the same call chain as emission, so the
/// decisions recorded during <c>Resolve</c> are visible when the manifest is written.</para>
/// </remarks>
public static class InputResolutionReport
{
    [ThreadStatic]
    private static List<InputResolutionDecision>? s_decisions;

    /// <summary>Clears all recorded decisions. Call once at the start of a generation.</summary>
    public static void Reset() => s_decisions?.Clear();

    /// <summary>
    /// Captures the decisions recorded so far. The verify-recover loop snapshots before its repeated
    /// per-render wrapper compiles and <see cref="Restore"/>s after, so the finalized manifest records
    /// exactly one resolution's worth of decisions — byte-identical to the single-render path — rather
    /// than the loop's N× accumulation.
    /// </summary>
    public static IReadOnlyList<InputResolutionDecision> Snapshot() => Decisions;

    /// <summary>Restores the recorded decisions to a prior <see cref="Snapshot"/>, discarding any added since.</summary>
    public static void Restore(IReadOnlyList<InputResolutionDecision> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        (s_decisions ??= new List<InputResolutionDecision>()).Clear();
        s_decisions.AddRange(snapshot);
    }

    /// <summary>Records a decision that used the requested input as-is.</summary>
    public static void RecordInfo(InputResolutionCategory category, string detail) =>
        Record(category, InputResolutionSeverity.Info, detail);

    /// <summary>
    /// Records a fallback that substituted a different input than requested. These are what
    /// <c>--strict-inputs</c> escalates to a fatal error.
    /// </summary>
    public static void RecordDegradation(InputResolutionCategory category, string detail) =>
        Record(category, InputResolutionSeverity.Degradation, detail);

    private static void Record(InputResolutionCategory category, InputResolutionSeverity severity, string detail)
    {
        if (string.IsNullOrEmpty(detail))
            return;
        (s_decisions ??= new List<InputResolutionDecision>()).Add(
            new InputResolutionDecision(category, severity, detail));
    }

    /// <summary>All decisions recorded since the last reset, in chronological order.</summary>
    public static IReadOnlyList<InputResolutionDecision> Decisions =>
        s_decisions is null ? Array.Empty<InputResolutionDecision>() : s_decisions.ToArray();

    /// <summary>True when at least one degradation (input substitution) was recorded.</summary>
    public static bool HasDegradations =>
        s_decisions is not null && s_decisions.Exists(d => d.Severity == InputResolutionSeverity.Degradation);
}
