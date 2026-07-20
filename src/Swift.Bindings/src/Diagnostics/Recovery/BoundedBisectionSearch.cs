// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace BindingsGeneration.Diagnostics;

/// <summary>
/// The result of a bounded bisection over withdrawable recovery units: the culprit set the search
/// isolated (empty when it declined), and how many render→compile probes it spent.
/// </summary>
/// <remarks>
/// Isolation is all-or-nothing. The search returns a non-empty <see cref="Isolated"/> only when it has
/// <em>confirmed</em> that withdrawing exactly those units clears the compile and that retaining them
/// while withdrawing every other candidate does not — anything short of that confirmation is a decline,
/// so a search-isolated culprit is never a guess. A decline carries the same probe count so a caller can
/// see the budget was honoured.
/// </remarks>
public sealed record BisectionOutcome
{
    /// <summary>The confirmed culprit units, or empty when the search declined.</summary>
    public required ImmutableArray<RecoveryUnitId> Isolated { get; init; }

    /// <summary>How many render→compile probes the search spent — bounded by the probe budget.</summary>
    public required int ProbesUsed { get; init; }

    /// <summary>True when the search confirmed a non-empty culprit set within budget.</summary>
    public bool DidIsolate => !Isolated.IsDefaultOrEmpty;

    /// <summary>A decline: no culprit was confirmed. Falls back to the escalation ladder unchanged.</summary>
    public static BisectionOutcome Declined(int probesUsed = 0) =>
        new() { Isolated = ImmutableArray<RecoveryUnitId>.Empty, ProbesUsed = probesUsed };

    /// <summary>A confirmed isolation of <paramref name="units"/>.</summary>
    public static BisectionOutcome Isolate(IEnumerable<RecoveryUnitId> units, int probesUsed) =>
        new() { Isolated = units.ToImmutableArray(), ProbesUsed = probesUsed };
}

/// <summary>
/// A bounded, dependency-aware delta-debug over withdrawable recovery units — the verify-recover loop's
/// fallback for a failure attribution could not place. It is deliberately <em>subordinate</em> to
/// attribution: it runs only after the symbol-anchored provenance ladder has already declined to name a
/// culprit, it spends at most a single-digit probe budget, and it isolates a culprit only when held-out
/// probes confirm the withdrawal both suffices and is necessary. Anything less is a decline, and a
/// decline leaves the module failing closed exactly as it does today.
/// </summary>
/// <remarks>
/// <para>
/// The search is sound because leaf/accessor withdrawal is ABI-neutral: removing a member or an accessor
/// pair shifts no retained surface's layout and strands no dependent, so withdrawing any subset of the
/// candidates can only <em>remove</em> failures, never introduce one. That monotonicity is what makes the
/// three gates decisive:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Containment.</b> Withdraw every candidate at once. If the compile still fails, the culprit is not a
/// withdrawable leaf at all — no leaf search can help — so decline immediately (one probe). Only a clean
/// withdraw-all proves the culprit lies inside the candidate set, which is the precondition the whole
/// search rests on.
/// </description></item>
/// <item><description>
/// <b>Narrowing.</b> Halve the candidate groups and withdraw one half. Under a single culprit,
/// withdrawing a half clears the compile iff the culprit is in that half, so one probe per level decides
/// which half to keep. Groups are never split internally — a group is a needs-closed set of units — so a
/// probe never separates a unit from the units it depends on.
/// </description></item>
/// <item><description>
/// <b>Confirmation.</b> Two held-out probes the narrowing never took: withdrawing only the isolated group
/// must clear the compile (sufficiency), and withdrawing every other candidate while retaining the
/// isolated group must still fail (necessity). Multiple culprits, or a spurious narrowing result, fail one
/// of these and the search declines.
/// </description></item>
/// </list>
/// <para>
/// The probe budget bounds the whole search to a single digit; a candidate pool too large to narrow
/// within it declines rather than overrun. Every probe is a full render→compile under the base denylist
/// plus a candidate subset, so the caller supplies the probe and the search never renders anything itself.
/// </para>
/// </remarks>
public static class BoundedBisectionSearch
{
    /// <summary>
    /// The default probe budget: a single-digit ceiling on render→compile probes per module. Sized so the
    /// two fixed containment/confirmation probes plus binary narrowing isolate a pool of up to a few dozen
    /// candidate groups; a larger pool declines rather than overrun the budget.
    /// </summary>
    public const int DefaultProbeBudget = 8;

    /// <summary>
    /// Runs the bounded search over <paramref name="candidateGroups"/>, using <paramref name="probeClean"/>
    /// to render+compile under the base denylist plus a candidate subset and report whether that compile is
    /// clean. Returns the confirmed culprit set, or <see cref="BisectionOutcome.Declined(int)"/>.
    /// </summary>
    /// <param name="candidateGroups">
    /// The withdrawable units, grouped by needs-closure — each group is withdrawn atomically so a probe
    /// never splits a unit from the units it depends on. On the wave-2 production path every group is a
    /// singleton leaf, since no populated recovery graph gives a leaf any dependents.
    /// </param>
    /// <param name="probeClean">
    /// Renders+compiles under the base denylist unioned with the given units and returns true iff the
    /// compile is clean. Called at most <paramref name="probeBudget"/> times.
    /// </param>
    /// <param name="probeBudget">The single-digit ceiling on probes. Must be ≥ 1.</param>
    public static BisectionOutcome Run(
        IReadOnlyList<ImmutableArray<RecoveryUnitId>> candidateGroups,
        Func<IReadOnlyCollection<RecoveryUnitId>, bool> probeClean,
        int probeBudget = DefaultProbeBudget)
    {
        ArgumentNullException.ThrowIfNull(candidateGroups);
        ArgumentNullException.ThrowIfNull(probeClean);
        if (probeBudget < 1)
            throw new ArgumentOutOfRangeException(nameof(probeBudget), probeBudget, "Probe budget must be at least 1.");

        // Non-empty groups only: an empty group carries no unit to withdraw and would make the containment
        // and narrowing probes ambiguous.
        var groups = candidateGroups.Where(g => !g.IsDefaultOrEmpty).ToList();
        if (groups.Count == 0)
            return BisectionOutcome.Declined();

        var probes = 0;
        var allUnits = groups.SelectMany(g => g).Distinct().ToArray();

        // Gate 1 — containment. If withdrawing every candidate still fails, the culprit is not a
        // withdrawable leaf; no leaf search can clear it, so decline before spending the budget narrowing.
        if (probes >= probeBudget)
            return BisectionOutcome.Declined(probes);
        probes++;
        if (!probeClean(allUnits))
            return BisectionOutcome.Declined(probes);

        // Gate 2 — narrowing. Halve the groups and probe the left half; under a single culprit, a clean
        // left-withdrawal means the culprit is in the left half, and a still-failing one means it is in the
        // right (no extra probe needed). One probe per level, so a pool of N groups narrows in ⌈log2 N⌉.
        var working = groups;
        while (working.Count > 1)
        {
            if (probes >= probeBudget)
                return BisectionOutcome.Declined(probes);

            var mid = working.Count / 2;
            var left = working.Take(mid).ToList();
            var leftUnits = left.SelectMany(g => g).Distinct().ToArray();

            probes++;
            working = probeClean(leftUnits) ? left : working.Skip(mid).ToList();
        }

        var culprit = working[0];
        var culpritUnits = culprit.Distinct().ToArray();

        // Gate 3(a) — sufficiency. Withdrawing only the isolated group must clear the compile. A held-out
        // probe the narrowing never took: it withdrew supersets, never the culprit alone.
        if (probes >= probeBudget)
            return BisectionOutcome.Declined(probes);
        probes++;
        if (!probeClean(culpritUnits))
            return BisectionOutcome.Declined(probes);

        // Gate 3(b) — necessity. Retaining the isolated group while withdrawing every other candidate must
        // still fail. If it clears, the failure was not this group's — a second culprit or a spurious
        // narrowing — so decline rather than withdraw an innocent member.
        var othersOnly = allUnits.Except(culpritUnits).ToArray();
        if (othersOnly.Length > 0)
        {
            if (probes >= probeBudget)
                return BisectionOutcome.Declined(probes);
            probes++;
            if (probeClean(othersOnly))
                return BisectionOutcome.Declined(probes);
        }

        return BisectionOutcome.Isolate(culpritUnits, probes);
    }
}
