// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Authoritative on-disk record of all binding generation artifacts for one module.
/// Written after every phase that mutates the output directory, so consumers (and the
/// rederived <see cref="BindingReport"/>) reflect what was actually shipped — not the
/// generator's mid-pipeline view.
/// </summary>
public sealed class BindingArtifactManifest
{
    public const int CurrentSchemaVersion = 1;

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
    public ProxyCoGatingSection? ProxyCoGating { get; set; }
    public WrapperSection? Wrapper { get; set; }
    public BridgeSection? Bridge { get; set; }
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
/// Generation-phase snapshot. Populated by the main pass after C# emission and after
/// the proxy co-gater runs in-process.
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

    public static GenerationSection From(BindingReport report)
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
        };
        foreach (var kv in report.EmittedMembersByKind)
            section.EmittedMembersByKind[kv.Key] = kv.Value;
        foreach (var kv in report.SkippedMembersByKind)
            section.SkippedMembersByKind[kv.Key] = kv.Value;
        section.SkippedItems.AddRange(report.SkippedItems);
        section.WrappedItems.AddRange(report.WrappedItems);
        section.BridgedViews.AddRange(report.BridgedViews);
        section.ThemeBridgedProperties.AddRange(report.ThemeBridgedProperties);
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

    public static EmissionSection From(EmissionReport report)
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
        };
        foreach (var kv in report.WrapperStrategyCounts)
            section.WrapperStrategyCounts[kv.Key] = kv.Value;
        foreach (var kv in report.SkipReasons)
            section.SkipReasons[kv.Key] = kv.Value;
        section.SilentTombstones.AddRange(report.SilentTombstones);
        return section;
    }
}

/// <summary>
/// Records the proxy-suppression co-gating pass that runs at the end of the main
/// generation phase. Each cogated method is a method body removed because the proxy
/// class it constructed was not emitted.
/// </summary>
public sealed class ProxyCoGatingSection
{
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public PhaseStatus Status { get; init; } = PhaseStatus.Success;

    public int SuppressedProxyClassCount { get; init; }
    public List<CoGatedMember> CoGatedMethods { get; init; } = new();
}

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
