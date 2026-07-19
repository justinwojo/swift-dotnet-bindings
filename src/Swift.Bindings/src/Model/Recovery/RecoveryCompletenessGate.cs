// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>
/// One cross-unit reference observed in the settled render — the independent witness the completeness
/// gate cross-checks the captured graph against. In production each rendered P/Invoke whose
/// <c>EntryPoint</c> names a wrapper symbol owned by <see cref="Provider"/>, emitted inside
/// <see cref="Consumer"/>'s artifact, produces one of these.
/// </summary>
/// <remarks>
/// The witness is deliberately built from the settled text, NOT from the same side tables the graph is
/// derived from: a reference is only a real completeness obligation if it actually appears in the
/// output a consumer will link against. This is what makes the gate independent — a rendered reference
/// with no captured edge is an <em>orphan</em>, and an orphan is an invariant failure (the graph missed
/// a dependency the output proves is real), not a warning.
/// </remarks>
public readonly record struct WitnessedReference
{
    /// <summary>The unit whose rendered artifact contains the reference.</summary>
    public required RecoveryUnitId Consumer { get; init; }

    /// <summary>The unit that owns the referenced symbol.</summary>
    public required RecoveryUnitId Provider { get; init; }

    /// <summary>The witnessable edge kind this reference implies (a symbol-witnessable kind).</summary>
    public required RecoveryEdgeKind Kind { get; init; }

    /// <summary>The wrapper symbol named by the reference, for diagnostics. May be empty.</summary>
    public string Symbol { get; init; }
}

/// <summary>
/// The result of cross-checking a <see cref="RecoveryGraph"/>'s captured edges against a witness. A
/// graph is complete only when every witnessed reference has a corresponding captured edge; the orphans
/// are the references the graph failed to model.
/// </summary>
public readonly record struct RecoveryCompletenessReport
{
    /// <summary>
    /// Whether an actual completeness check ran and found every witnessed reference captured. The
    /// <see cref="Checked"/> conjunct is load-bearing: a <c>default(RecoveryCompletenessReport)</c> — the
    /// value a caller gets by forgetting to run <see cref="RecoveryCompletenessGate.Check"/> — has an empty
    /// orphan list but was never checked, and must NOT read as complete. Without the guard an uninitialized
    /// report would silently authorize a withdrawal the gate never examined.
    /// </summary>
    public bool IsComplete => Checked && Orphans.IsDefaultOrEmpty;

    /// <summary>
    /// Whether this report is the result of a real completeness check (via <see cref="RecoveryCompletenessGate.Check"/>
    /// or the explicit <see cref="Complete"/> constant), as opposed to a default-constructed value. Only a
    /// checked report can be complete.
    /// </summary>
    public bool Checked { get; init; }

    /// <summary>
    /// The witnessed references with no captured edge — the modelling gaps that force the whole
    /// authorization to fail closed. A settled reference the graph did not know about is exactly the
    /// wave-1 false-safe: the referenced unit would read as having no dependent when it has one.
    /// </summary>
    public ImmutableArray<WitnessedReference> Orphans { get; init; }

    /// <summary>A complete report (checked, no orphans).</summary>
    public static RecoveryCompletenessReport Complete =>
        new() { Checked = true, Orphans = ImmutableArray<WitnessedReference>.Empty };
}

/// <summary>
/// Cross-checks a captured <see cref="RecoveryGraph"/> against an independent witness of the settled
/// render, per-occurrence, and fails closed on any orphan.
/// </summary>
/// <remarks>
/// <para>
/// Per-occurrence is the load-bearing property. A shared wrapper symbol may be referenced by many
/// consumers; checking only that <em>the symbol</em> has <em>an</em> edge would let one captured caller
/// mask every other uncaptured one — remove the provider and its one modelled caller, retain the
/// unmodelled callers, compile clean, then throw <c>EntryPointNotFoundException</c> at first native
/// call. So the gate checks that <em>every consumer occurrence</em> has its own captured edge to the
/// provider, not that the provider is referenced-by-someone.
/// </para>
/// <para>
/// A reference whose consumer and provider are the same unit is not a cross-unit obligation — the
/// symbol and its sole reference leave together when the unit is withdrawn — so it is satisfied
/// trivially.
/// </para>
/// </remarks>
public static class RecoveryCompletenessGate
{
    /// <summary>
    /// Checks that every reference in <paramref name="witness"/> has a captured, witnessable edge in
    /// <paramref name="graph"/>. Any reference without one is an orphan and makes the report incomplete.
    /// </summary>
    public static RecoveryCompletenessReport Check(
        RecoveryGraph graph,
        IReadOnlyCollection<WitnessedReference> witness)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(witness);

        var orphans = ImmutableArray.CreateBuilder<WitnessedReference>();
        foreach (var reference in witness)
        {
            // A unit referencing its own symbol strands nothing: the reference and the symbol are
            // withdrawn together.
            if (reference.Consumer == reference.Provider)
                continue;

            if (!HasCapturedWitnessableEdge(graph, reference))
                orphans.Add(reference);
        }

        return new RecoveryCompletenessReport { Checked = true, Orphans = orphans.ToImmutable() };
    }

    /// <summary>
    /// Whether the graph captured an edge from the reference's consumer to its provider whose kind is the
    /// <em>same</em> production-witnessable kind the witness observed. Matching the exact kind, not merely
    /// "some witnessable kind", is what keeps capture fidelity: a captured <see cref="RecoveryEdgeKind.HelperCall"/>
    /// must not vouch for a <see cref="RecoveryEdgeKind.PInvokeToWrapperSymbol"/> reference (or vice versa),
    /// and an <see cref="RecoveryEdgeKind.Unspecified"/> or semantic captured edge never satisfies a
    /// witnessable reference — the whole point is that the capture kept up with the witnessable output,
    /// kind for kind. The reference's own kind is re-checked for witnessability so a malformed witness
    /// bearing a non-witnessable kind can never be satisfied (it fails closed).
    /// </summary>
    private static bool HasCapturedWitnessableEdge(RecoveryGraph graph, WitnessedReference reference) =>
        RecoveryEdgeKinds.IsProductionWitnessable(reference.Kind)
        && graph.IncomingEdges(reference.Provider)
            .Any(e => e.Dependent == reference.Consumer && e.Kind == reference.Kind);
}
