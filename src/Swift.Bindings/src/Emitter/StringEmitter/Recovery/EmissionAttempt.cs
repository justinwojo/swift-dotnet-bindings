// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Signals that the current emission attempt has been abandoned and must be re-run with a wider
/// poison list. Carries no payload: the fault is already recorded on the attempt by the time this
/// is thrown, so a frame that swallows it still cannot lose the fault.
/// </summary>
internal sealed class EmissionAttemptAbandoned : Exception
{
    public EmissionAttemptAbandoned()
        : base("Emission attempt abandoned after a contained emitter fault.")
    {
    }
}

/// <summary>
/// Containment gave up: the emitter kept throwing on fresh declarations past the attempt cap, so the
/// module is not shippable and nothing was written.
/// </summary>
/// <remarks>
/// Reaching this means the denylist was still growing when the budget ran out, which is a different
/// failure from "one member is broken" — either a single defect is manifesting under many identities,
/// or emission is faulting on something structural that denying members cannot route around. Both
/// warrant failing loudly rather than shipping a module that silently lost an unknown share of its
/// surface, so the message carries every fault collected along the way.
/// </remarks>
internal sealed class EmitterFaultLimitException : Exception
{
    public EmitterFaultLimitException(string moduleName, int attempts, IReadOnlyCollection<EmitterFaultRecord> faults)
        : base(BuildMessage(moduleName, attempts, faults))
    {
        Faults = faults;
    }

    /// <summary>Every declaration the emitter threw on, in discovery order.</summary>
    public IReadOnlyCollection<EmitterFaultRecord> Faults { get; }

    private static string BuildMessage(string moduleName, int attempts, IReadOnlyCollection<EmitterFaultRecord> faults)
    {
        var builder = new System.Text.StringBuilder()
            .Append("SWIFTBIND110: emission of module '").Append(moduleName).Append("' still faulted after ")
            .Append(attempts).Append(" attempts; ").Append(faults.Count)
            .AppendLine(" declaration(s) were denied and the emitter kept throwing on new ones.");

        foreach (var fault in faults)
        {
            builder.Append("  - ").Append(fault.Subject.Canonical).Append(": ").AppendLine(fault.Details);
        }

        return builder.ToString();
    }
}

/// <summary>
/// The ambient state of one emission attempt: which declarations are denied going in, and which
/// threw on the way through.
/// </summary>
/// <remarks>
/// <para>
/// This is ambient rather than threaded for the same reason <see cref="ReportCollector"/> is. The
/// denial gate has to hold at every validation entry point — the method pipeline, property and
/// subscript gates, the protocol member evaluator, operators, type skip conditions — and several of
/// those are reached on paths that pass a null <c>ValidationContext</c>. A gate that can only be
/// consulted when a context happens to be non-null is not a gate; a poisoned declaration would emit
/// anyway on exactly the paths nobody remembered to thread.
/// </para>
/// <para>
/// The scope is one attempt at one module, opened and closed by the attempt loop, and modules are
/// processed sequentially — the same containment <see cref="ReportCollector"/> already relies on.
/// </para>
/// </remarks>
internal sealed class EmissionAttempt : IDisposable
{
    /// <summary>
    /// How many times a module may be emitted before containment gives up.
    /// </summary>
    /// <remarks>
    /// Three attempts contain two independent faults, which is well past what a healthy generator
    /// produces and short enough that a pathological library fails in seconds rather than grinding
    /// through a denial-per-member. The cap is on attempts rather than on faults because each attempt
    /// costs a full re-emission, and that is the resource worth bounding.
    /// </remarks>
    public const int MaxEmissionAttempts = 3;

    private static readonly AsyncLocal<EmissionAttempt?> Ambient = new();

    private readonly EmitterPoisonList _poison;
    private readonly EmissionAttempt? _previous;
    private bool _disposed;

    private EmissionAttempt(EmitterPoisonList poison)
    {
        _poison = poison;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    /// <summary>The attempt in flight, or null when no emission is running.</summary>
    public static EmissionAttempt? Current => Ambient.Value;

    /// <summary>Undo log for this attempt's writes to the frozen type database.</summary>
    public EmissionFactsJournal Journal { get; } = new();

    /// <summary>
    /// True when a fault was recorded during this attempt, which means the attempt's output is
    /// tainted and must be discarded rather than settled.
    /// </summary>
    public bool Abandoned { get; private set; }

    /// <summary>Opens an attempt over <paramref name="poison"/> and makes it ambient.</summary>
    public static EmissionAttempt Begin(EmitterPoisonList poison) => new(poison);

    /// <summary>
    /// True when <paramref name="id"/> is denied for this attempt. Safe to call with no attempt in
    /// flight — unit tests and tools that drive emitters directly are simply never poisoned.
    /// </summary>
    public static bool IsDenied(in DeclId id) => Ambient.Value?._poison.IsPoisoned(id) ?? false;

    /// <summary>The fault that denied <paramref name="id"/>, for the tombstone's details string.</summary>
    public static bool TryGetFault(in DeclId id, out EmitterFaultRecord fault)
    {
        var current = Ambient.Value;
        if (current is not null)
        {
            return current._poison.TryGet(id, out fault);
        }

        fault = default;
        return false;
    }

    /// <summary>
    /// True when the coarse sub-declaration <paramref name="unit"/> is denied for this attempt. This is
    /// the unit-keyed counterpart to <see cref="IsDenied(in DeclId)"/> — a shared-helper bundle or a
    /// conformance edge is denied by its full recovery-unit identity, so seeding one withdraws only its
    /// own surface rather than collapsing the whole enclosing declaration.
    /// </summary>
    public static bool IsDenied(in RecoveryUnitId unit) => Ambient.Value?._poison.IsPoisoned(unit) ?? false;

    /// <summary>The fault that denied the coarse <paramref name="unit"/>, for the tombstone's details.</summary>
    public static bool TryGetFault(in RecoveryUnitId unit, out EmitterFaultRecord fault)
    {
        var current = Ambient.Value;
        if (current is not null)
        {
            return current._poison.TryGet(unit, out fault);
        }

        fault = default;
        return false;
    }

    /// <summary>
    /// Records a contained fault and marks the attempt abandoned.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the fault could not be contained — the subject was already denied and has no
    /// wider rung — in which case the caller must let the exception propagate and fail the module.
    /// </returns>
    public bool RecordFault(EmitterFaultRecord fault)
    {
        if (!_poison.Record(fault))
        {
            return false;
        }

        Abandoned = true;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Ambient.Value = _previous;
    }
}
