// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BindingsGeneration;

/// <summary>
/// The CPU architectures the verify-recover loop grades a wrapper on, and the fold of their outcomes
/// into the one result the loop consumes.
///
/// The shipped wrapper is compiled for a primary architecture and lipo-folded with each extra one
/// (<c>--target-architectures</c>, <see cref="BindingsGenerator.TryDecideWrapperArchitectures"/>). A
/// loop that compiles only the primary architecture passes a member that compiles there and not on
/// another slice (a declaration conditioned on <c>#if arch(...)</c>, a type unavailable on x86_64);
/// the later fold then fails, which either fails the build (an explicit architecture list) or
/// silently ships a narrower wrapper (<c>auto</c>). Grading every architecture the wrapper is built for
/// and unioning the outcomes withdraws such a member on every slice, with its report row, through the
/// same multi-slice contract <see cref="WrapperCompileDiagnostics"/> already gives the device and
/// simulator slices.
/// </summary>
public static class WrapperProbeArchitectures
{
    /// <summary>
    /// The architectures to grade: exactly the ones the wrapper decision builds from
    /// <paramref name="targetArchitectures"/> against the probed slice's own architectures, its primary
    /// first. <paramref name="probeArchitecture"/> (the architecture the slice resolved to) leads only
    /// when the decision names no primary: an explicit list that leaves it out (<c>x86_64</c> on a
    /// universal simulator slice) ships no wrapper for it, so grading it would withdraw members that fail
    /// only there. A decision that cannot be made (an invalid token, an explicit architecture the slice
    /// lacks) grades the probe architecture alone: the post-loop compile owns the one diagnostic for it.
    /// </summary>
    public static IReadOnlyList<string> Plan(
        string probeArchitecture,
        string? targetArchitectures,
        IReadOnlyList<string> sliceArchitectures,
        string sliceId)
    {
        if (!TryDecide(targetArchitectures, sliceArchitectures, sliceId, out var primary, out var extras))
            return [probeArchitecture];
        return primary is null ? Distinct(probeArchitecture, extras) : Distinct(primary, extras);
    }

    /// <summary>
    /// The architecture the shipping compile pins its primary pass to, or null when it pins none (an
    /// arm-only source under <c>auto</c>, no list, or a decision that cannot be made): a null primary
    /// lets each slice pick its own architecture, so a device slice that lacks the simulator's is still
    /// built. The probe resolves the slices it grades the same way.
    /// </summary>
    public static string? DecidedPrimary(
        string? targetArchitectures, IReadOnlyList<string> sliceArchitectures, string sliceId) =>
        TryDecide(targetArchitectures, sliceArchitectures, sliceId, out var primary, out _) ? primary : null;

    /// <summary>
    /// Whether <paramref name="architecture"/> is the loop's primary pass. A pass that compiles one slice
    /// is primary when it is the slice the loop resolved; one that compiles both slices as the shipping
    /// compile does (<c>--wrapper-architectures all</c>) is primary when it is the plan's first
    /// architecture, the pass shipping pins first, whichever architecture the slice resolved to (an
    /// x86_64 + arm64e slice resolves to x86_64 while shipping pins arm64e).
    /// </summary>
    public static bool IsPrimaryPass(
        string architecture, IReadOnlyList<string> plan, string resolvedArchitecture, bool compilesBothSlices) =>
        string.Equals(architecture, compilesBothSlices ? plan[0] : resolvedArchitecture, StringComparison.OrdinalIgnoreCase);

    private static bool TryDecide(
        string? targetArchitectures, IReadOnlyList<string> sliceArchitectures, string sliceId,
        out string? primary, out List<string> extras)
    {
        var autoMatchSource = string.Equals(targetArchitectures?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
        var requested = autoMatchSource
            ? new List<string>()
            : BindingsGenerator.ParseTargetArchitectures(targetArchitectures, NullLogger.Instance);
        if (requested is not null
            && BindingsGenerator.TryDecideWrapperArchitectures(
                autoMatchSource, requested, sliceArchitectures, sliceId, NullLogger.Instance,
                out primary, out extras))
            return true;
        primary = null;
        extras = [];
        return false;
    }

    /// <summary>
    /// The slice an Apple-framework (direct mode) wrapper ships beside the probed one, which the loop must
    /// grade too, or null when it ships none. On a platform with a simulator the wrapper ships both slices
    /// whenever the SDK binds the framework (its second-slice step builds the slice the generator did not)
    /// or the run collects the lowering receipt (the generator compiles both itself): the simulator beside
    /// a device-first run, the device otherwise. (An xcframework wrapper under
    /// <c>--wrapper-architectures all</c> grades both slices through <c>CompileAll</c> itself.)
    /// </summary>
    public static string? OtherShippedSlice(
        bool platformHasSimulator, bool probedSliceIsSimulator, bool sdkMode, bool collectsLoweringEvidence)
    {
        if (!platformHasSimulator || (!sdkMode && !collectsLoweringEvidence))
            return null;
        return probedSliceIsSimulator ? DeviceSlice : SimulatorSlice;
    }

    /// <summary>
    /// The passes of an Apple-framework (direct mode) wrapper: <paramref name="probedArchitectures"/> on
    /// the probed slice, then, when the wrapper ships <paramref name="otherSlice"/> too
    /// (<see cref="OtherShippedSlice"/>), the simulator at every decided architecture it can build
    /// (<paramref name="decidedPrimary"/>, defaulting to <paramref name="simulatorDefaultArchitecture"/>,
    /// and <paramref name="decidedExtras"/>, filtered by <paramref name="simulatorNaturalArchitectures"/>:
    /// the passes the post-loop compile or the SDK's fat second slice builds) or the device at
    /// <paramref name="deviceArchitecture"/>.
    /// </summary>
    public static IReadOnlyList<WrapperProbePass> PlanForAppleFramework(
        IReadOnlyList<string> probedArchitectures,
        string? otherSlice,
        string? decidedPrimary,
        IReadOnlyList<string> decidedExtras,
        string simulatorDefaultArchitecture,
        IReadOnlyList<string> simulatorNaturalArchitectures,
        string deviceArchitecture)
    {
        var otherArchitectures = otherSlice == SimulatorSlice
            ? PlanForSlice(decidedPrimary ?? simulatorDefaultArchitecture, decidedPrimary, decidedExtras, simulatorNaturalArchitectures)
            : [deviceArchitecture];
        return Passes(probedArchitectures, otherSlice, otherArchitectures);
    }

    /// <summary>The <see cref="WrapperProbePass.Slice"/> of a pass on the device slice.</summary>
    public const string DeviceSlice = "device";

    /// <summary>The <see cref="WrapperProbePass.Slice"/> of a pass on the simulator slice.</summary>
    public const string SimulatorSlice = "simulator";

    /// <summary>
    /// The passes to grade: every architecture of <paramref name="probedArchitectures"/> on the probed
    /// slice, in order (the first is the loop's own compile), then each of
    /// <paramref name="otherArchitectures"/> on <paramref name="otherSlice"/> when the wrapper ships it.
    /// </summary>
    public static IReadOnlyList<WrapperProbePass> Passes(
        IReadOnlyList<string> probedArchitectures, string? otherSlice, IReadOnlyList<string> otherArchitectures)
    {
        var passes = probedArchitectures.Select(a => new WrapperProbePass(a, null)).ToList();
        if (otherSlice is not null)
            passes.AddRange(otherArchitectures.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(a => new WrapperProbePass(a, otherSlice)));
        return passes;
    }

    /// <summary>
    /// The architectures to grade on an Apple-framework slice (direct mode, no source xcframework):
    /// <paramref name="probeArchitecture"/> first, then the decided primary and extra architectures the
    /// slice can compile (<see cref="BindingsGenerator.GetAppleFrameworkSliceNaturalArchs"/>, the same
    /// filter the post-loop direct compile applies).
    /// </summary>
    public static IReadOnlyList<string> PlanForSlice(
        string probeArchitecture,
        string? decidedPrimary,
        IReadOnlyList<string> decidedExtras,
        IReadOnlyList<string> sliceNaturalArchitectures)
    {
        var decided = decidedPrimary is null ? decidedExtras : decidedExtras.Prepend(decidedPrimary);
        return Distinct(probeArchitecture,
            decided.Where(a => sliceNaturalArchitectures.Contains(a, StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Compiles every architecture of <paramref name="architectures"/> (the first is the loop's own
    /// compile) and folds the outcomes. An extra architecture that throws, or fails without a member-level
    /// error (no compiler error, or a global input error such as a missing module), is logged and left to
    /// the post-loop compile, which reports it with the build's own diagnostic.
    /// </summary>
    public static WrapperCompileDiagnostics Probe(
        IReadOnlyList<string> architectures,
        Func<string, WrapperCompileDiagnostics> compileArchitecture,
        ILogger logger) =>
        Probe(architectures.Select(a => new WrapperProbePass(a, null)).ToList(), p => compileArchitecture(p.Architecture), logger);

    /// <summary>
    /// <see cref="Probe(IReadOnlyList{string}, Func{string, WrapperCompileDiagnostics}, ILogger)"/> over
    /// passes that may also name the other slice the wrapper ships (<see cref="Passes"/>); a pass on it
    /// is an extra pass like any other architecture.
    /// </summary>
    public static WrapperCompileDiagnostics Probe(
        IReadOnlyList<WrapperProbePass> passes,
        Func<WrapperProbePass, WrapperCompileDiagnostics> compilePass,
        ILogger logger)
    {
        var outcomes = new List<(string Architecture, WrapperCompileDiagnostics Outcome)>
        {
            (passes[0].Label, compilePass(passes[0])),
        };
        foreach (var pass in passes.Skip(1))
        {
            var architecture = pass.Label;
            WrapperCompileDiagnostics outcome;
            try
            {
                outcome = compilePass(pass);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    "The wrapper probe could not compile the {Architecture} slice ({Message}); the wrapper compile reports it.",
                    architecture, ex.Message);
                continue;
            }
            // A failure with no compiler error (a timeout, a missing tool) or with an error outside any
            // declaration (a dependency without this architecture: "no such module") names no member to
            // withdraw. It is the extra architecture's build failing, which the wrapper compile already
            // handles as best effort, so it must not fail the round the primary passed.
            if (!outcome.AllSlicesClean && !NamesAMember(outcome))
            {
                logger.LogWarning(
                    "The wrapper probe's {Architecture} compile failed without a member-level error; the wrapper compile reports it.",
                    architecture);
                continue;
            }
            outcomes.Add((architecture, outcome));
        }
        return Fold(outcomes);
    }

    private static bool NamesAMember(WrapperCompileDiagnostics outcome) =>
        outcome.Diagnostics.Any(g => g.Primary.Severity == Diagnostics.DiagnosticSeverity.Error)
        && !outcome.Diagnostics.Any(g => Diagnostics.DiagnosticAttributor.IsGlobalInputError(g.Primary)
            || IsModuleMissingForArchitecture(g.Primary));

    // The frontend's wording when a module exists but ships no slice for the target being compiled
    // ("could not find module 'X' for target 'x86_64-apple-ios-simulator'; found: arm64-…"): on an
    // extra architecture it is the same input gap as a missing module.
    private static readonly System.Text.RegularExpressions.Regex ModuleMissingForArchitecture = new(
        @"^could not find module '[^']+' for target", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsModuleMissingForArchitecture(Diagnostics.CompilerDiagnostic diagnostic) =>
        diagnostic.Severity == Diagnostics.DiagnosticSeverity.Error
        && ModuleMissingForArchitecture.IsMatch(diagnostic.Message);

    /// <summary>
    /// One outcome over every architecture: clean only when each architecture compiled clean, carrying
    /// the first architecture's compilation result; otherwise failed, with every architecture's slices
    /// (an extra architecture's slice ids suffixed <c>@arch</c>) and the union of their diagnostics, so the
    /// attributor withdraws a member that fails on any of them. A module with no wrapper surface on the
    /// first architecture has none on any.
    /// </summary>
    internal static WrapperCompileDiagnostics Fold(
        IReadOnlyList<(string Architecture, WrapperCompileDiagnostics Outcome)> outcomes)
    {
        var first = outcomes[0].Outcome;
        if (outcomes.Count == 1 || first.NoWrapperSurface)
            return first;

        var slices = outcomes[0].Outcome.Slices
            .Concat(outcomes.Skip(1).SelectMany(o => o.Outcome.Slices.Select(s => s with { SliceId = $"{s.SliceId}@{o.Architecture}" })))
            .ToList();
        if (outcomes.All(o => o.Outcome.AllSlicesClean))
            return WrapperCompileDiagnostics.Clean(first.Result, slices, first.FileProvenance);

        // Provenance maps a diagnostic's line back to the emitted fragment. The source is the same on
        // every architecture, so one entry per file serves them all; a failing architecture's entry is
        // preferred, and each entry carries every architecture's compile paths, because a diagnostic
        // names the path its own compile read (in place for the first, a scratch copy for the others).
        var provenance = outcomes.OrderBy(o => o.Outcome.AllSlicesClean)
            .SelectMany(o => o.Outcome.FileProvenance)
            .GroupBy(p => p.FileName, StringComparer.Ordinal)
            .Select(g => g.First() with
            {
                CompileInputPaths = g.SelectMany(p => p.CompileInputPaths).Distinct(StringComparer.Ordinal).ToArray(),
            })
            .ToList();
        return WrapperCompileDiagnostics.Failed(slices, provenance);
    }

    private static IReadOnlyList<string> Distinct(string first, IEnumerable<string> rest) =>
        rest.Prepend(first).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// One compile of the wrapper probe: <see cref="Architecture"/> on the probed slice, or on
/// <see cref="Slice"/> (<see cref="WrapperProbeArchitectures.DeviceSlice"/> or
/// <see cref="WrapperProbeArchitectures.SimulatorSlice"/>) when the wrapper ships that slice beside it.
/// </summary>
public sealed record WrapperProbePass(string Architecture, string? Slice)
{
    /// <summary>The pass's name in logs and in the suffix of its slice ids (<c>x86_64</c>, <c>device-arm64</c>).</summary>
    public string Label => Slice is null ? Architecture : $"{Slice}-{Architecture}";
}
