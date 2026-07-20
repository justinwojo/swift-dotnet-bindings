// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using BindingsGeneration;

namespace BindingsGeneration.Diagnostics;

/// <summary>
/// Why a verify-recover run ended without converging to a compilable binding.
/// </summary>
public enum WrapperRecoveryFailureCause
{
    /// <summary>The run converged; not a failure.</summary>
    None,

    /// <summary>
    /// A round failed with no fresh leaf-scoped culprit left to withdraw — every unit it blamed is
    /// already denied, or it attributed the failure to no unit at all. No leaf withdrawal can move
    /// the compiler, and the only rung above a leaf needs the recovery graph this wave lacks, so it
    /// stops. This is the design's <c>D' == D</c> / no-progress escalation trigger.
    /// </summary>
    NoProgress,

    /// <summary>
    /// A culprit resolved to a scope coarser than a single member or accessor group — a shared
    /// helper, a managed conformance, a type layout. Withdrawing it soundly needs the dependency
    /// closure a populated recovery graph provides, which this wave does not have, so the module
    /// fails closed rather than withdraw a leaf and leave a dependent behind.
    /// </summary>
    RequiresGraphClosure,

    /// <summary>
    /// The failure was classified to a cause outside any declaration — a missing input module, a
    /// toolchain fault. No declaration withdrawal can fix it, so it is never a recovery input.
    /// </summary>
    InputConfiguration,

    /// <summary>An error could not be tied to any unit or classified; wave-1 fails closed rather than guess.</summary>
    Unattributable,

    /// <summary>Progress was being made, but the iteration cap was reached before the binding compiled clean.</summary>
    IterationCapExhausted,
}

/// <summary>
/// The result of a verify-recover run: whether it converged, the denylist it settled on, and — on
/// failure — why it stopped and which units blocked it.
/// </summary>
public sealed record WrapperRecoveryResult
{
    /// <summary>True when every promised slice compiled clean under the settled denylist.</summary>
    public required bool Converged { get; init; }

    /// <summary>The denylist of withdrawn units the run settled on (its final <c>D</c>).</summary>
    public required ImmutableArray<RecoveryUnitId> Denylist { get; init; }

    /// <summary>How many render→compile→attribute rounds ran.</summary>
    public required int Rounds { get; init; }

    /// <summary>Why the run failed, or <see cref="WrapperRecoveryFailureCause.None"/> when it converged.</summary>
    public required WrapperRecoveryFailureCause Cause { get; init; }

    /// <summary>
    /// The units that blocked recovery, when <see cref="Cause"/> is
    /// <see cref="WrapperRecoveryFailureCause.RequiresGraphClosure"/> — the coarse-scope culprits this
    /// wave cannot withdraw. Empty otherwise.
    /// </summary>
    public ImmutableArray<RecoveryUnitId> Blocking { get; init; } = ImmutableArray<RecoveryUnitId>.Empty;

    /// <summary>
    /// The subset of <see cref="Denylist"/> this run isolated by bounded bisection rather than by
    /// attribution — culprits the symbol-anchored provenance ladder could not place, found by a
    /// delta-debug search and confirmed by held-out probes. Reported so the caller marks these
    /// withdrawals with a distinct cause and a confidence no higher than Medium: a search-isolated root
    /// is less certain than an attributed one. Empty on the common path where attribution placed every
    /// culprit.
    /// </summary>
    public ImmutableArray<RecoveryUnitId> SearchIsolated { get; init; } = ImmutableArray<RecoveryUnitId>.Empty;
}

/// <summary>
/// One render→compile→attribute round for the verify-recover loop: renders every promised slice with
/// the current denylist applied through Gate 0, compiles them all, unions the diagnostics across
/// slices, and attributes the union to recovery units.
/// </summary>
/// <remarks>
/// <para>
/// The driver is the seam the pure controller drives — the controller owns the denylist and the
/// termination policy; the driver owns emission, the compiler subprocess, and attribution. Splitting
/// them this way is what lets the loop's non-negotiable properties (monotonic progress, one channel,
/// target-slice consistency, fail-closed escalation) be tested without a Swift toolchain.
/// </para>
/// <para>
/// Two invariants the driver must uphold for the controller's guarantees to hold:
/// the denylist it is handed is slice-agnostic, so a unit that failed on <em>any</em> required slice
/// must be rendered as withdrawn on <em>every</em> slice (target-slice consistency); and the returned
/// attribution must be over the union of all failing slices' diagnostics, not one slice's, so a
/// device-only failure is not lost when the simulator slice compiled clean.
/// </para>
/// </remarks>
public interface IWrapperRecoveryDriver
{
    /// <summary>
    /// Renders, compiles, and attributes one round under <paramref name="denylist"/>. Returns
    /// <c>null</c> when every promised slice compiled clean (converged); otherwise the attributed
    /// union of all failing slices' diagnostics.
    /// </summary>
    AttributionResult? RenderCompileAttribute(IReadOnlySet<RecoveryUnitId> denylist);

    /// <summary>
    /// Consulted when a round blames a culprit coarser than a leaf or accessor group — a scope no
    /// leaf withdrawal can clear. The driver decides, against the completeness-gated recovery graph it
    /// built during emission, whether the coarse withdrawal is <em>sound</em>: it may authorize only
    /// when every dependent-edge kind the coarse unit could carry is witnessable and witnessed, and it
    /// returns the full dependency closure to withdraw alongside it (which the controller adds to the
    /// denylist). An unauthorized verdict leaves the module failing closed exactly as before.
    /// </summary>
    /// <remarks>
    /// The default authorizes nothing: a driver with no populated, witness-complete recovery graph can
    /// never soundly widen past a leaf, so the loop's coarse-culprit behavior is unchanged until a
    /// driver overrides this seam. This is why wiring the seam is byte-neutral for every current driver
    /// — <see cref="InEmissionDriver"/> inherits the default and keeps failing coarse culprits closed
    /// with <see cref="WrapperRecoveryFailureCause.RequiresGraphClosure"/>.
    /// </remarks>
    /// <param name="blocking">The fresh coarse culprits this round could not withdraw as leaves.</param>
    /// <param name="denylist">The units already withdrawn, so the driver's closure excludes them.</param>
    CoarseWithdrawalAuthorization AuthorizeCoarseWithdrawal(
        IReadOnlyList<RecoveryUnitId> blocking, IReadOnlySet<RecoveryUnitId> denylist)
        => CoarseWithdrawalAuthorization.Unauthorized;

    /// <summary>
    /// Consulted when a round's failure could not be attributed to any recovery unit — the point at which
    /// the loop would otherwise fail closed as <see cref="WrapperRecoveryFailureCause.Unattributable"/>.
    /// The driver may run a bounded, dependency-aware bisection over its withdrawable leaves to isolate a
    /// culprit attribution could not place, confirmed by held-out probes and capped at a single-digit
    /// probe budget. It returns the confirmed culprit set (which the controller withdraws, records as
    /// search-isolated, and iterates), or a decline that leaves the module failing closed exactly as
    /// before.
    /// </summary>
    /// <remarks>
    /// The default declines: a driver with no candidate pool and no render→compile probe can never
    /// confirm an isolation, so the loop's unattributable behaviour is unchanged until a driver overrides
    /// this seam. This is why wiring the seam is byte-neutral for every current test driver — only
    /// <see cref="InEmissionDriver"/>, which can render and compile, supplies a real search.
    /// </remarks>
    /// <param name="denylist">The units already withdrawn, so the search's candidate pool excludes them.</param>
    BisectionOutcome AttemptBisection(IReadOnlySet<RecoveryUnitId> denylist)
        => BisectionOutcome.Declined();
}

/// <summary>
/// A driver's verdict on whether a round's coarse culprits may be withdrawn, and if so, the dependency
/// closure to withdraw with them. Authorization is all-or-nothing per round: either the driver's graph
/// proves the whole closure sound to withdraw, or the module fails closed — a partially-authorized
/// closure would strand a dependent, which is the outcome the whole gate exists to prevent.
/// </summary>
public sealed record CoarseWithdrawalAuthorization
{
    /// <summary>The default: no coarse withdrawal is authorized, so the module fails closed.</summary>
    public static readonly CoarseWithdrawalAuthorization Unauthorized = new()
    {
        Authorized = false,
        Closure = ImmutableArray<RecoveryUnitId>.Empty,
    };

    /// <summary>True when the driver's graph proved the coarse withdrawal sound.</summary>
    public required bool Authorized { get; init; }

    /// <summary>
    /// The full dependency closure to withdraw — the coarse culprits plus every unit that must go with
    /// them. The controller adds these to the denylist; a closure that adds no fresh unit is treated as
    /// no progress and fails closed, so it cannot spin.
    /// </summary>
    public required ImmutableArray<RecoveryUnitId> Closure { get; init; }

    /// <summary>Builds an authorized verdict over <paramref name="closure"/>.</summary>
    public static CoarseWithdrawalAuthorization Authorize(IEnumerable<RecoveryUnitId> closure) =>
        new() { Authorized = true, Closure = closure.ToImmutableArray() };
}

/// <summary>
/// The pure verify-recover loop: render all promised slices under a denylist, compile every slice,
/// attribute the union of failures to recovery units, withdraw the leaf-scoped culprits, and repeat
/// until the binding compiles clean or no sound leaf-scoped withdrawal remains.
/// </summary>
/// <remarks>
/// <para>
/// This is the wave-1 controller: it recovers only <see cref="RecoveryScope.LeafApi"/> and
/// <see cref="RecoveryScope.AccessorGroup"/> culprits — the two scopes whose withdrawal is provably
/// ABI-neutral (removing a member or an accessor pair shifts no other surface's layout). Every other
/// scope — a shared helper, a managed conformance, a type's representation — needs the dependency
/// closure a populated recovery graph supplies before it can be withdrawn without stranding a
/// dependent; until that graph is wired, a coarse-scope culprit fails the module closed rather than
/// ship a binding that compiles and is wrong at runtime.
/// </para>
/// <para>
/// The controller owns exactly one removal channel: the denylist of recovery units it accumulates and
/// hands back to the driver. It never strips text, never edits a slice, never removes a unit any
/// other way. A denied unit is never re-enabled within a run, and every round either withdraws at
/// least one new unit or terminates — so the loop cannot spin.
/// </para>
/// </remarks>
public static class WrapperRecoveryController
{
    /// <summary>
    /// The default cap on render→compile→attribute rounds. A localized failure clears in one or two
    /// rounds; a run still failing after this many is escalating without converging, so the floor
    /// (module failure) is the sound outcome rather than an unbounded retry.
    /// </summary>
    public const int DefaultIterationCap = 4;

    /// <summary>True when a unit's scope can be withdrawn soundly at this wave — member or accessor group.</summary>
    public static bool IsLeafRecoverable(RecoveryScope scope) =>
        scope is RecoveryScope.LeafApi or RecoveryScope.AccessorGroup;

    /// <summary>
    /// Runs the verify-recover loop, driving <paramref name="driver"/> until it reports a clean
    /// compile or no sound leaf-scoped withdrawal remains.
    /// </summary>
    /// <param name="driver">The render→compile→attribute seam.</param>
    /// <param name="iterationCap">Maximum rounds before the module fails closed. Must be ≥ 1.</param>
    public static WrapperRecoveryResult Run(IWrapperRecoveryDriver driver, int iterationCap = DefaultIterationCap)
    {
        ArgumentNullException.ThrowIfNull(driver);
        if (iterationCap < 1)
            throw new ArgumentOutOfRangeException(nameof(iterationCap), iterationCap, "Iteration cap must be at least 1.");

        // The denylist is keyed on the recovery unit alone — never on a slice — so a unit withdrawn
        // because it failed on one slice is withdrawn on every slice. That set identity IS the
        // target-slice-consistency guarantee; there is no per-slice denylist to drift out of sync.
        var denylist = new HashSet<RecoveryUnitId>();
        var denylistOrder = ImmutableArray.CreateBuilder<RecoveryUnitId>();
        var searchIsolated = ImmutableArray.CreateBuilder<RecoveryUnitId>();

        for (int round = 1; round <= iterationCap; round++)
        {
            var attribution = driver.RenderCompileAttribute(denylist);
            if (attribution is null)
                return Converged(denylistOrder, round, searchIsolated);

            // Fail-closed *nature* checks. These fire when the failing union merely CONTAINS such an
            // error — even alongside attributed leaf culprits — because wave-1 never partially
            // recovers past a failure no leaf withdrawal can clear. A global classification (missing
            // module, toolchain fault) is not a declaration's fault; an unplaceable error would still
            // fail the compile after every leaf we could withdraw. In both cases the leaf errors
            // beside them are most likely cascades of that same root, so withdrawing those leaves is
            // both futile and unsound (it would tombstone healthy members and still fail).
            if (HasGlobalInputError(attribution))
                return Failed(denylistOrder, round, WrapperRecoveryFailureCause.InputConfiguration, searchIsolated);

            if (attribution.HasUnattributedError)
            {
                // Attribution named no unit for this failure — the point the loop would otherwise fail
                // closed. Before it does, consult the driver's bounded bisection fallback: a delta-debug
                // over withdrawable leaves that can isolate a culprit the provenance ladder could not
                // place, confirmed by held-out probes and capped at a single-digit probe budget. The
                // default driver declines (no candidate pool), so this is the fail-closed path unchanged
                // for every driver but the production one. A confirmed isolation is withdrawn and recorded
                // as search-isolated so the report marks it distinctly and at a confidence no higher than
                // Medium — a searched root is less certain than an attributed one. The same monotonic
                // progress guard the coarse path uses applies: an isolation that adds no fresh unit fails
                // closed rather than spin.
                var bisection = driver.AttemptBisection(denylist);
                var addedIsolated = false;
                if (bisection.DidIsolate)
                {
                    foreach (var unit in bisection.Isolated)
                    {
                        if (denylist.Add(unit))
                        {
                            denylistOrder.Add(unit);
                            searchIsolated.Add(unit);
                            addedIsolated = true;
                        }
                    }
                }

                if (!addedIsolated)
                    return Failed(denylistOrder, round, WrapperRecoveryFailureCause.Unattributable, searchIsolated);

                continue;
            }

            // Progress is measured by fresh (not-yet-denied) culprits, never by the message
            // fingerprint. A swiftc cascade legitimately reuses identical diagnostic text across
            // members ("cannot find type 'X' in scope"), so a repeated fingerprint while a NEW leaf
            // remains withdrawable is a cascade to pursue — disabling that new unit IS the monotonic
            // step — not a reason to stop. Terminating on a repeated fingerprint here would collapse
            // exactly that common staged-recovery shape.
            var fresh = attribution.Culprits.Where(u => !denylist.Contains(u)).ToArray();

            // Any fresh culprit coarser than a leaf/accessor cannot be withdrawn as a leaf. Consult the
            // driver's authorization seam — which decides, against its completeness-gated recovery
            // graph, whether the coarse withdrawal is sound and, if so, returns the dependency closure
            // to withdraw with it. The default driver has no graph and authorizes nothing, so this is
            // the fail-closed path unchanged: without an authorized closure the module fails closed
            // rather than poison the coarse unit's declaration as if it were a leaf, which would strand
            // its dependents (the compile-clean/runtime-wrong outcome). Checked before any leaf
            // withdrawal so a coarse culprit is never disabled as if it were a leaf.
            var blocking = fresh.Where(u => !IsLeafRecoverable(u.Scope)).ToArray();
            if (blocking.Length > 0)
            {
                var authorization = driver.AuthorizeCoarseWithdrawal(blocking, denylist);

                // An authorized withdrawal only counts if it makes monotonic progress: an empty or
                // fully-redundant closure would let the same coarse culprit re-block every round, so it
                // fails closed exactly as an unauthorized culprit does. This keeps the loop's
                // termination guarantee intact regardless of what a driver's authorizer returns.
                var addedClosure = false;
                if (authorization.Authorized)
                {
                    foreach (var unit in authorization.Closure)
                    {
                        if (denylist.Add(unit))
                        {
                            denylistOrder.Add(unit);
                            addedClosure = true;
                        }
                    }
                }

                if (!addedClosure)
                    return Blocked(denylistOrder, round, blocking, searchIsolated);

                continue;
            }

            // Fresh leaves to withdraw: disable them and iterate. This is the monotonic step, and it
            // runs regardless of whether the fingerprint repeated — withdrawing a new unit is, by
            // definition, progress.
            if (fresh.Length > 0)
            {
                foreach (var unit in fresh)
                {
                    if (denylist.Add(unit))
                        denylistOrder.Add(unit);
                }
                continue;
            }

            // No fresh leaf withdrawal remains and the compile still fails (the design's D' == D /
            // no-progress condition): whether the round attributed nothing or re-blamed only
            // already-denied units, no leaf recovery can move the compiler. The one rung above a leaf
            // needs the recovery graph this wave lacks, so it fails closed at the module floor.
            return Failed(denylistOrder, round, WrapperRecoveryFailureCause.NoProgress, searchIsolated);
        }

        // Still making progress (each round withdrew something new) but out of rounds: the floor.
        return Failed(denylistOrder, iterationCap, WrapperRecoveryFailureCause.IterationCapExhausted, searchIsolated);
    }

    /// <summary>
    /// True when the round's failure <em>contains</em> a global classification error — the shape of a
    /// missing input module or a toolchain fault. Every <see cref="AttributionKind.Classification"/>
    /// owner (input configuration, Swift/.NET toolchain, environment) names a cause outside any
    /// declaration, so no leaf withdrawal can fix it; recovery must fail closed rather than withdraw
    /// leaves whose errors are, in a mixed union, most likely cascades of that same global root. The
    /// <see cref="WrapperRecoveryFailureCause.InputConfiguration"/> cause is the umbrella for all of
    /// them — the module fails closed regardless of which non-declaration owner it was.
    /// </summary>
    private static bool HasGlobalInputError(AttributionResult attribution) =>
        attribution.Diagnostics.Any(d =>
            d.Kind == AttributionKind.Classification && d.Diagnostic.IsError);

    private static WrapperRecoveryResult Converged(
        ImmutableArray<RecoveryUnitId>.Builder denylist,
        int rounds,
        ImmutableArray<RecoveryUnitId>.Builder searchIsolated) =>
        new()
        {
            Converged = true,
            Denylist = denylist.ToImmutable(),
            Rounds = rounds,
            Cause = WrapperRecoveryFailureCause.None,
            SearchIsolated = searchIsolated.ToImmutable(),
        };

    private static WrapperRecoveryResult Failed(
        ImmutableArray<RecoveryUnitId>.Builder denylist,
        int rounds,
        WrapperRecoveryFailureCause cause,
        ImmutableArray<RecoveryUnitId>.Builder searchIsolated) =>
        new()
        {
            Converged = false,
            Denylist = denylist.ToImmutable(),
            Rounds = rounds,
            Cause = cause,
            SearchIsolated = searchIsolated.ToImmutable(),
        };

    private static WrapperRecoveryResult Blocked(
        ImmutableArray<RecoveryUnitId>.Builder denylist,
        int rounds,
        IEnumerable<RecoveryUnitId> blocking,
        ImmutableArray<RecoveryUnitId>.Builder searchIsolated) =>
        new()
        {
            Converged = false,
            Denylist = denylist.ToImmutable(),
            Rounds = rounds,
            Cause = WrapperRecoveryFailureCause.RequiresGraphClosure,
            Blocking = blocking.ToImmutableArray(),
            SearchIsolated = searchIsolated.ToImmutable(),
        };
}
