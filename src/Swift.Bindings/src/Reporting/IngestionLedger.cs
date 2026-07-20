// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Which ingestion plane a ledger entry was decided on. The three planes are the pipeline's
/// three chances to lose or deform an input node, in order:
/// </summary>
/// <remarks>
/// The plane doubles as the phase axis: it says <em>where</em> in ingestion the loss/deform was
/// observed, which is enough to route a reader to the responsible stage. The finer site within a
/// plane is carried by <see cref="IngestionCause"/>.
/// </remarks>
public enum IngestionPlane
{
    /// <summary>Reading and dispatching ABI-JSON nodes — the parser's node walk.</summary>
    Ingest,

    /// <summary>Resolving the dependency/closure graph — module and cross-module reference completeness.</summary>
    Resolve,

    /// <summary>Deciding a degradation — quarantining a malformed node and proving its withdrawal closure.</summary>
    Degrade,
}

/// <summary>
/// The precise reason an input node was dropped, deformed, or withdrawn. Every structured ledger
/// entry carries exactly one, so a reader can tell a benign digester-shape miss apart from a
/// load-bearing record loss without re-reading the log.
/// </summary>
public enum IngestionCause
{
    /// <summary>An ABI node kind the parser's dispatch allowlist does not model (SWIFTBIND034).</summary>
    UnrecognizedNodeKind,

    /// <summary>
    /// A bindable type (Struct/Enum/Class/Protocol) whose load-bearing Swift mangled name is absent —
    /// the malformed-type-record shape the proven-closure quarantine exists for.
    /// </summary>
    MalformedTypeRecord,

    /// <summary>A recognized declaration missing some other load-bearing ABI field (SWIFTBIND046).</summary>
    AbiFieldAbsent,

    /// <summary>The parser reached a declaration shape it has not implemented a binder for.</summary>
    UnhandledDeclaration,

    /// <summary>An unclassified exception escaped a per-declaration binder.</summary>
    ParseFault,

    /// <summary>A required public dependency failed to parse, so the graph cannot be closed.</summary>
    UnresolvedRequiredDependency,
}

/// <summary>
/// What the disposition policy decided to do with a losable node. The policy is consensus-locked:
/// a malformed leaf with a fully-known signature degrades that leaf; a malformed type/layout/metadata
/// record quarantines the type plus its proven dependent closure; a missing module or an unprovable
/// closure is report-only fatal.
/// </summary>
public enum IngestionDisposition
{
    /// <summary>Degrade a single leaf declaration whose signature is fully known and whose dependents survive.</summary>
    DegradeLeaf,

    /// <summary>Quarantine a malformed type and every retained declaration proven to depend on it.</summary>
    QuarantineType,

    /// <summary>
    /// Report the loss and continue (the legacy fail-open drop channel): the node was dropped and
    /// generation proceeded with a smaller surface. Distinct from <see cref="ReportOnlyFatal"/> — this
    /// is the pre-existing "dropped-with-error, keep going" behavior that <c>--strict-inputs</c>
    /// escalates to fatal — and from <see cref="QuarantineType"/>, which is a proven, tombstoned
    /// withdrawal rather than an unproven drop.
    /// </summary>
    ReportOnly,

    /// <summary>
    /// Report the loss and fail the module before emission. Used when the graph is incomplete or the
    /// withdrawal closure cannot be proven complete — never quarantine on an unproven closure, since a
    /// compile-clean/runtime-wrong binding is worse than an early, precise failure.
    /// </summary>
    ReportOnlyFatal,
}

/// <summary>The terminal outcome of a ledger entry, set once the run has decided the node's fate.</summary>
public enum IngestionStatus
{
    /// <summary>The node was bound after all — recorded for completeness, no loss occurred.</summary>
    Retained,

    /// <summary>The node was omitted from the binding, tombstoned, and reported; the binding still shipped.</summary>
    Quarantined,

    /// <summary>The node's loss failed the module before emission.</summary>
    Fatal,

    /// <summary>
    /// The node was lost and generation continued as a degradation (the legacy dropped-with-error
    /// channel), without a proven withdrawal closure. Distinct from <see cref="Quarantined"/>: a drop
    /// is a recorded loss with no closure proof, a quarantine is a proven, tombstoned withdrawal.
    /// </summary>
    Dropped,
}

/// <summary>
/// The stable identity of one input node: enough to name it across runs without depending on a
/// mangled name that may be exactly what is absent. <see cref="Symbol"/> is the USR or mangled name
/// when present, else a sentinel — never null, so two malformed nodes never collapse onto one identity.
/// </summary>
public sealed record IngestionInputIdentity(string Module, string Kind, string Symbol)
{
    /// <summary>The sentinel used for <see cref="Symbol"/> when the node carries neither a USR nor a mangled name.</summary>
    public const string AbsentSymbol = "<absent>";

    /// <summary>A short, stable, human-readable identity string for report rows and evidence text.</summary>
    public override string ToString() => $"{Module}.{Kind}:{Symbol}";
}

/// <summary>
/// One structured ingestion-ledger entry: a single input node that was lost, deformed, or withdrawn,
/// with a disposition and its terminal status. The invariant the program upholds is that no parser
/// loss is ever silent again — every dropped/deformed/withdrawn node becomes one of these.
/// </summary>
/// <param name="Input">The node's stable identity.</param>
/// <param name="Parent">The declaring parent's identity, when the node had one.</param>
/// <param name="Plane">Which ingestion plane observed the loss/deform.</param>
/// <param name="Cause">The precise cause code.</param>
/// <param name="Referenced">
/// The type/module/symbol the loss refers to, when the cause names one (e.g. the module a dependency
/// failed to resolve, or the type a quarantined dependent referenced); else null.
/// </param>
/// <param name="Disposition">What the policy decided to do.</param>
/// <param name="ClosureEvidence">
/// Human-readable evidence for the disposition — for a quarantine, why the withdrawal closure is
/// proven complete; for a fatal, why it could not be. The one place a reader can audit the decision.
/// </param>
/// <param name="Status">The terminal outcome.</param>
public sealed record IngestionLedgerEntry(
    IngestionInputIdentity Input,
    IngestionInputIdentity? Parent,
    IngestionPlane Plane,
    IngestionCause Cause,
    string? Referenced,
    IngestionDisposition Disposition,
    string ClosureEvidence,
    IngestionStatus Status);
