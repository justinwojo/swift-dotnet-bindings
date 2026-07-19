// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>
/// The kind of cross-unit dependency one <c>Requires</c> edge records — why a dependent unit cannot
/// remain once its dependency is withdrawn.
/// </summary>
/// <remarks>
/// <para>
/// The kind exists for one reason: <em>witnessability</em>. A withdrawal is only sound when the set
/// of units depending on the withdrawn one is known <em>complete</em>. An edge whose dependency
/// leaves a distinct textual reference in the settled render (a wrapper symbol a P/Invoke names, a
/// shared helper a body calls) can be cross-checked against that render — so "we captured every such
/// edge" is provable. An edge with no distinct rendered reference (a retained conformance's
/// obligation on a member witness, a stored field's layout contribution, a shared initializer's
/// ordering) cannot be cross-checked against anything, so its completeness can never be proven and a
/// unit that could have one must stay fail-closed.
/// </para>
/// <para>
/// This is the wave-1 soundness bar restated as a type: an edge kind you cannot witness is an edge
/// kind you cannot authorize a coarse withdrawal over.
/// </para>
/// </remarks>
public enum RecoveryEdgeKind
{
    /// <summary>
    /// The default for an edge recorded without a stated kind. Treated as
    /// <see cref="RecoveryEdgeWitnessability.Semantic"/> — the conservative choice, so an unkinded
    /// edge never authorizes a coarse withdrawal.
    /// </summary>
    Unspecified,

    /// <summary>
    /// A P/Invoke's <c>EntryPoint</c> names the wrapper <c>SBW_*</c>/<c>@_cdecl</c> symbol the
    /// dependency owns. Witnessable against the settled render's wrapper-symbol table.
    /// </summary>
    PInvokeToWrapperSymbol,

    /// <summary>
    /// An emitted body calls a shared helper by the helper's own symbol (the UTF-8 slice bridge, the
    /// error-mint helper, the EveryProtocol carrier). Witnessable against the wrapper-symbol table.
    /// </summary>
    HelperCall,

    /// <summary>
    /// Emitted C# names another emitted C# type in a retained signature. Witnessable only through a
    /// Roslyn syntax tree of the emitted C#, which the production path does not currently materialize
    /// (only the unwired probe does) — so it is not production-witnessable this wave.
    /// </summary>
    CSharpTypeReference,

    /// <summary>
    /// A retained conformance's obligation on a member witness. No distinct rendered reference — the
    /// obligation is carried by the conformance's existence, not by a nameable symbol. Not witnessable.
    /// </summary>
    ConformanceObligation,

    /// <summary>
    /// A member contributes to a parent type's memory layout. No distinct rendered reference; the
    /// contribution is a property of the bytes, not of any name. Not witnessable.
    /// </summary>
    LayoutContribution,

    /// <summary>
    /// A statement in the shared module initializer depends on another's ordering. No distinct
    /// rendered reference that survives independent of statement order. Not witnessable.
    /// </summary>
    SharedInitializerOrdering,
}

/// <summary>
/// How the completeness of a <see cref="RecoveryEdgeKind"/>'s captured edges can be proven against
/// the settled render.
/// </summary>
public enum RecoveryEdgeWitnessability
{
    /// <summary>Provable against the wrapper-symbol table (production-available).</summary>
    Symbol,

    /// <summary>Provable only against a Roslyn syntax tree of the emitted C# (not production-available this wave).</summary>
    CSharpType,

    /// <summary>Not provable against any rendered reference — completeness can never be established.</summary>
    Semantic,
}

/// <summary>
/// The witnessability classification of every <see cref="RecoveryEdgeKind"/>, and the per-scope rule
/// for whether a unit's <em>possible</em> incoming edge kinds are all witnessable by a witness that
/// exists this wave — the gate on whether a coarse withdrawal may be authorized at all.
/// </summary>
public static class RecoveryEdgeKinds
{
    /// <summary>How this edge kind's completeness can be proven.</summary>
    public static RecoveryEdgeWitnessability Witnessability(RecoveryEdgeKind kind) => kind switch
    {
        RecoveryEdgeKind.PInvokeToWrapperSymbol => RecoveryEdgeWitnessability.Symbol,
        RecoveryEdgeKind.HelperCall => RecoveryEdgeWitnessability.Symbol,
        RecoveryEdgeKind.CSharpTypeReference => RecoveryEdgeWitnessability.CSharpType,
        RecoveryEdgeKind.ConformanceObligation => RecoveryEdgeWitnessability.Semantic,
        RecoveryEdgeKind.LayoutContribution => RecoveryEdgeWitnessability.Semantic,
        RecoveryEdgeKind.SharedInitializerOrdering => RecoveryEdgeWitnessability.Semantic,
        RecoveryEdgeKind.Unspecified => RecoveryEdgeWitnessability.Semantic,
        _ => RecoveryEdgeWitnessability.Semantic,
    };

    /// <summary>
    /// The witnesses that exist in the production path this wave. Only the wrapper-symbol table is
    /// materialized after emission; the C# Roslyn tree is not, so a
    /// <see cref="RecoveryEdgeWitnessability.CSharpType"/> edge cannot yet be proven complete in
    /// production and its scope stays fail-closed.
    /// </summary>
    public static bool IsProductionWitnessable(RecoveryEdgeKind kind) =>
        Witnessability(kind) == RecoveryEdgeWitnessability.Symbol;

    /// <summary>
    /// The kinds of edge that can point <em>into</em> (depend on) a unit of the given scope — the
    /// dependents it could strand if withdrawn. This is the "possible", not the "captured", set: the
    /// danger a withdrawal must guard against is a dependent that was never modelled, so the
    /// authorization test is over what a scope <em>could</em> have, not over what happens to be in the
    /// graph.
    /// </summary>
    public static ImmutableArray<RecoveryEdgeKind> PossibleIncomingKinds(RecoveryScope scope) => scope switch
    {
        // A leaf callable and an accessor group are the two scopes this coarse-edge gate authorizes. Their
        // render-emergent dependents — another unit's P/Invoke or helper body that names this leaf's
        // wrapper symbol — are witnessable (a PInvokeToWrapperSymbol / HelperCall reference the completeness
        // gate proves complete against the settled render). The one non-witnessable way a leaf can be
        // depended on is a retained conformance whose vtable slot this leaf witnesses — a
        // ConformanceObligation with no rendered reference. That edge is STRUCTURAL, not render-emergent: it
        // is knowable from the type model at graph-build time, so — unlike a symbol edge, which is only
        // discovered as the entry point is chosen during emission — it is the builder's responsibility to
        // model, and the pure policy then blocks the withdrawal on it (graph.Provides) whenever the edge is
        // present and its conformer is retained. The empty set here therefore encodes a PRECONDITION, not
        // the (false) claim that a leaf has no incoming edge: before the authorizer is wired to authorize
        // leaf or accessor withdrawal in production, the builder must be proven to model every structural
        // incoming edge, or a missing ConformanceObligation into a leaf would be invisible to the
        // symbols-only completeness witness and the withdrawal would strand the conformance. This wave the
        // authorizer has no production caller and the loop withdraws leaves through the wave-1
        // leaf-recoverable path, so the precondition is documented, not yet load-bearing.
        RecoveryScope.LeafApi => ImmutableArray<RecoveryEdgeKind>.Empty,
        RecoveryScope.AccessorGroup => ImmutableArray<RecoveryEdgeKind>.Empty,

        // A shared helper bundle is NOT uniformly witnessable-incoming, so it is not authorizable this
        // wave. Most helpers it bundles (the UTF-8 slice bridge, the error-mint helper, the closure
        // context helper) are pure symbol callees — their dependents call them by symbol, a witnessable
        // HelperCall / P/Invoke-to-wrapper-symbol reference. But the same scope also classifies the
        // NativeAOT eager-registration helper, and a retained conformer depends on that registration
        // *semantically*: the rendered text points the wrong way (the initializer names the type; the
        // type never names the initializer), and dropping the registration leaves a compile-clean binding
        // whose conformance fails to resolve at runtime on device. That is a ConformanceObligation /
        // SharedInitializerOrdering incoming edge with no rendered reference to witness, so the bundle
        // scope as classified carries a semantic possible-incoming and stays fail-closed. (Splitting the
        // pure-callee helpers out into their own authorizable scope is a lattice change for a later wave;
        // until then this scope is closed.)
        RecoveryScope.SharedHelperBundle => ImmutableArray.Create(
            RecoveryEdgeKind.HelperCall,
            RecoveryEdgeKind.PInvokeToWrapperSymbol,
            RecoveryEdgeKind.ConformanceObligation,
            RecoveryEdgeKind.SharedInitializerOrdering),

        // A forward interface's dependents are retained C# signatures that name it — witnessable only
        // through the emitted-C# Roslyn tree, which production does not materialize this wave.
        RecoveryScope.ForwardProtocolView => ImmutableArray.Create(RecoveryEdgeKind.CSharpTypeReference),

        // A reverse conformance's dependents hold an obligation on its member witnesses — a semantic
        // dependency with no nameable rendered reference.
        RecoveryScope.ManagedProtocolConformance => ImmutableArray.Create(RecoveryEdgeKind.ConformanceObligation),

        // A conformance edge propagates to every retained API whose signature depends on the
        // conformance — a C#-type reference, Roslyn-only.
        RecoveryScope.ConformanceEdge => ImmutableArray.Create(RecoveryEdgeKind.CSharpTypeReference),

        // A representation's dependents are every later field and the type's total size — a layout
        // contribution, never witnessable, which is why it is never withdrawable in isolation.
        RecoveryScope.TypeRepresentation => ImmutableArray.Create(RecoveryEdgeKind.LayoutContribution),

        // A whole type can be depended on every way at once; it is authorized (or not) through the
        // separate whole-type path, never through this coarse-edge gate.
        RecoveryScope.TypeSurface => ImmutableArray.Create(
            RecoveryEdgeKind.CSharpTypeReference,
            RecoveryEdgeKind.ConformanceObligation,
            RecoveryEdgeKind.LayoutContribution),

        // The module is the floor; nothing coarser depends on it.
        RecoveryScope.Module => ImmutableArray<RecoveryEdgeKind>.Empty,

        _ => ImmutableArray.Create(RecoveryEdgeKind.Unspecified),
    };

    /// <summary>
    /// Whether a unit of this scope is eligible for a coarse-withdrawal authorization at all: true only
    /// when every kind of dependent it could have is provable complete by a witness that exists this
    /// wave. A scope with any semantic or not-yet-witnessable possible incoming kind stays fail-closed
    /// regardless of what the graph happens to contain — the missing dependent it must guard against is
    /// exactly the one the graph would not show.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This wave the only scopes that pass are <see cref="RecoveryScope.LeafApi"/> and
    /// <see cref="RecoveryScope.AccessorGroup"/> — the two the loop already withdraws without the graph.
    /// Every coarser scope carries at least one non-witnessable or not-yet-production-witnessable possible
    /// incoming kind and stays fail-closed: the shared-helper bundle because it also classifies the
    /// NativeAOT registration helper (a semantic conformance/init obligation); the conformance scopes
    /// because their dependents are Roslyn-only C# references or bare conformance obligations; the
    /// representation because its dependents are a layout contribution.
    /// </para>
    /// <para>
    /// The Module and TypeSurface scopes are handled by dedicated paths (module = the floor;
    /// type surface = the existing whole-type withdrawal keyed on the bare declaration), so they are
    /// deliberately not authorizable through this coarse-edge gate even though their withdrawal can be
    /// sound by other means.
    /// </para>
    /// </remarks>
    public static bool IsCoarseWithdrawalWitnessable(RecoveryScope scope)
    {
        // The module is the escalation terminus (reaching it is failure, never a coarse degrade) and the
        // whole type is authorized through the dedicated whole-type path — neither is ever authorized
        // through this coarse-edge gate, regardless of how its possible-incoming set happens to compute.
        if (scope is RecoveryScope.Module or RecoveryScope.TypeSurface)
            return false;

        return PossibleIncomingKinds(scope).All(IsProductionWitnessable);
    }
}
