// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;

namespace BindingsGeneration;

/// <summary>
/// Authoritative on-disk record of all binding generation artifacts for one module.
/// Written after every phase that mutates the output directory, so consumers (and the
/// rederived <see cref="BindingReport"/>) reflect what was actually shipped — not the
/// generator's mid-pipeline view.
/// </summary>
public sealed class BindingArtifactManifest
{
    // v2: retired the ProxyCoGating/ContractCoGating sections (the generate-then-strip
    // co-gater is gone — proxy/contract decisions are made at emission). The surviving
    // proxy-suppression count moved to EmissionSection.SuppressedProxyClassCount.
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Module { get; init; }
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? GeneratorVersion { get; init; }

    /// <summary>
    /// Document-level status. <see cref="ManifestStatus.Complete"/> only when the
    /// manifest can be treated as the authoritative description of the output dir.
    /// A manifest with no <see cref="Generation"/> section is always
    /// <see cref="ManifestStatus.Partial"/>; it cannot be confused for a complete
    /// generation artifact.
    /// </summary>
    public ManifestStatus Status { get; set; } = ManifestStatus.Partial;
    public string? PartialReason { get; set; }

    public GenerationSection? Generation { get; set; }
    public EmissionSection? Emission { get; set; }

    /// <summary>
    /// Finding 50: per-generation input-resolution report. Records every slice/arch/artifact
    /// decision and degraded dependency the input edge made, so a silently-substituted input
    /// (device→sim slice fallback, missing swiftinterface, ABI-JSON fallback, ambiguous TBD,
    /// degraded auto-detected dependency) is observable after the fact and can fail a CI gate.
    /// Null for partial manifests written before resolution ran.
    /// </summary>
    public InputResolutionSection? InputResolution { get; set; }

    public WrapperSection? Wrapper { get; set; }
    public BridgeSection? Bridge { get; set; }

    /// <summary>
    /// A1: the ObjC binding surface's dropped symbols. A mixed (ObjC+Swift) binding has two
    /// independent binding surfaces; only the Swift one flows through <see cref="Generation"/>.
    /// The ObjC pipeline records its drops as <c>ObjCBindingDiagnostics</c> whose only prior sink
    /// was an INFO log line — never serialized, never in <c>SkipTriage</c>. Carrying them here folds
    /// the ObjC drop set into the rederived <c>binding-report.json</c>'s single <c>ReviewCount</c>
    /// gate (<see cref="BindingReportProjection"/>). Null for Swift-only bindings and for legacy
    /// manifests written before this section existed.
    /// </summary>
    public ObjCSection? ObjC { get; set; }
}

public enum ManifestStatus
{
    Partial,
    Complete,
}

public enum PhaseStatus
{
    NotRun,
    Success,
    Warning,
    Fatal,
    NoOp,
    Partial,
}

/// <summary>
/// Generation-phase snapshot. Populated by the main pass after C# emission. Proxy-reference
/// and wrapper-symbol-contract suppression are decided during emission, not in a post-pass.
/// </summary>
public sealed class GenerationSection
{
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public PhaseStatus Status { get; init; } = PhaseStatus.Success;

    public int TotalTypes { get; init; }
    public int EmittedTypes { get; init; }
    public int SkippedTypes { get; init; }
    public int TotalMembers { get; init; }
    public int EmittedMembers { get; init; }
    public int SkippedMembers { get; init; }
    public int SynthesizedMembers { get; init; }

    public Dictionary<BindingItemKind, int> EmittedMembersByKind { get; init; } = new();
    public Dictionary<BindingItemKind, int> SkippedMembersByKind { get; init; } = new();

    public List<SkippedItem> SkippedItems { get; init; } = new();
    public List<WrappedItem> WrappedItems { get; init; } = new();
    public List<BridgedViewItem> BridgedViews { get; init; } = new();
    public List<ThemeBridgedItem> ThemeBridgedProperties { get; init; } = new();
    public BridgeSummary? BridgeSummary { get; init; }

    /// <summary>
    /// Finding 14a: node-level parse reconciliation (<c>Parsed == Emitted + SkippedWithReason +
    /// DroppedWithError</c>). Turns the parser's previously invisible <c>HandleNode</c> swallow
    /// channel into a durable count so a regression that drops declarations surfaces as a number.
    /// Null for legacy/partial manifests written before this field existed.
    /// </summary>
    public ParseReconciliation? ParseReconciliation { get; init; }

    /// <summary>
    /// Finding 53: distinct <c>// Unsupported:</c> comment-drops emitted this run, each surfaced as a
    /// <c>SWIFTBIND025</c> diagnostic. Carried on the manifest so it survives into the rederived
    /// <c>binding-report.json</c> (<see cref="BindingReportProjection"/>) — the SWIFTBIND025 message
    /// promises the drop is "recorded under unsupportedCommentDrops in binding-report.json", and the
    /// report is projected from this manifest, so the list must round-trip or that claim is false.
    /// </summary>
    public List<string> UnsupportedCommentDrops { get; init; } = new();

    /// <summary>
    /// Finding 53: distinct Swift types that degraded to bare <c>object</c>, each surfaced as a
    /// <c>SWIFTBIND026</c> diagnostic. Carried on the manifest for the same round-trip reason as
    /// <see cref="UnsupportedCommentDrops"/>.
    /// </summary>
    public List<string> ObjectDegradations { get; init; } = new();

    /// <summary>
    /// F10 Stage 20: distinct Apple-framework reference types bridged to their ObjC class by the
    /// naming-convention heuristic (no database record). Carried on the manifest so it round-trips
    /// into the rederived <c>binding-report.json</c> (<see cref="BindingReportProjection"/>) — a
    /// successful-but-heuristic bridge made observable, not a degradation.
    /// </summary>
    public List<string> ObjCPrefixBridges { get; init; } = new();

    public static GenerationSection From(BindingReport report, ParseReconciliation? parseReconciliation = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var section = new GenerationSection
        {
            TotalTypes = report.TotalTypes,
            EmittedTypes = report.EmittedTypes,
            SkippedTypes = report.SkippedTypes,
            TotalMembers = report.TotalMembers,
            EmittedMembers = report.EmittedMembers,
            SkippedMembers = report.SkippedMembers,
            SynthesizedMembers = report.SynthesizedMembers,
            BridgeSummary = report.BridgeSummary,
            ParseReconciliation = parseReconciliation,
        };
        foreach (var kv in report.EmittedMembersByKind)
            section.EmittedMembersByKind[kv.Key] = kv.Value;
        foreach (var kv in report.SkippedMembersByKind)
            section.SkippedMembersByKind[kv.Key] = kv.Value;
        section.SkippedItems.AddRange(report.SkippedItems);
        section.WrappedItems.AddRange(report.WrappedItems);
        section.BridgedViews.AddRange(report.BridgedViews);
        section.ThemeBridgedProperties.AddRange(report.ThemeBridgedProperties);
        section.UnsupportedCommentDrops.AddRange(report.UnsupportedCommentDrops);
        section.ObjectDegradations.AddRange(report.ObjectDegradations);
        section.ObjCPrefixBridges.AddRange(report.ObjCPrefixBridges);
        return section;
    }
}

/// <summary>
/// Emission-phase snapshot. Same data <see cref="EmissionReportEmitter"/> writes to
/// <c>binding-emission-report.json</c>, captured here so the manifest is self-contained.
/// </summary>
public sealed class EmissionSection
{
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public PhaseStatus Status { get; init; } = PhaseStatus.Success;

    public Dictionary<string, int> WrapperStrategyCounts { get; init; } = new();
    public Dictionary<string, int> SkipReasons { get; init; } = new();
    public ConformanceDecisionsSummary ConformanceDecisions { get; init; } = new();
    public List<string> SilentTombstones { get; init; } = new();

    /// <summary>
    /// Number of EveryProtocol proxy classes withheld at emission because their conformance was
    /// not emitted. Mirrors <see cref="EmissionReport.SuppressedProxyClassCount"/>; carried on the
    /// manifest so this emission-time decision is self-contained alongside the conformance summary.
    /// Replaces the retired <c>ProxyCoGatingSection.SuppressedProxyClassCount</c> — proxy
    /// suppression is now an emission decision, not a generate-then-strip co-gating result.
    /// </summary>
    public int SuppressedProxyClassCount { get; init; }

    /// <summary>
    /// Finding 14c: the Apple supplement references this module accrued during emission, each with
    /// the resolution mechanism(s) that recorded it. These are the references that drive the
    /// consumer csproj's <c>SwiftBindings.Apple</c> <c>PackageReference</c>; surfacing them with
    /// provenance turns the previously opaque <c>[ThreadStatic]</c> side-channel into an auditable
    /// manifest record.
    /// </summary>
    public List<AppleSupplementReferenceEntry> AppleSupplementReferences { get; init; } = new();

    public static EmissionSection From(
        EmissionReport report,
        IReadOnlyList<(string Identity, IReadOnlyList<string> Provenance)>? appleSupplementReferences = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var section = new EmissionSection
        {
            ConformanceDecisions = new ConformanceDecisionsSummary
            {
                EmittedInSource = report.ConformanceDecisions.EmittedInSource,
                SkippedAtEmission = report.ConformanceDecisions.SkippedAtEmission,
                Note = report.ConformanceDecisions.Note,
            },
            SuppressedProxyClassCount = report.SuppressedProxyClassCount,
        };
        foreach (var kv in report.WrapperStrategyCounts)
            section.WrapperStrategyCounts[kv.Key] = kv.Value;
        foreach (var kv in report.SkipReasons)
            section.SkipReasons[kv.Key] = kv.Value;
        section.SilentTombstones.AddRange(report.SilentTombstones);
        if (appleSupplementReferences != null)
        {
            foreach (var entry in appleSupplementReferences)
                section.AppleSupplementReferences.Add(
                    new AppleSupplementReferenceEntry(entry.Identity, entry.Provenance.ToList()));
        }
        return section;
    }
}

/// <summary>
/// Finding 14c: one Apple supplement reference accrued during emission, with the aggregated
/// provenance (caller hints) explaining why it was recorded.
/// </summary>
public sealed record AppleSupplementReferenceEntry(string Identity, List<string> Provenance);

/// <summary>
/// Finding 50: input-resolution snapshot. Captures the ordered list of decisions
/// <see cref="InputResolutionReport"/> accumulated during <see cref="XCFrameworkResolver"/>
/// resolution and dependency parsing. <see cref="PhaseStatus.Warning"/> when at least one
/// decision was a degradation (a fallback substituted a different input than requested);
/// <see cref="PhaseStatus.Success"/> when every input was found and used as-is.
/// </summary>
public sealed class InputResolutionSection
{
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public PhaseStatus Status { get; init; } = PhaseStatus.Success;

    public int DecisionCount { get; init; }
    public int DegradationCount { get; init; }
    public List<InputResolutionDecisionEntry> Decisions { get; init; } = new();

    public static InputResolutionSection From(IReadOnlyList<InputResolutionDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        var entries = new List<InputResolutionDecisionEntry>(decisions.Count);
        var degradationCount = 0;
        foreach (var decision in decisions)
        {
            if (decision.Severity == InputResolutionSeverity.Degradation)
                degradationCount++;
            entries.Add(new InputResolutionDecisionEntry(
                decision.Category, decision.Severity, decision.Detail));
        }
        return new InputResolutionSection
        {
            Status = degradationCount > 0 ? PhaseStatus.Warning : PhaseStatus.Success,
            DecisionCount = decisions.Count,
            DegradationCount = degradationCount,
            Decisions = entries,
        };
    }
}

/// <summary>
/// Finding 50: one input-resolution decision serialized onto the manifest. Mirrors
/// <see cref="InputResolutionDecision"/> as a manifest-owned record so the on-disk shape is
/// independent of the in-memory collector type.
/// </summary>
public sealed record InputResolutionDecisionEntry(
    InputResolutionCategory Category,
    InputResolutionSeverity Severity,
    string Detail);

/// <summary>
/// Wrapper-compilation-phase snapshot. Populated by both <c>RunCompileWrapperOnly</c>
/// and the in-process wrapper compile in <c>BindingsGeneratorCommand</c>.
/// </summary>
public sealed class WrapperSection
{
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public PhaseStatus Status { get; init; } = PhaseStatus.Success;

    public string? RawOutcome { get; init; }
    public string? EffectiveOutcome { get; init; }
    public int ExitCode { get; init; }
    public string? DiagnosticCode { get; init; }
    public string? Message { get; init; }

    public int SliceCount { get; init; }
    public int CompiledFileCount { get; init; }
    public int PostProcessorStrippedBlockCount { get; init; }

    /// <summary>
    /// Per-sub-cause histogram for the strips counted in
    /// <see cref="PostProcessorStrippedBlockCount"/>. Validation reporting reads this to
    /// distinguish "the new <c>Pattern2InternalTypeReach</c> emission gate caught the
    /// dominant case" from "the post-processor swept up unexpected residue."
    /// Keys mirror <see cref="StripSubCause"/> serialized as strings.
    /// </summary>
    public Dictionary<string, int> PostProcessorStrippedBlocksBySubCause { get; init; } = new();

    public bool WrapperXcfwExists { get; init; }

    /// <summary>
    /// Set of @_cdecl / @_silgen_name symbols that were stripped from the compiled wrapper.
    /// Item 3 (fail-closed CI) reads this list to gate releases.
    /// </summary>
    public List<string> StrippedSymbols { get; init; } = new();

    /// <summary>
    /// Members removed from generated C# because their wrapper P/Invoke target was stripped.
    /// Identities are stable enough to project as <see cref="SkippedItem"/> entries —
    /// duplicates are preserved (overload-correct).
    /// </summary>
    public List<CoGatedMember> CSharpCoGatedMembers { get; init; } = new();

    public static WrapperSection From(
        WrapperBuildOutcome outcome,
        IReadOnlyList<CoGatedMember> coGatedMembers)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(coGatedMembers);

        var section = new WrapperSection
        {
            Status = outcome.IsFatal
                ? PhaseStatus.Fatal
                : (outcome.IsWarning ? PhaseStatus.Warning : PhaseStatus.Success),
            RawOutcome = outcome.RawOutcome.ToString(),
            EffectiveOutcome = outcome.EffectiveOutcome.ToString(),
            ExitCode = outcome.ExitCode,
            DiagnosticCode = outcome.DiagnosticCode,
            Message = string.IsNullOrEmpty(outcome.Message) ? null : outcome.Message,
            SliceCount = outcome.CompilationResult?.SliceCount ?? 0,
            CompiledFileCount = outcome.CompilationResult?.CompiledFileCount ?? 0,
            PostProcessorStrippedBlockCount = outcome.CompilationResult?.StrippedBlockCount ?? 0,
            WrapperXcfwExists = outcome.CompilationResult?.XCFrameworkPath is { } p && Directory.Exists(p),
        };
        if (outcome.CompilationResult?.StrippedBlocksBySubCause is { } subCauses)
        {
            foreach (var (cause, count) in subCauses.OrderBy(kv => kv.Key))
                section.PostProcessorStrippedBlocksBySubCause[cause.ToString()] = count;
        }
        foreach (var symbol in outcome.StrippedSymbols.OrderBy(s => s, StringComparer.Ordinal))
            section.StrippedSymbols.Add(symbol);
        section.CSharpCoGatedMembers.AddRange(coGatedMembers);
        return section;
    }
}

/// <summary>
/// Bridge-compilation-phase snapshot. <see cref="PhaseStatus.NoOp"/> when no bridge
/// files were present (so the phase ran but produced nothing), <see cref="PhaseStatus.Success"/>
/// or <see cref="PhaseStatus.Warning"/> when compilation was attempted.
/// </summary>
public sealed class BridgeSection
{
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public PhaseStatus Status { get; init; } = PhaseStatus.NotRun;

    public string? Severity { get; init; }
    public string? DiagnosticCode { get; init; }
    public string? Message { get; init; }
    public bool BridgeCompiled { get; init; }
    public int SliceCount { get; init; }

    public static BridgeSection From(BridgeBuildOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return new BridgeSection
        {
            Status = outcome.Severity == BridgeCompilationSeverity.Warning
                ? PhaseStatus.Warning
                : PhaseStatus.Success,
            Severity = outcome.Severity.ToString(),
            DiagnosticCode = outcome.DiagnosticCode,
            Message = string.IsNullOrEmpty(outcome.Message) ? null : outcome.Message,
            BridgeCompiled = outcome.BridgeCompiled,
            SliceCount = outcome.CompilationResult?.SliceCount ?? 0,
        };
    }
}

/// <summary>
/// A1: ObjC-binding-phase snapshot. Records the symbols the ObjC pipeline dropped, already
/// projected into the shared report vocabulary (<see cref="SkippedItem"/>) so
/// <see cref="BindingReportProjection"/> can fold them straight into the rederived report's
/// <c>SkippedItems</c> / <c>SkipTriage</c> without re-touching the ObjC domain types. Present on
/// mixed and pure-ObjC manifests; absent (null) on Swift-only bindings.
/// </summary>
public sealed class ObjCSection
{
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public PhaseStatus Status { get; init; } = PhaseStatus.Success;

    /// <summary>Total symbols the ObjC pipeline dropped (== <see cref="SkippedItems"/>.Count).</summary>
    public int SkippedSymbolCount { get; init; }

    /// <summary>Drop count keyed by <see cref="SkipReason"/> name — the ObjC slice of the by-reason roll-up.</summary>
    public Dictionary<string, int> SkippedByReason { get; init; } = new();

    /// <summary>
    /// The dropped ObjC symbols as <see cref="SkippedItem"/>s (mapped by <see cref="ObjCSkipProjection"/>).
    /// Stored already-projected so the report projection is a plain append — the same shape
    /// <see cref="GenerationSection.SkippedItems"/> already round-trips through the manifest.
    /// </summary>
    public List<SkippedItem> SkippedItems { get; init; } = new();

    public static ObjCSection From(ObjCBindingDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var section = new ObjCSection
        {
            SkippedSymbolCount = diagnostics.SkippedSymbols.Count,
            Status = diagnostics.SkippedSymbols.Count == 0 ? PhaseStatus.Success : PhaseStatus.Warning,
        };
        foreach (var symbol in diagnostics.SkippedSymbols)
        {
            var item = ObjCSkipProjection.ToSkippedItem(symbol);
            section.SkippedItems.Add(item);
            var key = item.Reason.ToString();
            section.SkippedByReason[key] = section.SkippedByReason.GetValueOrDefault(key) + 1;
        }
        return section;
    }
}

/// <summary>
/// Stable identity for a member removed during co-gating. Confidence reflects whether
/// the identity is overload-stable (mangled symbol present) or heuristic (container,
/// name, kind, ordinal). Item 4 will tighten heuristic identities by adding parameter
/// labels/types — additive to this record, no breaking change.
/// </summary>
public sealed class CoGatedMember
{
    public required string Name { get; init; }
    public string? ContainingType { get; init; }
    public required BindingItemKind Kind { get; init; }
    public string? MangledSymbol { get; init; }

    /// <summary>
    /// Per-file occurrence index. Disambiguates overloads when no mangled symbol is
    /// available — projection never collapses two cogated members into one.
    /// </summary>
    public required int Ordinal { get; init; }
    public required IdentityConfidence Confidence { get; init; }
    public string? SourceFile { get; init; }
}

public enum IdentityConfidence
{
    /// <summary>Mangled wrapper symbol present — overload-stable across renames.</summary>
    Mangled,

    /// <summary>Container + name + kind + ordinal best-effort identity. Item 4 will tighten.</summary>
    Heuristic,
}
