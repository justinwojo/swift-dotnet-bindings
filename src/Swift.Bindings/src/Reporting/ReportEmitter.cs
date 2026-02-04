// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BindingsGeneration;

/// <summary>
/// Emits binding report artifacts (JSON + console summary).
/// </summary>
public static class ReportEmitter
{
    public static void Emit(BindingReport report, string outputDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var reportPath = Path.Combine(outputDirectory, "binding-report.json");
        var serializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            Converters = new List<JsonConverter> { new StringEnumConverter() },
        };
        File.WriteAllText(reportPath, JsonConvert.SerializeObject(report, serializerSettings));

        var typeCoverage = GetCoverage(report.EmittedTypes, report.TotalTypes);
        var memberCoverage = GetCoverage(report.EmittedMembers, report.TotalMembers);

        logger.LogInformation("=== Binding Generation Report ===");
        logger.LogInformation("Module: {Module}", report.ModuleName);
        logger.LogInformation("Types: {Emitted} emitted, {Skipped} skipped ({Coverage:P1} coverage)", report.EmittedTypes, report.SkippedTypes, typeCoverage);
        logger.LogInformation("Members: {Emitted} emitted, {Skipped} skipped, {Synthesized} synthesized ({Coverage:P1} coverage)",
            report.EmittedMembers, report.SkippedMembers, report.SynthesizedMembers, memberCoverage);

        if (report.WrappedItems.Count > 0)
        {
            logger.LogInformation("Wrapped items: {Count} (Swift wrappers auto-generated)", report.WrappedItems.Count);
        }

        if (report.SkippedItems.Count > 0)
        {
            logger.LogInformation("Skipped items by reason:");
            foreach (var group in report.SkippedItems.GroupBy(i => i.Reason).OrderByDescending(g => g.Count()))
            {
                logger.LogInformation("  {Reason}: {Count}", group.Key, group.Count());
            }
        }

        logger.LogInformation("Full details in: {ReportPath}", reportPath);
    }

    private static double GetCoverage(int emitted, int total) =>
        total == 0 ? 1.0 : (double)emitted / total;
}
