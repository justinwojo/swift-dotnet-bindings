// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>
/// One withdrawable thing: an identity, the artifacts it owns, what it contributes to the binary
/// interface, what it needs in order to stay, and where it escalates when it cannot stay.
/// </summary>
/// <remarks>
/// Units are built by <see cref="RecoveryGraphBuilder"/> and consumed by <see cref="RecoveryPolicy"/>.
/// Nothing here records a decision — a unit describes what would be lost, not whether to lose it.
/// </remarks>
public sealed record RecoveryUnit
{
    /// <summary>This unit's identity.</summary>
    public required RecoveryUnitId Id { get; init; }

    /// <summary>Convenience projection of <see cref="RecoveryUnitId.Scope"/>.</summary>
    public RecoveryScope Scope => Id.Scope;

    /// <summary>
    /// Where recovery goes when this unit cannot be withdrawn on its own. Null only for a
    /// <see cref="RecoveryScope.Module"/> unit, which is the terminus.
    /// </summary>
    public RecoveryUnitId? EscalationParent { get; init; }

    /// <summary>Union of the footprints of every artifact in this unit.</summary>
    public AbiFootprint Footprint { get; init; }

    /// <summary>
    /// True when withdrawing this unit would change an agreed layout its escalation parent still
    /// exposes — the "never droppable alone" cases.
    /// </summary>
    public bool ContributesToParentLayout { get; init; }

    /// <summary>
    /// Units that must remain for this one to remain. The obligation half of the safe-to-drop
    /// question: a retained unit whose <c>Requires</c> names a withdrawn unit is left promising
    /// something the binding no longer provides.
    /// </summary>
    public ImmutableArray<RecoveryUnitId> Requires { get; init; } = ImmutableArray<RecoveryUnitId>.Empty;

    /// <summary>Every generated artifact that belongs to this unit.</summary>
    public ImmutableArray<ArtifactId> Artifacts { get; init; } = ImmutableArray<ArtifactId>.Empty;

    /// <summary>Whether this unit's own rule permits withdrawing it without escalating first.</summary>
    public bool DroppableAlone => !ContributesToParentLayout;

    /// <inheritdoc />
    public override string ToString() => Id.Canonical;
}

/// <summary>
/// The recovery graph: every unit in a module, their escalation chains, and their
/// <c>Requires</c>/<c>Provides</c> edges.
/// </summary>
/// <remarks>
/// <c>Provides</c> is derived, never stored: it is exactly the inverse of <c>Requires</c>, and
/// keeping a second copy is how the two drift apart. <see cref="Provides"/> answers "who is relying
/// on this unit", which is what the escalation walk needs.
/// </remarks>
public sealed class RecoveryGraph
{
    private readonly IReadOnlyDictionary<RecoveryUnitId, RecoveryUnit> _units;
    private readonly IReadOnlyDictionary<RecoveryUnitId, ImmutableArray<RecoveryUnitId>> _provides;

    internal RecoveryGraph(
        IReadOnlyDictionary<RecoveryUnitId, RecoveryUnit> units,
        IReadOnlyDictionary<RecoveryUnitId, ImmutableArray<RecoveryUnitId>> provides)
    {
        _units = units;
        _provides = provides;
    }

    /// <summary>Every unit, in insertion order.</summary>
    public IEnumerable<RecoveryUnit> Units => _units.Values;

    /// <summary>Number of units in the graph.</summary>
    public int Count => _units.Count;

    /// <summary>Whether the graph knows this unit.</summary>
    public bool Contains(RecoveryUnitId id) => _units.ContainsKey(id);

    /// <summary>Looks a unit up; null when the graph does not know it.</summary>
    public RecoveryUnit? Find(RecoveryUnitId id) => _units.GetValueOrDefault(id);

    /// <summary>Units <paramref name="id"/> depends on. Empty for an unknown unit.</summary>
    public ImmutableArray<RecoveryUnitId> Requires(RecoveryUnitId id) =>
        _units.TryGetValue(id, out var unit) ? unit.Requires : ImmutableArray<RecoveryUnitId>.Empty;

    /// <summary>
    /// Units that depend on <paramref name="id"/> — the inverse of <see cref="Requires"/>, i.e. the
    /// set this unit provides for. Empty for an unknown unit or one nothing depends on.
    /// </summary>
    public ImmutableArray<RecoveryUnitId> Provides(RecoveryUnitId id) =>
        _provides.TryGetValue(id, out var dependents) ? dependents : ImmutableArray<RecoveryUnitId>.Empty;

    /// <summary>
    /// The escalation chain above <paramref name="id"/>, nearest first, ending at a
    /// <see cref="RecoveryScope.Module"/> unit. Empty for an unknown unit or a module unit.
    /// </summary>
    public IEnumerable<RecoveryUnitId> Ancestors(RecoveryUnitId id)
    {
        var seen = new HashSet<RecoveryUnitId>();
        var current = Find(id)?.EscalationParent;
        while (current is { } parent && seen.Add(parent))
        {
            yield return parent;
            current = Find(parent)?.EscalationParent;
        }
    }

    /// <summary>
    /// Every unit that would lose an obligation if the given seeds were withdrawn — the seeds
    /// themselves plus everything transitively depending on them. Unknown seeds are preserved in the
    /// result so a caller can tell "nothing depends on it" from "the graph never heard of it".
    /// </summary>
    public ImmutableHashSet<RecoveryUnitId> DependentClosure(IEnumerable<RecoveryUnitId> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);

        var closure = new HashSet<RecoveryUnitId>();
        var pending = new Stack<RecoveryUnitId>();
        foreach (var seed in seeds)
        {
            if (closure.Add(seed))
                pending.Push(seed);
        }

        while (pending.Count > 0)
        {
            foreach (var dependent in Provides(pending.Pop()))
            {
                if (closure.Add(dependent))
                    pending.Push(dependent);
            }
        }

        return closure.ToImmutableHashSet();
    }
}
