// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The denial half of poison-and-regenerate: refuses, before any emission work happens, every
/// declaration a previous attempt at this module threw on.
/// </summary>
/// <remarks>
/// <para>
/// This runs ahead of every other gate — a declaration that crashes the emitter cannot be judged on
/// whether its types are supported, because reaching that judgement is what crashed. Denying first
/// also makes the retry cheap: a poisoned member is refused at its gate rather than partially lowered
/// and then unwound.
/// </para>
/// <para>
/// The denial flows out through the ordinary skip channel rather than a private one, so a contained
/// fault produces exactly what every other refusal produces: an unsupported comment where the member
/// would have been, a report row, and a consumed-but-empty vtable slot where the layout needs one.
/// That is what makes a contained run equivalent to a clean run against a wider denylist, rather than
/// to a run where the declaration was never parsed — the latter would shrink the vtable and shift
/// every later slot.
/// </para>
/// </remarks>
internal static class EmitterFaultGate
{
    /// <summary>
    /// The verdict denying <paramref name="subject"/>, or <c>null</c> when it is not poisoned — which
    /// is every declaration of every ordinary run.
    /// </summary>
    public static ValidationResult? Denied(in DeclId subject) =>
        EmissionAttempt.TryGetFault(subject, out var fault)
            ? ValidationResult.Skip(SkipReason.EmitterFault, fault.Details).WithSubject(subject)
            : null;

    /// <summary>
    /// True when <paramref name="subject"/> is poisoned, with the detail string for callers whose
    /// refusal channel is not a <see cref="ValidationResult"/> (operators, enum members, type
    /// pre-passes, proxy policy).
    /// </summary>
    public static bool IsDenied(in DeclId subject, out string details)
    {
        if (EmissionAttempt.TryGetFault(subject, out var fault))
        {
            details = fault.Details;
            return true;
        }

        details = string.Empty;
        return false;
    }

    /// <summary>
    /// True when the coarse sub-declaration <paramref name="unit"/> is poisoned, with the detail string
    /// for the tombstone/report row. The unit-keyed counterpart to
    /// <see cref="IsDenied(in DeclId, out string)"/> — a coarse emission boundary (the reverse
    /// -conformance seam, the shared-helper bundle emitters, the conformance-edge site) queries this so a
    /// withdrawal of its surface does not require poisoning, and thereby collapsing, the whole type.
    /// </summary>
    public static bool IsDenied(in RecoveryUnitId unit, out string details)
    {
        if (EmissionAttempt.TryGetFault(unit, out var fault))
        {
            details = fault.Details;
            return true;
        }

        details = string.Empty;
        return false;
    }
}
