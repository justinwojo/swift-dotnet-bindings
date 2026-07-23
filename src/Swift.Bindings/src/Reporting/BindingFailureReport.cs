// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;

using BindingsGeneration.Diagnostics;

namespace BindingsGeneration;

/// <summary>
/// The structured evidence a generator run leaves on disk when it fails — the durable counterpart to
/// the single console line a nonzero exit used to produce. Written next to where
/// <c>binding-report.json</c> would land, as <c>binding-failure-report.json</c>, on every nonzero exit
/// where a module and its inputs are known.
/// </summary>
/// <remarks>
/// The stable IDs and enums on this type (and the ones it nests) are the contract a downstream triager
/// or tool depends on; the C# class names and doc prose are not. Evolution is additive — a new field
/// with a compatible default is a compatible change; a renamed/removed field or a repurposed enum value
/// is a breaking one and bumps <see cref="CurrentSchemaVersion"/>. Serialized with
/// <c>StringEnumConverter</c> so every enum round-trips as its name, not an ordinal.
/// </remarks>
public sealed class BindingFailureReport
{
    /// <summary>The current frozen schema version. Bump only on a breaking (non-additive) change.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The schema version this document conforms to.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The module whose generation failed.</summary>
    public required string Module { get; init; }

    /// <summary>When the report was written (UTC).</summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The generator's informational version, so a report ties back to the build that produced it.</summary>
    public string? GeneratorVersion { get; init; }

    /// <summary>The identity of the inputs that were being bound, including a content fingerprint.</summary>
    public required BindingFailureInput Input { get; init; }

    /// <summary>The terminal outcome: what kind of failure, at which stage, under which stable reason code.</summary>
    public required BindingFailureOutcome Outcome { get; init; }

    /// <summary>
    /// The diagnostics that blocked the run — the first-class evidence. Empty when the failure produced
    /// no compiler/tool diagnostics (a structural fail-closed gate, say).
    /// </summary>
    public List<FailureDiagnostic> Diagnostics { get; init; } = new();

    /// <summary>
    /// The recovery units the failing round attributed the blocking diagnostics to — the declarations
    /// implicated in the failure. Empty when the failure was not attributed to any unit.
    /// </summary>
    public List<AttributedUnit> AttributedUnits { get; init; } = new();

    /// <summary>
    /// The verify-recover decision context — seeds, proposed vs. actual withdrawals, and why escalation
    /// stopped. Null for failures that never entered the verify-recover loop.
    /// </summary>
    public RecoveryDecision? RecoveryDecision { get; init; }

    /// <summary>Paths a human needs to inspect the failed attempt (the output directory, at least).</summary>
    public List<string> ArtifactPaths { get; init; } = new();
}

/// <summary>The identity of a failed generation's inputs.</summary>
public sealed class BindingFailureInput
{
    /// <summary>Path to the Swift ABI JSON, when known.</summary>
    public string? SwiftAbiPath { get; init; }

    /// <summary>Path to the dynamic library, when known.</summary>
    public string? DylibPath { get; init; }

    /// <summary>Path to the TBD file, when known.</summary>
    public string? TbdPath { get; init; }

    /// <summary>Path to the .swiftinterface, when supplied.</summary>
    public string? SwiftInterfacePath { get; init; }

    /// <summary>
    /// A stable content fingerprint over the supplied inputs, the target platform, and the generator
    /// version — each input folds in its file name (never its directory, so re-conversions into fresh
    /// temp directories fingerprint identically), then its content when the file is present, so
    /// distinct missing inputs stay distinct. Identifies exactly which inputs produced this failure so
    /// a re-run can be tied back to it. A lowercase hex SHA-256 digest, or <c>"unavailable"</c> when
    /// the digest could not be computed.
    /// </summary>
    public required string Fingerprint { get; init; }
}

/// <summary>How a generator run terminated.</summary>
public sealed class BindingFailureOutcome
{
    /// <summary>The terminal outcome kind (stable enum).</summary>
    public required BindingFailureOutcomeKind Kind { get; init; }

    /// <summary>
    /// The stable reason code — the emitted <c>SWIFTBIND…</c> diagnostic code where one exists, else a
    /// descriptive upper-snake token (e.g. <c>ABI_CONTRACT_VIOLATION</c>).
    /// </summary>
    public required string ReasonCode { get; init; }

    /// <summary>The pipeline stage/plane at which the run failed.</summary>
    public required RecoveryStage Stage { get; init; }

    /// <summary>
    /// How many render→compile→attribute rounds the verify-recover loop ran. Zero for failures that
    /// never entered the loop.
    /// </summary>
    public int RecoveryRounds { get; init; }

    /// <summary>
    /// The verify-recover failure cause, when the outcome is a recovery non-convergence. Null otherwise.
    /// </summary>
    public WrapperRecoveryFailureCause? RecoveryCause { get; init; }
}

/// <summary>The terminal-outcome taxonomy. Stable — add members, never repurpose.</summary>
public enum BindingFailureOutcomeKind
{
    /// <summary>The verify-recover loop could not reach a clean compile (SWIFTBIND111).</summary>
    RecoveryNonConvergence,

    /// <summary>Convergence left no usable public surface (SWIFTBIND116).</summary>
    NoUsableSurface,

    /// <summary>
    /// A wrapper-symbol integrity gate failed — an emitted wrapper-symbol P/Invoke had no matching
    /// definition, or stripped-symbol reconciliation could not restore consistency.
    /// </summary>
    WrapperSymbolViolation,

    /// <summary>A bare proxy construction had no matching proxy definition (proxy-reference integrity gate).</summary>
    ProxyReferenceViolation,

    /// <summary>The parse ledger was unbalanced — a declaration was lost with no disposition (SWIFTBIND121).</summary>
    ParseLedgerImbalance,

    /// <summary>An emitted member violated the library's ABI contract (AbiContractViolationException).</summary>
    AbiContractViolation,

    /// <summary>The compile-import graph could not be proven closed before parsing (SWIFTBIND119).</summary>
    InputClosureUnsatisfied,

    /// <summary>An ingestion-quarantined type's withdrawal closure could not be proven complete (SWIFTBIND120).</summary>
    IngestionClosureUnprovable,

    /// <summary>
    /// A dependency input could not be found, loaded, or parsed — a module database
    /// (SWIFTBIND070), a dependency ABI (SWIFTBIND072/073), or a declared
    /// <c>--framework-dependency</c> that failed resolution.
    /// </summary>
    DependencyInputFailure,

    /// <summary>An unexpected exception aborted the run.</summary>
    UnhandledException,

    /// <summary>Input resolution degraded and <c>--strict-inputs</c> forbids degrading (SWIFTBIND027).</summary>
    StrictInputsDegraded,

    /// <summary>The Swift wrapper could not be compiled or configured (fatal compile outcome, invalid architecture selection).</summary>
    WrapperCompileFailure,

    /// <summary>The generated C# failed in-generator compile verification.</summary>
    CSharpVerificationFailure,

    /// <summary>The binding project (csproj and companions) could not be emitted.</summary>
    ProjectEmissionFailure,

    /// <summary>The mixed-framework ObjC companion pipeline failed to produce its surface.</summary>
    MixedObjCSurfaceFailure,

    /// <summary>The pure-ObjC binding pipeline (clang parse, filtering, or companion emission) exited nonzero.</summary>
    ObjCPipelineFailure,

    /// <summary>A required primary input (ABI JSON, dylib, TBD) or the output directory was missing or invalid at generation start.</summary>
    RequiredInputMissing,

    /// <summary>A command-line option value was invalid, detected after the module identity and inputs were already resolved.</summary>
    InvalidConfiguration,
}

/// <summary>The tool/plane a diagnostic came from.</summary>
public enum DiagnosticPlane
{
    /// <summary>The generator itself (a global classification with no source file).</summary>
    Generator,

    /// <summary>The Swift compiler (a <c>.swift</c> source).</summary>
    SwiftCompiler,

    /// <summary>The C# compiler (a <c>.cs</c> source).</summary>
    CSharpCompiler,

    /// <summary>The plane could not be determined.</summary>
    Unknown,
}

/// <summary>One compiler/tool diagnostic that blocked the run.</summary>
public sealed class FailureDiagnostic
{
    /// <summary>The tool/plane the diagnostic came from.</summary>
    public required DiagnosticPlane Plane { get; init; }

    /// <summary>The tool's own diagnostic code, when one was parsed; null otherwise.</summary>
    public string? Code { get; init; }

    /// <summary>The diagnostic severity.</summary>
    public required DiagnosticSeverity Severity { get; init; }

    /// <summary>The message, whitespace-normalized.</summary>
    public required string Message { get; init; }

    /// <summary>The source span, when the diagnostic carried a position.</summary>
    public SourceSpan? Span { get; init; }

    /// <summary>A stable per-diagnostic fingerprint over plane, normalized message, and span.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// The compiler's attached notes — cascade context ("declared here", "in expansion of …") that
    /// rides along with the primary. Evidence only; empty when the tool attached none.
    /// </summary>
    public List<FailureDiagnosticNote> Notes { get; init; } = new();
}

/// <summary>A note a compiler attached to a primary diagnostic.</summary>
public sealed class FailureDiagnosticNote
{
    /// <summary>The note's message, whitespace-normalized.</summary>
    public required string Message { get; init; }

    /// <summary>The note's source span, when it carried a position.</summary>
    public SourceSpan? Span { get; init; }
}

/// <summary>A source position on a diagnostic.</summary>
public sealed class SourceSpan
{
    /// <summary>The source file, when known.</summary>
    public string? File { get; init; }

    /// <summary>1-based line, 0 when positionless.</summary>
    public int Line { get; init; }

    /// <summary>1-based column, 0 when positionless.</summary>
    public int Column { get; init; }
}

/// <summary>A recovery unit the failing round attributed the failure to.</summary>
public sealed class AttributedUnit
{
    /// <summary>The stable recovery-unit id (<see cref="RecoveryUnitId.Canonical"/>).</summary>
    public required string UnitId { get; init; }

    /// <summary>The stable declaration id (<see cref="DeclId.Canonical"/>).</summary>
    public required string DeclId { get; init; }

    /// <summary>The human-readable name (<see cref="RecoveryUnitId.Describe"/>).</summary>
    public required string DisplayName { get; init; }

    /// <summary>The recovery scope of the unit.</summary>
    public required RecoveryScope Scope { get; init; }

    /// <summary>How attribution placed the unit (its provenance).</summary>
    public ProvenanceSource Provenance { get; init; }

    /// <summary>Confidence in the attribution, derived from its provenance.</summary>
    public AttributionConfidence Confidence { get; init; }

    /// <summary>Indices into <see cref="BindingFailureReport.Diagnostics"/> that named this unit.</summary>
    public List<int> DiagnosticRefs { get; init; } = new();
}

/// <summary>The verify-recover loop's decision context at the point of failure.</summary>
public sealed class RecoveryDecision
{
    /// <summary>The units the loop was seeded with before its first round (ingestion-quarantine withdrawals).</summary>
    public List<string> SeedIds { get; init; } = new();

    /// <summary>The units the final round proposed to withdraw (its attributed culprits).</summary>
    public List<string> ProposedWithdrawalIds { get; init; } = new();

    /// <summary>The units the run actually withdrew (its settled denylist).</summary>
    public List<string> ActualWithdrawalIds { get; init; } = new();

    /// <summary>The withdrawn units isolated by bounded bisection rather than attribution.</summary>
    public List<string> SearchIsolatedIds { get; init; } = new();

    /// <summary>The coarse-scope units the loop could not withdraw as leaves.</summary>
    public List<string> BlockerUnitIds { get; init; } = new();

    /// <summary>
    /// The single unit at which escalation crossed out of safe leaf-withdrawal territory — the first
    /// coarse blocker on the graph-closure path. Null when the failure was global, unattributed, or a
    /// whole-round no-progress condition with no single implicated unit.
    /// </summary>
    public string? EscalationUnitId { get; init; }

    /// <summary>The coarse-withdrawal authorization outcome.</summary>
    public required CoarseWithdrawalOutcome AuthorizationOutcome { get; init; }

    /// <summary>The stable obstruction code — the verify-recover failure cause name.</summary>
    public required string ObstructionCode { get; init; }
}

/// <summary>The verdict a coarse-withdrawal authorization reached, projected for the report.</summary>
public enum CoarseWithdrawalOutcome
{
    /// <summary>No coarse withdrawal was attempted (the failure was leaf-scoped, global, or unattributed).</summary>
    NotApplicable,

    /// <summary>A coarse withdrawal was needed but not authorized — the module failed closed.</summary>
    Unauthorized,

    /// <summary>A coarse withdrawal was authorized (reserved; this wave authorizes none).</summary>
    Authorized,
}
