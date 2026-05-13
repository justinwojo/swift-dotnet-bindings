// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Confidence in a <see cref="TypeResolutionResult"/>.
/// </summary>
/// <remarks>
/// Forward-looking field — will be populated meaningfully once strategies
/// can attach source provenance. Current strategies report <see cref="High"/>
/// uniformly because they are exact-match lookups.
/// </remarks>
public enum ResolutionConfidence
{
    /// <summary>Match is exact (e.g., generic-parameter syntax, primitive alias map hit).</summary>
    High,
    /// <summary>Match required heuristic interpretation (reserved).</summary>
    Medium,
    /// <summary>Match was a best-effort fallback (reserved).</summary>
    Low,
}

/// <summary>
/// Provenance attached to a resolution.
/// </summary>
/// <param name="Source">
/// Free-form description of where the resolution came from (e.g.,
/// <c>"strategy:DynamicSelf"</c>). May later be extended with Swift
/// <c>file:line:column</c> positions sourced from the regex parser.
/// </param>
public sealed record ResolutionProvenance(string Source);

/// <summary>
/// Outcome of a single <see cref="TypeResolver.Resolve"/> call.
/// </summary>
/// <remarks>
/// Replaces the four diverging legacy result shapes
/// (<c>TryGetTypeRecord</c>'s out parameter,
/// <c>GetTypeRecordOrAnyType</c>'s record return,
/// <c>GetTypeRecordOrThrow</c>'s record-or-throw,
/// <c>TryGetAnyTypeFallbackInfo</c>'s reason payload) with a single record.
/// Callers project the slice they need; the resolver carries every fact each
/// shape needs to render.
/// </remarks>
/// <param name="Record">
/// Resolved <see cref="TypeRecord"/>, or null when the resolver could not
/// produce a record. A null record paired with a non-null
/// <see cref="SkipReason"/> describes an explicit skip.
/// </param>
/// <param name="SyntheticFallback">
/// Set when the resolver intentionally produced a synthetic fallback record
/// (e.g., the <c>AnyType</c> result for an unresolvable type). Mirrors the
/// payload <c>TryGetAnyTypeFallbackInfo</c> renders today. Strategies that
/// resolve to a real type should leave this null.
/// </param>
/// <param name="SkipReason">
/// When set, indicates the resolver chose to skip the type. Carries the
/// short, machine-readable skip reason that diagnostic identity code keys
/// against.
/// </param>
/// <param name="SupplementReference">
/// Module-qualified Swift identity that should be recorded against the
/// SwiftBindings.Apple supplement. Populated by the supplement strategy
/// when it is the resolution source.
/// </param>
/// <param name="Confidence">Confidence in the result. See <see cref="ResolutionConfidence"/>.</param>
/// <param name="Provenance">
/// Optional provenance attached by the strategy that produced the result.
/// </param>
public sealed record TypeResolutionResult(
    TypeRecord? Record,
    TypeDatabaseExtensions.AnyTypeFallbackInfo? SyntheticFallback = null,
    string? SkipReason = null,
    string? SupplementReference = null,
    ResolutionConfidence Confidence = ResolutionConfidence.High,
    ResolutionProvenance? Provenance = null)
{
    /// <summary>
    /// Convenience: a resolution succeeded if a <see cref="TypeRecord"/> was produced.
    /// </summary>
    public bool IsResolved => Record is not null;
}
