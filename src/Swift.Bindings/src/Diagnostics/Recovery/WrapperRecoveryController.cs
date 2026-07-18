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

        for (int round = 1; round <= iterationCap; round++)
        {
            var attribution = driver.RenderCompileAttribute(denylist);
            if (attribution is null)
                return Converged(denylistOrder, round);

            // Fail-closed *nature* checks. These fire when the failing union merely CONTAINS such an
            // error — even alongside attributed leaf culprits — because wave-1 never partially
            // recovers past a failure no leaf withdrawal can clear. A global classification (missing
            // module, toolchain fault) is not a declaration's fault; an unplaceable error would still
            // fail the compile after every leaf we could withdraw. In both cases the leaf errors
            // beside them are most likely cascades of that same root, so withdrawing those leaves is
            // both futile and unsound (it would tombstone healthy members and still fail).
            if (HasGlobalInputError(attribution))
                return Failed(denylistOrder, round, WrapperRecoveryFailureCause.InputConfiguration);

            if (attribution.HasUnattributedError)
                return Failed(denylistOrder, round, WrapperRecoveryFailureCause.Unattributable);

            // Progress is measured by fresh (not-yet-denied) culprits, never by the message
            // fingerprint. A swiftc cascade legitimately reuses identical diagnostic text across
            // members ("cannot find type 'X' in scope"), so a repeated fingerprint while a NEW leaf
            // remains withdrawable is a cascade to pursue — disabling that new unit IS the monotonic
            // step — not a reason to stop. Terminating on a repeated fingerprint here would collapse
            // exactly that common staged-recovery shape.
            var fresh = attribution.Culprits.Where(u => !denylist.Contains(u)).ToArray();

            // Any fresh culprit coarser than a leaf/accessor cannot be withdrawn soundly without the
            // dependency closure a populated recovery graph supplies. Fail the module closed — never
            // poison the coarse unit's declaration as if it were a leaf, which would strand its
            // dependents (the compile-clean/runtime-wrong outcome). Checked before any withdrawal so
            // a coarse culprit is never disabled as if it were a leaf.
            var blocking = fresh.Where(u => !IsLeafRecoverable(u.Scope)).ToArray();
            if (blocking.Length > 0)
                return Blocked(denylistOrder, round, blocking);

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
            return Failed(denylistOrder, round, WrapperRecoveryFailureCause.NoProgress);
        }

        // Still making progress (each round withdrew something new) but out of rounds: the floor.
        return Failed(denylistOrder, iterationCap, WrapperRecoveryFailureCause.IterationCapExhausted);
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
        ImmutableArray<RecoveryUnitId>.Builder denylist, int rounds) =>
        new()
        {
            Converged = true,
            Denylist = denylist.ToImmutable(),
            Rounds = rounds,
            Cause = WrapperRecoveryFailureCause.None,
        };

    private static WrapperRecoveryResult Failed(
        ImmutableArray<RecoveryUnitId>.Builder denylist, int rounds, WrapperRecoveryFailureCause cause) =>
        new()
        {
            Converged = false,
            Denylist = denylist.ToImmutable(),
            Rounds = rounds,
            Cause = cause,
        };

    private static WrapperRecoveryResult Blocked(
        ImmutableArray<RecoveryUnitId>.Builder denylist, int rounds, IEnumerable<RecoveryUnitId> blocking) =>
        new()
        {
            Converged = false,
            Denylist = denylist.ToImmutable(),
            Rounds = rounds,
            Cause = WrapperRecoveryFailureCause.RequiresGraphClosure,
            Blocking = blocking.ToImmutableArray(),
        };
}
