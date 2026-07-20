// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

namespace BindingsGeneration;

/// <summary>
/// Builds the Gate-0 seed for a wrapper verify-recover round: turns the controller's denylist of
/// recovery units into the <see cref="EmitterPoisonList"/> a re-render denies up front, so a unit the
/// Swift wrapper compile could not accept is never emitted on the next attempt.
/// </summary>
/// <remarks>
/// <para>
/// The seed is the one channel by which a withdrawn unit leaves the binding. Each denied unit flows
/// through the ordinary skip machinery — the <c>// Unsupported:</c> tombstone and the skip report row
/// — exactly as any unsupported member always has, so a contained withdrawal is indistinguishable in
/// the output from a member the library never had.
/// </para>
/// <para>
/// The records are marked <see cref="EmitterFaultOrigin.RecoveryWithdrawal"/> so their tombstones tell
/// the truth: the unit was withdrawn to make the wrapper compile, not that the emitter threw on it.
/// On the default (unauthorized) driver path the controller hands only leaf and accessor-group units to
/// a round — a coarse culprit fails the module closed before it could reach here — so every seed record
/// is a terminal withdrawal with no escalation rung. A coarse unit reaches this seed only when a driver's
/// authorization seam returns an authorized dependency closure to withdraw; no production driver does
/// this wave.
/// </para>
/// </remarks>
internal static class WrapperDenylistSeed
{
    /// <summary>
    /// Builds the poison list that denies every unit in <paramref name="denylist"/> from the next
    /// render. The unit's own declaration is the poison key, so the emission gate that looks a member
    /// up by its <see cref="DeclId"/> finds it — an accessor-group unit is already normalized to its
    /// property by <see cref="RecoveryUnitId.ForAccessorGroup"/>, matching the property-level key the
    /// emitter gates use.
    /// </summary>
    public static EmitterPoisonList Build(IReadOnlySet<RecoveryUnitId> denylist)
        => Build(denylist, static _ => EmitterFaultOrigin.RecoveryWithdrawal);

    /// <summary>
    /// Builds the poison list, choosing each unit's withdrawal wording from
    /// <paramref name="originOf"/> — the same denylist can carry units the Swift wrapper compile
    /// withdrew and units the C# compile withdrew (a joint verify-recover run reuses one monotonic
    /// denylist across both planes). The origin drives only the tombstone/report wording; the emission
    /// gate denies every unit identically regardless of which verifier named it. A unit
    /// <paramref name="originOf"/> does not recognize falls back to the Swift-wrapper wording.
    /// </summary>
    public static EmitterPoisonList Build(
        IReadOnlySet<RecoveryUnitId> denylist,
        Func<RecoveryUnitId, EmitterFaultOrigin> originOf)
    {
        ArgumentNullException.ThrowIfNull(denylist);
        ArgumentNullException.ThrowIfNull(originOf);

        var poison = new EmitterPoisonList();
        foreach (var unit in denylist)
        {
            var origin = originOf(unit);
            // Each origin's wording says honestly how the unit was removed: the ABI plane is not a compile
            // (the typed plan-vs-descriptor check rejected the call); a bisection isolation is not an
            // attribution (a delta-debug search found this member cleared the compile, without naming why);
            // and the C# and wrapper compile wordings stay byte-identical.
            var message = origin switch
            {
                EmitterFaultOrigin.AbiRecoveryWithdrawal =>
                    $"the plan-vs-descriptor check rejected this call ({unit.Describe()})",
                EmitterFaultOrigin.BisectionIsolatedWithdrawal =>
                    $"a bounded bisection search isolated this member as the culprit for an " +
                    $"unattributable compile failure ({unit.Describe()})",
                _ =>
                    $"withdrawn to recover the " +
                    $"{(origin == EmitterFaultOrigin.CSharpRecoveryWithdrawal ? "C#" : "wrapper")} compile " +
                    $"({unit.Describe()})",
            };
            // The unit-aware Record routes by scope: a leaf/accessor/type seed lands in the bare-DeclId
            // index exactly as before (byte-identical), while a coarse sub-declaration seed lands in the
            // unit-keyed index so it withdraws only its own surface.
            poison.Record(unit, EmitterFaultRecord.ForRecoveryWithdrawal(
                unit.Decl,
                unit.Scope,
                message,
                origin: origin));
        }

        return poison;
    }
}
