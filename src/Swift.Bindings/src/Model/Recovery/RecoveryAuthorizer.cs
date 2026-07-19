// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace BindingsGeneration;

/// <summary>Why a coarse withdrawal was not authorized.</summary>
public enum RecoveryAuthorizationDenial
{
    /// <summary>No denial — the withdrawal is authorized.</summary>
    None,

    /// <summary>
    /// The unit's scope can have a dependent of a kind no witness available this wave can prove complete
    /// (a semantic conformance/layout obligation, or a Roslyn-only C# reference). The scope stays
    /// fail-closed regardless of what the captured graph happens to contain.
    /// </summary>
    NotWitnessableScope,

    /// <summary>
    /// The capture is incomplete: the settled render references a unit the graph did not model. No
    /// coarse withdrawal may be authorized against an incomplete graph, because the missing edge is
    /// exactly the dependent a withdrawal must guard against.
    /// </summary>
    WitnessIncomplete,

    /// <summary>
    /// The scope and completeness gates passed, but the pure policy still refuses — a retained
    /// dependent or a retained parent layout. The graph is trusted here, so this is a real obstruction.
    /// </summary>
    PolicyBlocked,

    /// <summary>
    /// An escalation walk could not close into a sound withdrawal set (it reached the module or hit a
    /// unit it could not reason about), so there is no coarse set to authorize.
    /// </summary>
    EscalationNotClosed,
}

/// <summary>The answer to "may this coarse withdrawal be authorized", with the reason attached.</summary>
public readonly record struct RecoveryAuthorization
{
    /// <summary>Whether the withdrawal is authorized.</summary>
    public bool IsAuthorized => Denial == RecoveryAuthorizationDenial.None;

    /// <summary>Why it was refused; <see cref="RecoveryAuthorizationDenial.None"/> when authorized.</summary>
    public RecoveryAuthorizationDenial Denial { get; init; }

    /// <summary>
    /// The pure-policy verdict, when the scope and completeness gates passed and the decision was
    /// delegated to <see cref="RecoveryPolicy"/>. Default otherwise.
    /// </summary>
    public RecoveryVerdict PolicyVerdict { get; init; }

    /// <summary>An authorized result.</summary>
    public static RecoveryAuthorization Authorized(RecoveryVerdict verdict = default) =>
        new() { Denial = RecoveryAuthorizationDenial.None, PolicyVerdict = verdict };

    /// <summary>A refused result.</summary>
    public static RecoveryAuthorization Denied(
        RecoveryAuthorizationDenial denial,
        RecoveryVerdict verdict = default) =>
        new() { Denial = denial, PolicyVerdict = verdict };
}

/// <summary>The authorized outcome of an escalation walk — the closure to withdraw, or the refusal.</summary>
public readonly record struct RecoveryAuthorizedEscalation
{
    /// <summary>Whether a sound, fully-witnessable closure was authorized for withdrawal.</summary>
    public bool IsAuthorized => Denial == RecoveryAuthorizationDenial.None;

    /// <summary>Why it was refused; <see cref="RecoveryAuthorizationDenial.None"/> when authorized.</summary>
    public RecoveryAuthorizationDenial Denial { get; init; }

    /// <summary>
    /// The full set of units to withdraw together when authorized — the escalation closure. Empty on
    /// refusal.
    /// </summary>
    public ImmutableHashSet<RecoveryUnitId> Withdrawn { get; init; }

    /// <summary>
    /// The first closure member whose scope was not witnessable, when
    /// <see cref="Denial"/> is <see cref="RecoveryAuthorizationDenial.NotWitnessableScope"/>. Null
    /// otherwise.
    /// </summary>
    public RecoveryUnitId? UnwitnessableMember { get; init; }

    /// <summary>An authorized closure.</summary>
    public static RecoveryAuthorizedEscalation Authorized(ImmutableHashSet<RecoveryUnitId> withdrawn) =>
        new() { Denial = RecoveryAuthorizationDenial.None, Withdrawn = withdrawn };

    /// <summary>A refused closure.</summary>
    public static RecoveryAuthorizedEscalation Denied(
        RecoveryAuthorizationDenial denial,
        RecoveryUnitId? unwitnessableMember = null) =>
        new()
        {
            Denial = denial,
            Withdrawn = ImmutableHashSet<RecoveryUnitId>.Empty,
            UnwitnessableMember = unwitnessableMember,
        };
}

/// <summary>
/// The authorized entry point in front of the pure <see cref="RecoveryPolicy"/>: a coarse withdrawal is
/// authorized only after the scope gate and the capture-completeness gate both pass, and only then is
/// the decision delegated to <see cref="RecoveryPolicy.SafeToDrop"/> / <see cref="RecoveryPolicy.Escalate"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a <em>separate</em> gate rather than a change to <see cref="RecoveryPolicy"/> because the
/// policy must stay pure — its callers test it against deliberately partial graphs, and baking a
/// completeness requirement into it would break that contract and, worse, would make the false-safe
/// hole a property of the test-only paths as well. Keeping the gate here means <see cref="RecoveryPolicy"/>
/// answers "is this sound <em>given a complete graph</em>", and this type answers "is the graph complete
/// enough to trust that answer".
/// </para>
/// <para>
/// The ordering is deliberate: scope gate, then completeness gate, then policy. A non-witnessable scope
/// is refused before completeness even matters, because no witness can ever make it authorizable; an
/// incomplete graph is refused before the policy is consulted, because the policy's "no retained
/// dependent" answer is only as trustworthy as the graph it reads.
/// </para>
/// </remarks>
public static class RecoveryAuthorizer
{
    /// <summary>
    /// Whether <paramref name="unit"/> may be withdrawn while <paramref name="retained"/> stays, given
    /// the completeness of the capture. Refuses a non-witnessable scope, refuses an incomplete graph,
    /// then delegates to <see cref="RecoveryPolicy.SafeToDrop"/>.
    /// </summary>
    public static RecoveryAuthorization SafeToDrop(
        RecoveryGraph graph,
        RecoveryCompletenessReport completeness,
        RecoveryUnitId unit,
        IReadOnlySet<RecoveryUnitId> retained)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(retained);

        if (!RecoveryEdgeKinds.IsCoarseWithdrawalWitnessable(unit.Scope))
            return RecoveryAuthorization.Denied(RecoveryAuthorizationDenial.NotWitnessableScope);

        if (!completeness.IsComplete)
            return RecoveryAuthorization.Denied(RecoveryAuthorizationDenial.WitnessIncomplete);

        var verdict = RecoveryPolicy.SafeToDrop(graph, unit, retained);
        return verdict.IsSafe
            ? RecoveryAuthorization.Authorized(verdict)
            : RecoveryAuthorization.Denied(RecoveryAuthorizationDenial.PolicyBlocked, verdict);
    }

    /// <summary>
    /// Grows <paramref name="seeds"/> into the smallest sound withdrawal set and authorizes it only when
    /// the walk closed, the graph is complete, and <em>every</em> member of the closure is itself a
    /// witnessable scope. The whole-closure scope check is what stops an authorizable seed from dragging
    /// a non-authorizable dependent (a reverse conformance, say) out through the back door: the seed
    /// being witnessable is not enough — everything the withdrawal actually removes must be.
    /// </summary>
    public static RecoveryAuthorizedEscalation Escalate(
        RecoveryGraph graph,
        RecoveryCompletenessReport completeness,
        IEnumerable<RecoveryUnitId> seeds)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(seeds);

        if (!completeness.IsComplete)
            return RecoveryAuthorizedEscalation.Denied(RecoveryAuthorizationDenial.WitnessIncomplete);

        var result = RecoveryPolicy.Escalate(graph, seeds);
        if (!result.IsUsable)
            return RecoveryAuthorizedEscalation.Denied(RecoveryAuthorizationDenial.EscalationNotClosed);

        foreach (var member in result.Withdrawn)
        {
            if (!RecoveryEdgeKinds.IsCoarseWithdrawalWitnessable(member.Scope))
                return RecoveryAuthorizedEscalation.Denied(
                    RecoveryAuthorizationDenial.NotWitnessableScope, member);
        }

        return RecoveryAuthorizedEscalation.Authorized(result.Withdrawn);
    }
}
