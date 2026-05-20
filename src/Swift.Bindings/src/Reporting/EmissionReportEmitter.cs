// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BindingsGeneration;

/// <summary>
/// Report model for emission-level metrics (wrapper strategies, skip reasons, conformance decisions).
/// Written to binding-emission-report.json alongside binding-report.json.
/// </summary>
public class EmissionReport
{
    [JsonProperty("module")]
    public string Module { get; set; } = "";

    [JsonProperty("wrapperStrategyCounts")]
    public Dictionary<string, int> WrapperStrategyCounts { get; set; } = new();

    [JsonProperty("skipReasons")]
    public Dictionary<string, int> SkipReasons { get; set; } = new();

    [JsonProperty("conformanceDecisions")]
    public ConformanceDecisionsSummary ConformanceDecisions { get; set; } = new();

    /// <summary>
    /// Module-qualified names of types that were emitted with [OpaqueSwiftType] but have
    /// zero usable surface (all members skipped). The type IS present as a C# class
    /// declaration — metadata-cookie references to it resolve correctly — but the surface
    /// is opaque-only (the consumer can hold an instance but cannot call any methods on it).
    /// Call sites whose return type is a silent tombstone are flagged with the SB0002
    /// diagnostic so audits can grep them out.
    ///
    /// Invariant: every name here also appears in <see cref="ModuleEmissionContext.EmittedOpaqueTypes"/>.
    /// <see cref="EmissionReportEmitter.Emit"/> throws on divergence — a break would mean
    /// the registrar predicate (<c>SilentTombstoneRegistrar.WouldEmitAsOpaqueTombstone</c>)
    /// has drifted from handler reality and a metadata-cookie reference to a tombstoned
    /// type would dangle in the generated source.
    /// </summary>
    [JsonProperty("silentTombstones")]
    public List<string> SilentTombstones { get; set; } = new();

    /// <summary>
    /// Conformer pairings the CSM engine rejected at the pairing step because the
    /// conformer fails to satisfy a non-selected protocol constraint on its generic
    /// parameter. See <see cref="ConcreteSpecializationEngine.CsmRejectedPairing"/> for
    /// the mechanism. Populated from <see cref="ConcreteSpecializationEngine.RejectedPairings"/>.
    /// Sorted for deterministic output.
    /// </summary>
    [JsonProperty("csmConformerRejections")]
    public List<CsmConformerRejectionEntry> CsmConformerRejections { get; set; } = new();
}

/// <summary>
/// Serializable view of <see cref="ConcreteSpecializationEngine.CsmRejectedPairing"/>.
/// Mirrors the record fields one-for-one for stable JSON output.
/// </summary>
public class CsmConformerRejectionEntry
{
    [JsonProperty("parentType")]
    public string ParentType { get; set; } = "";

    [JsonProperty("genericParam")]
    public string GenericParam { get; set; } = "";

    [JsonProperty("selectedProtocol")]
    public string SelectedProtocol { get; set; } = "";

    [JsonProperty("conformer")]
    public string Conformer { get; set; } = "";

    [JsonProperty("missingConstraint")]
    public string MissingConstraint { get; set; } = "";

    [JsonProperty("reason")]
    public string Reason { get; set; } = "";
}

/// <summary>
/// Summary of EveryProtocol conformance emission decisions.
/// </summary>
public class ConformanceDecisionsSummary
{
    [JsonProperty("emittedInSource")]
    public int EmittedInSource { get; set; }

    [JsonProperty("skippedAtEmission")]
    public int SkippedAtEmission { get; set; }

    [JsonProperty("note")]
    public string Note { get; set; } = "Emitted conformances are stripped by post-processor Pattern 1 (unconditional EveryProtocol removal)";
}

/// <summary>
/// Emits the binding-emission-report.json file from ModuleEmissionContext data.
/// Follows the same pattern as ReportEmitter.
/// </summary>
public static class EmissionReportEmitter
{
    public static void Emit(ModuleEmissionContext emissionContext, string moduleName, string outputDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(emissionContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        AssertSilentTombstoneInvariant(emissionContext, moduleName);

        var report = BuildReport(emissionContext, moduleName);

        var reportPath = Path.Combine(outputDirectory, "binding-emission-report.json");
        var json = JsonConvert.SerializeObject(report, Formatting.Indented);
        File.WriteAllText(reportPath, json);

        // Log summary
        if (report.WrapperStrategyCounts.Count > 0)
        {
            var total = report.WrapperStrategyCounts.Values.Sum();
            logger.LogInformation("Emission: {Total} wrapper strategies assigned ({Breakdown})",
                total, string.Join(", ", report.WrapperStrategyCounts.Select(kv => $"{kv.Key}: {kv.Value}")));
        }

        if (report.SkipReasons.Count > 0)
        {
            var total = report.SkipReasons.Values.Sum();
            logger.LogInformation("Emission: {Total} methods skipped ({Breakdown})",
                total, string.Join(", ", report.SkipReasons.Select(kv => $"{kv.Key}: {kv.Value}")));
        }

        var decisions = report.ConformanceDecisions;
        if (decisions.EmittedInSource > 0 || decisions.SkippedAtEmission > 0)
        {
            logger.LogInformation("Emission: {Emitted} conformances emitted in source, {Skipped} skipped at emission",
                decisions.EmittedInSource, decisions.SkippedAtEmission);
        }

        if (report.SilentTombstones.Count > 0)
        {
            logger.LogInformation("Emission: {Count} silent tombstones (types emitted with [OpaqueSwiftType] but zero usable members)",
                report.SilentTombstones.Count);
        }

        if (report.CsmConformerRejections.Count > 0)
        {
            logger.LogInformation(
                "Emission: {Count} CSM conformer rejections (multi-constraint intersection filter)",
                report.CsmConformerRejections.Count);
        }
    }

    /// <summary>
    /// Verifies that every silent tombstone the registrar pre-pass recorded was actually
    /// emitted as a <c>[OpaqueSwiftType]</c> declaration by a handler. A break means
    /// <see cref="SilentTombstoneRegistrar.WouldEmitAsOpaqueTombstone"/> returned <c>true</c>
    /// for a type that no handler emitted — leaving any metadata-cookie reference to that
    /// type unresolved in the generated source. Throws to fail the build loudly so the
    /// drift can be diagnosed and the registrar's predicate brought back in sync.
    /// </summary>
    internal static void AssertSilentTombstoneInvariant(ModuleEmissionContext emissionContext, string moduleName)
    {
        var divergent = emissionContext.SilentTombstones
            .Where(name => !emissionContext.EmittedOpaqueTypes.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        if (divergent.Count == 0)
            return;

        throw new InvalidOperationException(
            $"Silent tombstone invariant violated in module '{moduleName}': "
            + $"{divergent.Count} type(s) registered by SilentTombstoneRegistrar were not emitted "
            + "with [OpaqueSwiftType] by any handler. Metadata-cookie references to these types "
            + "would dangle. Likely cause: an early-return predicate in "
            + "SilentTombstoneRegistrar.WouldEmitAsOpaqueTombstone is missing a case that the "
            + "handler-side opaque-emission gate excludes, or a handler suppressed emission via "
            + "a path the registrar does not model. "
            + $"Divergent types: {string.Join(", ", divergent)}");
    }

    internal static EmissionReport BuildReport(ModuleEmissionContext emissionContext, string moduleName)
    {
        var report = new EmissionReport { Module = moduleName };

        // Aggregate wrapper strategy counts from accumulated data
        foreach (var kv in emissionContext.WrapperStrategyCounts)
        {
            report.WrapperStrategyCounts[kv.Key] = kv.Value;
        }

        // Aggregate skip reasons
        foreach (var kv in emissionContext.WrapperSkipReasons)
        {
            report.SkipReasons[kv.Key] = kv.Value;
        }

        // Conformance decisions
        foreach (var kv in emissionContext.ConformanceDecisions)
        {
            if (kv.Value.Emitted)
                report.ConformanceDecisions.EmittedInSource++;
            else
                report.ConformanceDecisions.SkippedAtEmission++;
        }

        // Silent tombstones (sorted for deterministic output)
        report.SilentTombstones = emissionContext.SilentTombstones
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        // CSM conformer rejections (multi-constraint intersection filter). The engine
        // is the source of truth — it accumulates rejections across every
        // FindSpecializableMethods / ResolveParentSpecializableParams call within the
        // module's run. Sorted by (parentType, genericParam, conformer, missingConstraint)
        // for deterministic output.
        var engine = emissionContext.SpecializationEngine;
        if (engine is not null)
        {
            report.CsmConformerRejections = engine.RejectedPairings
                .Select(r => new CsmConformerRejectionEntry
                {
                    ParentType = r.ParentType,
                    GenericParam = r.GenericParamName,
                    SelectedProtocol = r.SelectedProtocol,
                    Conformer = r.ConformerSwiftType,
                    MissingConstraint = r.MissingConstraint,
                    Reason = r.Reason,
                })
                .OrderBy(e => e.ParentType, StringComparer.Ordinal)
                .ThenBy(e => e.GenericParam, StringComparer.Ordinal)
                .ThenBy(e => e.Conformer, StringComparer.Ordinal)
                .ThenBy(e => e.MissingConstraint, StringComparer.Ordinal)
                .ToList();
        }

        return report;
    }
}
