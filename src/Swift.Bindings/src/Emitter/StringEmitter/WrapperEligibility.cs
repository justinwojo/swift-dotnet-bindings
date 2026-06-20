// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The single decision type the @_cdecl wrapper emitters return from their
/// <c>EvaluateWrapperEligibility</c> pass. Collapses the former predict/emit mirror pairs
/// (a boolean <c>ShouldEmitWrapper</c> gate plus a hand-maintained <c>GetRejectionReason</c>
/// string) into one traversal, so the "would this member be wrapped?" answer and the
/// "why not?" diagnostic can never drift apart (Finding 12 — predict/emit unification).
///
/// <para><see cref="IsWrappable"/> is the load-bearing signal — it drives emission and must
/// stay byte-identical to the legacy boolean gates. <see cref="Reason"/> is diagnostic only:
/// it feeds debug logs and the emission-report skip-reason histogram, never the generated C#.</para>
/// </summary>
public readonly struct WrapperEligibility
{
    /// <summary>
    /// The first guard that rejected the member, or <c>null</c> when the member is wrappable.
    /// Diagnostic only — consumed by logging and the emission report, not by code generation.
    /// </summary>
    public string? Reason { get; }

    private WrapperEligibility(string? reason) => Reason = reason;

    /// <summary>True when no guard rejected the member (it will receive a @_cdecl wrapper).</summary>
    public bool IsWrappable => Reason is null;

    /// <summary>A member that passes every wrapper guard.</summary>
    public static WrapperEligibility Wrappable => new((string?)null);

    /// <summary>A member rejected by the named guard.</summary>
    public static WrapperEligibility Reject(string reason) => new(reason);
}
