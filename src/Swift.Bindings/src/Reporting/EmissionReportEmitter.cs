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

        return report;
    }
}
