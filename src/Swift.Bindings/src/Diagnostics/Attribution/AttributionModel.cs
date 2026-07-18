// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using BindingsGeneration;

namespace BindingsGeneration.Diagnostics;

/// <summary>
/// What became of one diagnostic when attribution ran.
/// </summary>
public enum AttributionKind
{
    /// <summary>Resolved to a recovery unit — a real culprit the denylist can act on.</summary>
    Unit,

    /// <summary>
    /// A global failure classified by cause (a missing input module, a toolchain fault) rather than
    /// blamed on a declaration. Classification never contributes a culprit.
    /// </summary>
    Classification,

    /// <summary>Could not be tied to a unit or classified — surfaces as "no attribution".</summary>
    Unattributed,
}

/// <summary>
/// Which provenance mechanism resolved a diagnostic, in priority order. Recorded so a caller — a
/// test, a report, the recovery loop — can see whether a culprit came from the authoritative
/// interval map or a fallback, without re-running the resolution.
/// </summary>
public enum ProvenanceSource
{
    /// <summary>Not resolved by any mechanism.</summary>
    None,

    /// <summary>Priority 1: the per-render interval map over immutable fragments.</summary>
    IntervalMap,

    /// <summary>Priority 2: the enclosing block's <c>@_cdecl</c>/<c>@_silgen_name</c> symbol.</summary>
    SymbolAnchor,

    /// <summary>Priority 3: the enclosing block's <c>// SBW-ORIGIN:</c> anchor comment.</summary>
    OriginAnchor,

    /// <summary>Priority 4: a linker error matched to a wrapper symbol by name.</summary>
    LinkerSymbol,
}

/// <summary>The unit a provenance step resolved a diagnostic to, and how it got there.</summary>
public readonly record struct ProvenanceHit(ArtifactId Artifact, RecoveryUnitId Unit, ProvenanceSource Source);

/// <summary>
/// One resolution step in the attribution priority order. Each step either resolves a diagnostic to
/// an owning unit or declines, letting the next-lower-priority step try.
/// </summary>
/// <remarks>
/// The steps are deliberately an ordered list rather than a hard-coded ladder so the same engine
/// serves both today's Swift wrapper compile and the later in-process C# probe: a caller supplies
/// whichever steps its compiler affords (interval map when a fragment set is in hand, symbol/anchor
/// always, linker only for the native link). Order is priority — the engine takes the first hit.
/// </remarks>
public interface IProvenanceStep
{
    /// <summary>Resolves <paramref name="diagnostic"/> to an owning unit, or returns false to defer.</summary>
    bool TryResolve(CompilerDiagnostic diagnostic, out ProvenanceHit hit);
}

/// <summary>One diagnostic together with the attribution decision made for it.</summary>
public readonly record struct AttributedDiagnostic
{
    /// <summary>The diagnostic group (primary + notes) this decision is about.</summary>
    public required DiagnosticGroup Diagnostic { get; init; }

    /// <summary>Whether it resolved to a unit, was classified, or could not be attributed.</summary>
    public required AttributionKind Kind { get; init; }

    /// <summary>The owning artifact, when <see cref="Kind"/> is <see cref="AttributionKind.Unit"/>.</summary>
    public ArtifactId? Artifact { get; init; }

    /// <summary>The owning recovery unit, when <see cref="Kind"/> is <see cref="AttributionKind.Unit"/>.</summary>
    public RecoveryUnitId? Unit { get; init; }

    /// <summary>Which mechanism resolved it (or <see cref="ProvenanceSource.None"/>).</summary>
    public ProvenanceSource Source { get; init; }

    /// <summary>The cause owner, when <see cref="Kind"/> is <see cref="AttributionKind.Classification"/>.</summary>
    public CauseOwner Owner { get; init; }

    /// <summary>Human-readable classification detail (e.g. the missing module name); null otherwise.</summary>
    public string? ClassificationDetail { get; init; }
}

/// <summary>
/// The outcome of attributing one failed compile: the per-diagnostic decisions, the batched set of
/// distinct culprit units, and a position-independent fingerprint of the failure.
/// </summary>
/// <remarks>
/// The culprit set is already cascade-collapsed and batched: only primaries are attributed, and
/// distinct-by-unit means many diagnostics inside one block (a cascade off a single broken member)
/// yield exactly one denylist increment. Dependency-closure over the recovery graph — turning a
/// culprit into the full set of units that must go with it — is the recovery step's job, not this
/// one's; attribution reports the roots it can see from the diagnostics alone.
/// </remarks>
public sealed record AttributionResult
{
    /// <summary>Per-diagnostic attribution decisions, in input order.</summary>
    public required ImmutableArray<AttributedDiagnostic> Diagnostics { get; init; }

    /// <summary>Distinct culprit units, in first-seen order — one denylist increment.</summary>
    public required ImmutableArray<RecoveryUnitId> Culprits { get; init; }

    /// <summary>Position-independent fingerprint of the failure, for no-progress detection.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The declarations behind <see cref="Culprits"/>, in the same order.</summary>
    public IEnumerable<DeclId> CulpritDecls => Culprits.Select(u => u.Decl);

    /// <summary>True when an error-severity diagnostic resolved to neither a unit nor a classification.</summary>
    public bool HasUnattributedError =>
        Diagnostics.Any(d => d.Kind == AttributionKind.Unattributed && d.Diagnostic.IsError);

    /// <summary>Count of error-severity primaries in the input.</summary>
    public int ErrorCount => Diagnostics.Count(d => d.Diagnostic.IsError);
}
