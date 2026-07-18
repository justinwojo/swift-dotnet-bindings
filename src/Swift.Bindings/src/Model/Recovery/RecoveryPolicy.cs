// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>Why a withdrawal is not safe.</summary>
public enum RecoveryObstruction
{
    /// <summary>No obstruction — the withdrawal is safe.</summary>
    None,

    /// <summary>
    /// The graph does not know the unit. Fail-closed: an unmodelled surface is never assumed
    /// droppable, because the assumption that would make it droppable is exactly the one nobody
    /// checked.
    /// </summary>
    UnknownUnit,

    /// <summary>
    /// The unit contributes to a layout its escalation parent still exposes — a frozen struct's
    /// stored field while the struct survives, a slot position inside a retained vtable. Withdrawing
    /// it would leave the retained parent describing a shape that no longer matches.
    /// </summary>
    ParentLayoutRetained,

    /// <summary>
    /// A retained unit requires this one. Withdrawing it would leave that unit promising a capability
    /// the binding no longer provides.
    /// </summary>
    RetainedDependent,
}

/// <summary>The answer to "can this unit be withdrawn", with the reason attached.</summary>
public readonly record struct RecoveryVerdict
{
    /// <summary>Whether the withdrawal is safe.</summary>
    public bool IsSafe => Obstruction == RecoveryObstruction.None;

    /// <summary>What blocks the withdrawal; <see cref="RecoveryObstruction.None"/> when safe.</summary>
    public RecoveryObstruction Obstruction { get; init; }

    /// <summary>
    /// The unit responsible for the obstruction — the retained parent or the retained dependent.
    /// Null when safe, or when the obstructed unit is not in the graph at all.
    /// </summary>
    public RecoveryUnitId? Blocker { get; init; }

    /// <summary>A safe verdict.</summary>
    public static RecoveryVerdict Safe => new() { Obstruction = RecoveryObstruction.None };

    /// <summary>An unsafe verdict.</summary>
    public static RecoveryVerdict Blocked(RecoveryObstruction obstruction, RecoveryUnitId? blocker = null) =>
        new() { Obstruction = obstruction, Blocker = blocker };

    /// <summary>Human-readable explanation, for report text and diagnostics.</summary>
    public string Explain() => Obstruction switch
    {
        RecoveryObstruction.None => "safe to withdraw",
        RecoveryObstruction.UnknownUnit => "not a known recovery unit",
        RecoveryObstruction.ParentLayoutRetained =>
            $"contributes to the layout of retained '{Blocker?.Describe() ?? "parent"}'",
        RecoveryObstruction.RetainedDependent =>
            $"retained '{Blocker?.Describe() ?? "unit"}' requires it",
        _ => "unsafe to withdraw",
    };

    /// <inheritdoc />
    public override string ToString() => Explain();
}

/// <summary>How an escalation walk ended.</summary>
public enum EscalationOutcome
{
    /// <summary>
    /// Every unit in the withdrawal set is safe to withdraw given what remains. The binding degrades
    /// and stays sound.
    /// </summary>
    Closed,

    /// <summary>
    /// Escalation reached the module unit: no coarser-but-still-sound withdrawal existed, so the
    /// whole binding is implicated. Callers must treat this as a failure, not a degradation.
    /// </summary>
    ReachedModule,

    /// <summary>
    /// The walk hit a unit it could not reason about and could not escalate — an unknown unit, or one
    /// with no escalation parent. Fail-closed: no withdrawal set is proposed.
    /// </summary>
    Blocked,
}

/// <summary>The withdrawal set an escalation walk settled on, and how it ended.</summary>
public sealed record EscalationResult
{
    /// <summary>Every unit that must be withdrawn together.</summary>
    public required ImmutableHashSet<RecoveryUnitId> Withdrawn { get; init; }

    /// <summary>How the walk ended.</summary>
    public required EscalationOutcome Outcome { get; init; }

    /// <summary>The unit that ended a <see cref="EscalationOutcome.Blocked"/> walk; null otherwise.</summary>
    public RecoveryUnitId? BlockedAt { get; init; }

    /// <summary>Number of escalation rounds performed. Zero when the seeds were already closed.</summary>
    public required int Rounds { get; init; }

    /// <summary>Whether the walk produced a usable, sound withdrawal set.</summary>
    public bool IsUsable => Outcome == EscalationOutcome.Closed;
}

/// <summary>
/// The soundness rule for withdrawing generated surfaces, as pure functions over a
/// <see cref="RecoveryGraph"/>.
/// </summary>
/// <remarks>
/// <para>
/// A removal is safe iff it alters no retained ABI footprint <em>and</em> leaves no retained
/// capability with an unsatisfied obligation. Those are the two halves
/// <see cref="SafeToDrop(RecoveryGraph, RecoveryUnitId, IReadOnlySet{RecoveryUnitId})"/> checks:
/// layout contribution against a retained parent, and retained units that require this one.
/// </para>
/// <para>
/// Nothing here decides <em>whether</em> to withdraw anything, and nothing here withdraws a surface
/// that was merely degraded. A member that keeps its public shape but throws — a suppressed reverse
/// -dispatch member whose vtable slot is deliberately retained for layout parity — is not a
/// withdrawal and must not be modelled as one: its slot still has to be there.
/// </para>
/// </remarks>
public static class RecoveryPolicy
{
    /// <summary>
    /// Whether <paramref name="unit"/> can be withdrawn while <paramref name="retained"/> stays.
    /// </summary>
    /// <param name="graph">The recovery graph.</param>
    /// <param name="unit">The unit proposed for withdrawal. Expected not to be in
    /// <paramref name="retained"/> — it is the thing leaving.</param>
    /// <param name="retained">The units that would remain.</param>
    public static RecoveryVerdict SafeToDrop(
        RecoveryGraph graph,
        RecoveryUnitId unit,
        IReadOnlySet<RecoveryUnitId> retained)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(retained);

        if (graph.Find(unit) is not { } found)
            return RecoveryVerdict.Blocked(RecoveryObstruction.UnknownUnit);

        // Half one — does removing this alter a footprint someone retained still exposes? Owning a
        // whole vtable and withdrawing it is fine; contributing field N to a struct that survives is
        // not, and only the unit itself knows which of the two it is.
        if (found.ContributesToParentLayout
            && found.EscalationParent is { } parent
            && retained.Contains(parent))
        {
            return RecoveryVerdict.Blocked(RecoveryObstruction.ParentLayoutRetained, parent);
        }

        // Half two — does removing this leave a retained capability with an unsatisfied obligation?
        foreach (var dependent in graph.Provides(unit))
        {
            if (retained.Contains(dependent))
                return RecoveryVerdict.Blocked(RecoveryObstruction.RetainedDependent, dependent);
        }

        return RecoveryVerdict.Safe;
    }

    /// <summary>
    /// Grows a set of failing units into the smallest sound withdrawal set: start at the smallest
    /// attributable scope and escalate to parents until every obligation closes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each round takes the dependent closure of the current seeds, treats everything else as
    /// retained, and re-tests every closure member. Recomputing <c>retained</c> from the
    /// <em>current</em> closure each round is what makes the loop terminate: testing offenders against
    /// the original retained set would leave an offender whose parent has already joined the closure
    /// permanently offending.
    /// </para>
    /// <para>
    /// The obligation half is discharged structurally rather than by iteration. The closure is
    /// dependent-closed by construction, so a unit inside it can never have a retained dependent —
    /// every dependent is already in the closure. That leaves layout contribution as the only
    /// obstruction the loop can actually encounter, and escalating an offender to its parent always
    /// adds a unit the closure did not contain (its parent was retained, which is why it offended).
    /// The closure therefore grows strictly every round and is bounded by the graph.
    /// </para>
    /// </remarks>
    public static EscalationResult Escalate(RecoveryGraph graph, IEnumerable<RecoveryUnitId> seeds)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(seeds);

        var withdrawn = seeds.ToImmutableHashSet();
        if (withdrawn.IsEmpty)
        {
            return new EscalationResult
            {
                Withdrawn = withdrawn,
                Outcome = EscalationOutcome.Closed,
                Rounds = 0,
            };
        }

        var universe = graph.Units.Select(u => u.Id).ToImmutableHashSet();

        for (var round = 0; ; round++)
        {
            var closure = graph.DependentClosure(withdrawn);
            var retained = universe.Except(closure);

            var escalations = new HashSet<RecoveryUnitId>();
            foreach (var member in closure)
            {
                var verdict = SafeToDrop(graph, member, retained);
                if (verdict.IsSafe)
                    continue;

                // Nowhere left to escalate: an unknown unit, or the module itself. Either way the walk
                // cannot propose a sound set, and saying so beats proposing an unsound one.
                if (graph.Find(member) is not { EscalationParent: { } parent })
                {
                    return new EscalationResult
                    {
                        Withdrawn = closure,
                        Outcome = member.Scope == RecoveryScope.Module
                            ? EscalationOutcome.ReachedModule
                            : EscalationOutcome.Blocked,
                        BlockedAt = member,
                        Rounds = round,
                    };
                }

                escalations.Add(parent);
            }

            if (escalations.Count == 0)
            {
                return new EscalationResult
                {
                    Withdrawn = closure,
                    Outcome = closure.Any(id => id.Scope == RecoveryScope.Module)
                        ? EscalationOutcome.ReachedModule
                        : EscalationOutcome.Closed,
                    Rounds = round,
                };
            }

            var grown = closure.Union(escalations);

            // Defensive: the argument above says the closure grows every round, so a round that adds
            // nothing means an invariant broke. Stopping beats spinning.
            if (grown.Count == closure.Count)
            {
                return new EscalationResult
                {
                    Withdrawn = closure,
                    Outcome = EscalationOutcome.Blocked,
                    BlockedAt = escalations.First(),
                    Rounds = round,
                };
            }

            withdrawn = grown;
        }
    }
}
