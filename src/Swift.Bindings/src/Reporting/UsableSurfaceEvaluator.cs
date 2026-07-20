// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Decides whether a binding exposes any usable public surface — the D-R6 ship-or-fail-closed policy for
/// a degenerate binding. A degenerate binding that still exposes something callable (a value type, a
/// direct-native free function) ships with an honest report; one that exposes nothing usable fails closed,
/// because a binding you cannot call is not a smaller binding — it is an empty one.
/// </summary>
/// <remarks>
/// "Usable" is measured from the emission tallies, not the wrapper compile: at least one usable member, or
/// at least one emitted type that is NOT a silent tombstone. A silent tombstone is a type emitted with
/// <c>[OpaqueSwiftType]</c> and zero usable members — it is counted in <see cref="BindingReport.EmittedTypes"/>
/// (its handler runs to completion and records it), so it must be netted out here; a type-set that is
/// entirely tombstones exposes nothing callable. The member and non-tombstone-type arms are OR'd, not
/// AND'd, so a free-function-only or value-type-only binding — which legitimately has one arm zero — still
/// counts as usable.
/// </remarks>
public static class UsableSurfaceEvaluator
{
    /// <summary>The verdict: whether usable surface remains, and a one-line reason for the report/log.</summary>
    public readonly record struct Result(bool HasUsableSurface, string Reason);

    /// <summary>
    /// Evaluates the usable-surface predicate against the emission tallies. <paramref name="silentTombstoneCount"/>
    /// is the number of emitted types that degraded to opaque tombstones (<c>emissionContext.SilentTombstones.Count</c>),
    /// netted out of <see cref="BindingReport.EmittedTypes"/> so a tombstone-only type-set does not read as usable.
    /// </summary>
    public static Result Evaluate(BindingReport report, int silentTombstoneCount)
    {
        System.ArgumentNullException.ThrowIfNull(report);

        if (report.EmittedMembers > 0)
            return new Result(true, $"{report.EmittedMembers} usable member(s) emitted");

        var nonTombstoneTypes = report.EmittedTypes - silentTombstoneCount;
        if (nonTombstoneTypes > 0)
            return new Result(true, $"{nonTombstoneTypes} non-tombstone type(s) emitted");

        return new Result(
            false,
            report.EmittedTypes == 0
                ? "no types or members were emitted"
                : $"all {report.EmittedTypes} emitted type(s) are silent tombstones with zero usable members");
    }
}
