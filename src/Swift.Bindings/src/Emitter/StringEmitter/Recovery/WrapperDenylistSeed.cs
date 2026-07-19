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
/// The controller only ever hands leaf and accessor-group units to a round, so every seed record is a
/// terminal withdrawal with no escalation rung — a wider fault fails the module closed in the
/// controller before it could reach here.
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
    {
        ArgumentNullException.ThrowIfNull(denylist);

        var poison = new EmitterPoisonList();
        foreach (var unit in denylist)
        {
            poison.Record(EmitterFaultRecord.ForRecoveryWithdrawal(
                unit.Decl,
                unit.Scope,
                $"withdrawn to recover the wrapper compile ({unit.Describe()})"));
        }

        return poison;
    }
}
