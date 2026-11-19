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
    /// The architectures to grade: <paramref name="probeArchitecture"/> (the slice the loop already
    /// compiles) first, then every other architecture the wrapper decision builds from
    /// <paramref name="targetArchitectures"/> against the probed slice's own architectures. A decision
    /// that cannot be made (an invalid token, an explicit architecture the slice lacks) grades the probe
    /// architecture alone: the post-loop compile owns the one diagnostic for it.
    /// </summary>
    public static IReadOnlyList<string> Plan(
        string probeArchitecture,
        string? targetArchitectures,
        IReadOnlyList<string> sliceArchitectures,
        string sliceId)
    {
        var autoMatchSource = string.Equals(targetArchitectures?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
        var requested = autoMatchSource
            ? new List<string>()
            : BindingsGenerator.ParseTargetArchitectures(targetArchitectures, NullLogger.Instance);
        if (requested is null
            || !BindingsGenerator.TryDecideWrapperArchitectures(
                autoMatchSource, requested, sliceArchitectures, sliceId, NullLogger.Instance,
                out var primary, out var extras))
            return [probeArchitecture];
        return Distinct(probeArchitecture, primary is null ? extras : extras.Prepend(primary));
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
        ILogger logger)
    {
        var outcomes = new List<(string Architecture, WrapperCompileDiagnostics Outcome)>
        {
            (architectures[0], compileArchitecture(architectures[0])),
        };
        foreach (var architecture in architectures.Skip(1))
        {
            WrapperCompileDiagnostics outcome;
            try
            {
                outcome = compileArchitecture(architecture);
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
        && !outcome.Diagnostics.Any(g => Diagnostics.DiagnosticAttributor.IsGlobalInputError(g.Primary));

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
